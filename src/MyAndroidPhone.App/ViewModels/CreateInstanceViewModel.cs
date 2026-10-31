using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Legal;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed record NoticeViewModel(string Component, string License, Uri Link, string LinkText, string Note);

public sealed record IconChoice(string Name, string Glyph);

public sealed record AccentChoice(string Name, string Hex)
{
    public Brush Brush => ColorHelper.Brush(Hex);
}

/// <summary>
/// The pop-up wizard for creating an instance: 1) accept the license notice, 2) connect a phone over USB or the
/// network, 3) name it and pick an icon, accent color and theme. In accept-only mode (the notices changed since the
/// user last accepted) it shows just the first step.
/// </summary>
public sealed partial class CreateInstanceViewModel : ObservableObject, IDisposable
{
    private static readonly string[] StepTitles = { "License and security notice", "Connect your phone", "Make it yours" };

    private readonly IDeviceService _devices;
    private readonly IInstanceService _instances;
    private readonly ISetupStateService _setup;
    private readonly IToolService _tools;
    private readonly ToolActions _toolActions;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService _notifications;
    private readonly ILogger<CreateInstanceViewModel> _logger;

    public CreateInstanceViewModel(
        IDeviceService devices,
        IInstanceService instances,
        ISetupStateService setup,
        IToolService tools,
        ToolActions toolActions,
        IUiDispatcher ui,
        INotificationService notifications,
        ILogger<CreateInstanceViewModel> logger)
    {
        _devices = devices;
        _instances = instances;
        _setup = setup;
        _tools = tools;
        _toolActions = toolActions;
        _ui = ui;
        _notifications = notifications;
        _logger = logger;

        foreach (var notice in LegalNotices.ThirdParty)
        {
            Notices.Add(new NoticeViewModel(notice.Component, notice.License, new Uri(notice.Url), notice.Url, notice.Note));
        }

        _tools.Changed += OnToolsChanged;
        _devices.Changed += OnDevicesChanged;
        _themeIndex = 0;
        RebuildAdb();
        RebuildDevices();
    }

    public event EventHandler<AndroidInstance>? Created;

    public event EventHandler? Cancelled;

    public event EventHandler? LicenseAccepted;

    public bool AcceptOnly { get; private set; }

    /// <summary>Shows only the license step; used when the notices changed since the user last accepted.</summary>
    public void BeginAcceptOnly()
    {
        AcceptOnly = true;
        OnPropertyChanged(nameof(StepCount));
        OnPropertyChanged(nameof(CanGoBack));
    }

    public string SecurityNotice => LegalNotices.SecurityNotice;

    public string ApplicationLicenseStatement => LegalNotices.ApplicationLicenseStatement;

    public ObservableCollection<NoticeViewModel> Notices { get; } = new();

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    public IReadOnlyList<IconChoice> IconChoices { get; } = new[]
    {
        new IconChoice("Phone", ""),
        new IconChoice("Tablet", ""),
        new IconChoice("Monitor", ""),
        new IconChoice("Game controller", ""),
        new IconChoice("Home", "")
    };

