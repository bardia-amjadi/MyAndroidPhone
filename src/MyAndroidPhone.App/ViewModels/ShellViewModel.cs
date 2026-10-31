using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Features;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed partial class DockItemViewModel : ObservableObject
{
    private readonly Action<string> _open;

    public DockItemViewModel(FeatureDescriptor descriptor, Action<string> open, bool showLabel)
    {
        Descriptor = descriptor;
        _open = open;
        _showLabel = showLabel;
    }

    public FeatureDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string IconGlyph => Descriptor.IconGlyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorOpacity))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorOpacity))]
    private bool _isMinimized;

    [ObservableProperty]
    private bool _showLabel;

    /// <summary>Solid dot: window is showing. Faint dot: minimized. No dot: closed.</summary>
    public double IndicatorOpacity => !IsOpen ? 0 : IsMinimized ? 0.4 : 1;

    [RelayCommand]
    private void Open() => _open(Id);
}

/// <summary>
/// State of one instance's desktop: which instance and phone it controls, the phone's connection status, the Dock and
/// the instance theme. The desktop is always bound to exactly one device serial.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IFeatureRegistry _features;
    private readonly IToolService _tools;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDeviceService _devices;
    private readonly IInstanceService _instances;
    private readonly INotificationService _notifications;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly ActiveInstance _active;
    private AndroidInstance? _instance;

    public ShellViewModel(
        IFeatureRegistry features,
        IToolService tools,
        ISettingsService settings,
        IDeviceService devices,
        IInstanceService instances,
        INotificationService notifications,
        ActiveInstance active,
        IUiDispatcher ui,
        ILogger<ShellViewModel> logger)
    {
        _features = features;
        _tools = tools;
        _settings = settings;
        _devices = devices;
        _instances = instances;
        _notifications = notifications;
        _ui = ui;
        _logger = logger;
        _active = active;

        foreach (var feature in features.All.Where(f => f.ShowInDock))
        {
            DockItems.Add(new DockItemViewModel(feature, RequestOpen, settings.Current.ShowDockLabels));
        }

        _toolSummary = BuildToolSummary();
        _tools.Changed += OnStateChanged;
        _settings.Changed += OnStateChanged;
        _devices.Changed += OnDevicesChanged;
    }

    public ObservableCollection<DockItemViewModel> DockItems { get; } = new();

    public IReadOnlyList<FeatureDescriptor> Features => _features.All;

    public AndroidInstance? Instance => _instance;

    public event EventHandler<string>? OpenFeatureRequested;

    /// <summary>The user chose to go back to the start screen.</summary>
    public event EventHandler? CloseInstanceRequested;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private string _instanceGlyph = "";

    [ObservableProperty]
    private Brush? _accentBrush;

    [ObservableProperty]
    private string _deviceStatus = string.Empty;

    [ObservableProperty]
    private bool _isDeviceReady;

    [ObservableProperty]
    private bool _isReconnecting;

    [ObservableProperty]
    private string _toolSummary;

    [ObservableProperty]
    private AppTheme _theme;

    public void Initialize(AndroidInstance instance)
    {
        _instance = instance;
        _active.Set(instance);
        InstanceName = instance.Name;
        InstanceGlyph = instance.IconGlyph;
        AccentBrush = ColorHelper.Brush(instance.AccentColor);
        Theme = instance.Theme;
        _ = StartAsync(instance);
    }

    private async Task StartAsync(AndroidInstance instance)
    {
        try
        {
            await _instances.MarkOpenedAsync(instance.Id);
            await _devices.SelectAsync(instance.DeviceSerial);
            UpdateStatus();
            var device = _devices.Devices.FirstOrDefault(d => d.Serial == instance.DeviceSerial);
            if (device is not { IsOnline: true })
            {
                await ReconnectAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening an instance failed.");
            _notifications.Post(NotificationSeverity.Error, "Could not open the instance", ex.Message);
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(UpdateStatus);

    private void UpdateStatus()
    {
        if (_instance is null)
        {
            return;
        }

        var device = _devices.Devices.FirstOrDefault(d => d.Serial == _instance.DeviceSerial);
        IsDeviceReady = device is { IsOnline: true };
        if (device is null)
        {
            DeviceStatus = $"{_instance.Name} is not connected. Plug in the phone or choose Reconnect.";
            return;
        }

        if (device.IsOnline)
        {
            var battery = device.Hardware?.Battery?.Level;
            DeviceStatus = $"{device.DisplayName} - {device.ConnectionType} - ready" + (battery is null ? string.Empty : $" - battery {battery}%");
            return;
        }

        DeviceStatus = device.Guidance ?? $"{device.DisplayName} - {device.State}";
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (_instance is null || IsReconnecting)
        {
            return;
        }

        IsReconnecting = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_instance.Endpoint))
            {
                DeviceStatus = $"Reconnecting to {_instance.Endpoint}...";
                var result = await _devices.ConnectAsync(_instance.Endpoint);
                if (!result.Succeeded)
                {
                    _notifications.Post(NotificationSeverity.Warning, "Could not reconnect", result.ErrorMessage);
                }
            }

            await _devices.RefreshAsync();
            UpdateStatus();
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    public void RequestOpen(string featureId) => OpenFeatureRequested?.Invoke(this, featureId);

    public void RequestCloseInstance() => CloseInstanceRequested?.Invoke(this, EventArgs.Empty);

    public async Task SetThemeAsync(AppTheme theme)
    {
        Theme = theme;
        if (_instance is null)
        {
            return;
        }

        var result = await _instances.UpdateAsync(_instance.Id, i => i.Theme = theme);
        if (!result.Succeeded)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not save the theme", result.ErrorMessage);
        }
    }

    public void SetOpenWindows(IReadOnlySet<string> openFeatureIds, IReadOnlySet<string> minimizedFeatureIds)
    {
        foreach (var item in DockItems)
        {
            item.IsOpen = openFeatureIds.Contains(item.Id);
            item.IsMinimized = minimizedFeatureIds.Contains(item.Id);
        }
    }

    public void Dispose()
    {
        _active.Set(null);
        _tools.Changed -= OnStateChanged;
        _settings.Changed -= OnStateChanged;
        _devices.Changed -= OnDevicesChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        ToolSummary = BuildToolSummary();
        foreach (var item in DockItems)
        {
            item.ShowLabel = _settings.Current.ShowDockLabels;
        }
    });

    private string BuildToolSummary()
    {
        var parts = _tools.Installations.Select(i => i.Status switch
        {
            ToolStatus.Found => $"{i.Definition.ShortName} {i.Version}",
            ToolStatus.Invalid => $"{i.Definition.ShortName} needs attention",
            ToolStatus.NotFound => $"{i.Definition.ShortName} not found",
            _ => $"{i.Definition.ShortName} checking..."
        });
        return string.Join("   -   ", parts);
    }
}
