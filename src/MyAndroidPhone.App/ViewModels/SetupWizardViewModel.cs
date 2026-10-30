using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Legal;
using MyAndroidPhone.Core.Platform;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed record NoticeViewModel(string Component, string License, Uri Link, string LinkText, string Note);

/// <summary>
/// First-run setup: Welcome, Android tools, Legal notice, Finish. The current step is persisted after every change so an
/// interrupted setup resumes where it stopped. A "connect a device" step is added together with device discovery.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IDisposable
{
    private const int LastStep = (int)SetupStep.Finish;

    private static readonly string[] StepTitles =
    {
        "Welcome",
        "Android tools",
        "License and security notice",
        "Connect a device",
        "All set"
    };

    private readonly ISetupStateService _setup;
    private readonly IToolService _tools;
    private readonly ToolActions _actions;
    private readonly IDeviceService _deviceService;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService _notifications;
    private readonly ILogger<SetupWizardViewModel> _logger;
    private readonly bool _initialized;

    public SetupWizardViewModel(
        ISetupStateService setup,
        IToolService tools,
        ToolActions actions,
        IDeviceService deviceService,
        ISystemCompatibilityService compatibility,
        IUiDispatcher ui,
        INotificationService notifications,
        ILogger<SetupWizardViewModel> logger)
    {
        _setup = setup;
        _tools = tools;
        _actions = actions;
        _deviceService = deviceService;
        _ui = ui;
        _notifications = notifications;
        _logger = logger;

        var report = compatibility.Check();
        CompatibilityText = $"{report.OsDescription}, {report.Architecture}";
        HasCompatibilityIssues = report.Issues.Count > 0 || report.Warnings.Count > 0;
        CompatibilityIssuesText = string.Join(Environment.NewLine, report.Issues.Concat(report.Warnings));

        foreach (var notice in LegalNotices.ThirdParty)
        {
            Notices.Add(new NoticeViewModel(notice.Component, notice.License, new Uri(notice.Url), notice.Url, notice.Note));
        }

        // A user who finished setup earlier but must re-accept updated notices goes straight to the legal step.
        var state = setup.Current;
        _stepIndex = state.Completed ? (int)SetupStep.Legal : Math.Clamp((int)state.CurrentStep, 0, LastStep);

        _tools.Changed += OnToolsChanged;
        _deviceService.Changed += OnDevicesChanged;
        _deviceStatus = string.Empty;
        RebuildTools();
        RebuildDevices();
        _initialized = true;

        if (_stepIndex == (int)SetupStep.Dependencies)
        {
            _ = RescanAsync();
        }
    }

    public event EventHandler? Completed;

    public ObservableCollection<ToolItemViewModel> ToolItems { get; } = new();

    public ObservableCollection<NoticeViewModel> Notices { get; } = new();

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    [ObservableProperty]
    private string _deviceStatus;

    public string CompatibilityText { get; }

    public bool HasCompatibilityIssues { get; }

    public string CompatibilityIssuesText { get; }

    public string SecurityNotice => LegalNotices.SecurityNotice;

    public string ApplicationLicenseStatement => LegalNotices.ApplicationLicenseStatement;

    public double StepCount => StepTitles.Length;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(StepNumber), nameof(CanGoBack), nameof(ShowNext), nameof(ShowAccept), nameof(ShowFinish))]
    private int _stepIndex;

    [ObservableProperty]
    private bool _isScanning;

    public string StepTitle => StepTitles[StepIndex];

    public double StepNumber => StepIndex + 1;

    public bool CanGoBack => StepIndex > 0;

    public bool ShowNext => StepIndex < (int)SetupStep.Legal || StepIndex == (int)SetupStep.Device;

    public bool ShowAccept => StepIndex == (int)SetupStep.Legal;

    public bool ShowFinish => StepIndex == LastStep;

    public string FinishSummary
    {
        get
        {
            var lines = _tools.Installations.Select(i => i.Status switch
            {
                ToolStatus.Found => $"{i.Definition.ShortName}: ready ({i.Version})",
                ToolStatus.Invalid => $"{i.Definition.ShortName}: needs attention",
                _ => $"{i.Definition.ShortName}: not found - features that need it will tell you what is missing"
            });
            var ready = _deviceService.Devices.Count(d => d.IsOnline);
            var deviceLine = ready > 0
                ? $"Devices: {ready} ready"
                : "Devices: none connected yet - connect one any time from Device Center";
            return string.Join(Environment.NewLine, lines.Append(deviceLine));
        }
    }

    partial void OnStepIndexChanged(int value)
    {
        if (!_initialized)
        {
            return;
        }

        OnPropertyChanged(nameof(FinishSummary));
        _ = SaveStepAsync(value);
        if (value == (int)SetupStep.Dependencies)
        {
            _ = RescanAsync();
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (StepIndex < LastStep)
        {
            StepIndex++;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        try
        {
            await _setup.UpdateAsync(s =>
            {
                s.AcceptedNoticesVersion = LegalNotices.Version;
                s.AcceptedAt = DateTimeOffset.Now;
                s.CurrentStep = SetupStep.Device;
            });
            StepIndex = (int)SetupStep.Device;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record acceptance of the notices.");
            _notifications.Post(NotificationSeverity.Error, "Could not save your acceptance", ex.Message);
        }
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        try
        {
            await _setup.UpdateAsync(s =>
            {
                s.Completed = true;
                s.CurrentStep = SetupStep.Finish;
            });
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not complete setup.");
            _notifications.Post(NotificationSeverity.Error, "Could not finish setup", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            await _tools.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool detection failed during setup.");
            _notifications.Post(NotificationSeverity.Error, "Tool detection failed", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            await _deviceService.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device refresh failed during setup.");
            _notifications.Post(NotificationSeverity.Error, "Could not check for devices", ex.Message);
        }
    }

    public void Dispose()
    {
        _tools.Changed -= OnToolsChanged;
        _deviceService.Changed -= OnDevicesChanged;
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        RebuildDevices();
        OnPropertyChanged(nameof(FinishSummary));
    });

    private void RebuildDevices()
    {
        Devices.Clear();
        foreach (var device in _deviceService.Devices)
        {
            Devices.Add(new DeviceItemViewModel(device));
        }

        DeviceStatus = Devices.Count == 0 ? _deviceService.StatusMessage : $"{Devices.Count(d => d.Device.IsOnline)} of {Devices.Count} device(s) ready";
    }

    private async Task SaveStepAsync(int step)
    {
        try
        {
            await _setup.UpdateAsync(s => s.CurrentStep = (SetupStep)step);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist setup progress.");
        }
    }

    private void OnToolsChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        RebuildTools();
        OnPropertyChanged(nameof(FinishSummary));
    });

    private void RebuildTools()
    {
        ToolItems.Clear();
        foreach (var installation in _tools.Installations)
        {
            ToolItems.Add(new ToolItemViewModel(installation, _actions));
        }
    }
}