    public IReadOnlyList<AccentChoice> AccentChoices { get; } = new[]
    {
        new AccentChoice("Blue", "#3B82F6"),
        new AccentChoice("Green", "#22C55E"),
        new AccentChoice("Orange", "#F59E0B"),
        new AccentChoice("Red", "#EF4444"),
        new AccentChoice("Purple", "#A855F7"),
        new AccentChoice("Pink", "#EC4899"),
        new AccentChoice("Gray", "#9CA3AF")
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(StepNumber), nameof(StepCount), nameof(ShowAccept), nameof(ShowNext), nameof(ShowCreate), nameof(CanGoBack), nameof(CanGoNext))]
    private int _stepIndex;

    // ----- step 2: connect -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsbMode), nameof(IsNetworkMode))]
    private int _modeIndex;

    [ObservableProperty]
    private ToolItemViewModel? _adbItem;

    [ObservableProperty]
    private bool _hasAdbProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string _pairEndpoint = string.Empty;

    [ObservableProperty]
    private string _pairCode = string.Empty;

    [ObservableProperty]
    private string _connectStatus = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _deviceListHint = string.Empty;

    // ----- step 3: personalize -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewGlyph))]
    private int _iconIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBrush))]
    private int _accentIndex;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool IsUsbMode => ModeIndex == 0;

    public bool IsNetworkMode => ModeIndex == 1;

    public bool HasError => ErrorText.Length > 0;

    public string StepTitle => StepTitles[StepIndex];

    public double StepNumber => StepIndex + 1;

    public double StepCount => AcceptOnly ? 1 : StepTitles.Length;

    public bool ShowAccept => StepIndex == 0;

    public bool ShowNext => StepIndex == 1;

    public bool ShowCreate => StepIndex == 2;

    public bool CanGoBack => StepIndex > 0 && !AcceptOnly;

    public bool CanGoNext => StepIndex == 1 && SelectedDevice is { Device.IsOnline: true };

    public string PreviewName => string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name.Trim();

    public string PreviewGlyph => IconChoices[Math.Clamp(IconIndex, 0, IconChoices.Count - 1)].Glyph;

    public Brush PreviewBrush => AccentChoices[Math.Clamp(AccentIndex, 0, AccentChoices.Count - 1)].Brush;

    partial void OnModeIndexChanged(int value)
    {
        SelectedDevice = null;
        ConnectStatus = string.Empty;
        RebuildDevices();
    }

    // ----- navigation -----

    [RelayCommand]
    private async Task AcceptAsync()
    {
        try
        {
            await _setup.UpdateAsync(s =>
            {
                s.AcceptedNoticesVersion = LegalNotices.Version;
                s.AcceptedAt = DateTimeOffset.Now;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record acceptance of the notices.");
            _notifications.Post(NotificationSeverity.Error, "Could not save your acceptance", ex.Message);
            return;
        }

        if (AcceptOnly)
        {
            LicenseAccepted?.Invoke(this, EventArgs.Empty);
            return;
        }

        StepIndex = 1;
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext || SelectedDevice is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = SelectedDevice.Name;
        }

        IconIndex = Math.Max(0, IconChoices.ToList().FindIndex(i => i.Glyph == SelectedDevice.IconGlyph));
        StepIndex = 2;
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0)
        {
            ErrorText = string.Empty;
            StepIndex--;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    // ----- connect -----

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            await _devices.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device refresh failed in the create wizard.");
            ConnectStatus = "Could not check for devices: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PairAsync()
    {
        IsBusy = true;
        ConnectStatus = "Pairing...";
        try
        {
            var result = await _devices.PairAsync(PairEndpoint, PairCode);
            ConnectStatus = result.Succeeded ? result.Value : result.ErrorMessage;
            if (result.Succeeded)
            {
                PairCode = string.Empty;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        ConnectStatus = "Connecting...";
        try
        {
            var result = await _devices.ConnectAsync(Endpoint);
            ConnectStatus = result.Succeeded ? result.Value : result.ErrorMessage;
            if (result.Succeeded)
            {
                await _devices.RefreshAsync();
                var match = Devices.FirstOrDefault(d => d.Device.IsOnline && d.Serial == Endpoint.Trim())
                            ?? Devices.FirstOrDefault(d => d.Device.IsOnline);
                if (match is not null)
                {
                    SelectedDevice = match;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task LocateAdbAsync() => _toolActions.LocateAsync(ToolId.Adb);

    // ----- create -----

    [RelayCommand]
    private async Task CreateAsync()
    {
        ErrorText = string.Empty;
        if (SelectedDevice is null || !SelectedDevice.Device.IsOnline)
        {
            ErrorText = "The phone is no longer connected. Go back and connect it again.";
            return;
        }

        var device = SelectedDevice.Device;
        var icon = IconChoices[Math.Clamp(IconIndex, 0, IconChoices.Count - 1)].Glyph;
        var accent = AccentChoices[Math.Clamp(AccentIndex, 0, AccentChoices.Count - 1)].Hex;
        var endpoint = device.ConnectionType == DeviceConnectionType.Wireless
            ? (IsNetworkMode && !string.IsNullOrWhiteSpace(Endpoint) && device.Serial == Endpoint.Trim() ? Endpoint.Trim() : LooksLikeEndpoint(device.Serial) ? device.Serial : null)
            : null;

        var draft = new AndroidInstance
        {
            Name = Name,
            IconGlyph = icon,
            AccentColor = accent,
            Theme = ThemeIndex switch { 1 => AppTheme.Light, 2 => AppTheme.System, _ => AppTheme.Dark },
            DeviceSerial = device.Serial,
            ConnectionType = device.ConnectionType,
            Endpoint = endpoint,
            DeviceModel = device.DisplayName
        };

        IsBusy = true;
        try
        {
            var result = await _instances.CreateAsync(draft);
            if (!result.Succeeded)
            {
                ErrorText = result.ErrorMessage;
                return;
            }

            // Keep Device Center's saved name, icon and color in step with the instance.
            await _devices.UpdatePreferencesAsync(device.Serial, result.Value.Name, icon, accent);
            Created?.Invoke(this, result.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating an instance failed.");
            ErrorText = "Could not save the instance: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool LooksLikeEndpoint(string serial) => serial.Contains(':') && char.IsDigit(serial[0]);

    // ----- live state -----

    private void OnToolsChanged(object? sender, EventArgs e) => _ui.Post(RebuildAdb);

    private void RebuildAdb()
    {
        var adb = _tools.Installations.FirstOrDefault(i => i.Definition.Id == ToolId.Adb);
        HasAdbProblem = adb is null || !adb.IsUsable;
        AdbItem = adb is null ? null : new ToolItemViewModel(adb, _toolActions);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(RebuildDevices);

    private void RebuildDevices()
    {
        var keep = SelectedDevice?.Serial;
        Devices.Clear();
        foreach (var device in _devices.Devices.Where(d => IsUsbMode
                     ? d.ConnectionType is DeviceConnectionType.Usb or DeviceConnectionType.Emulator
                     : d.ConnectionType == DeviceConnectionType.Wireless))
        {
            Devices.Add(new DeviceItemViewModel(device));
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Serial == keep && d.Device.IsOnline);
        DeviceListHint = Devices.Count == 0
            ? (IsUsbMode ? "No phone found on USB yet. " + _devices.StatusMessage : "No phone is connected over the network yet.")
            : string.Empty;
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void Dispose()
    {
        _tools.Changed -= OnToolsChanged;
        _devices.Changed -= OnDevicesChanged;
    }
}
