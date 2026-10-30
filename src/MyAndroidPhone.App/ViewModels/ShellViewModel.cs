using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Features;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
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
    private bool _isOpen;

    [ObservableProperty]
    private bool _showLabel;

    [RelayCommand]
    private void Open() => _open(Id);
}

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IFeatureRegistry _features;
    private readonly IToolService _tools;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDeviceService _deviceService;
    private bool _syncingDevices;

    public ShellViewModel(IFeatureRegistry features, IToolService tools, ISettingsService settings, IDeviceService deviceService, IUiDispatcher ui)
    {
        _deviceService = deviceService;
        _features = features;
        _tools = tools;
        _settings = settings;
        _ui = ui;

        foreach (var feature in features.All.Where(f => f.ShowInDock))
        {
            DockItems.Add(new DockItemViewModel(feature, RequestOpen, settings.Current.ShowDockLabels));
        }

        _theme = settings.Current.Theme;
        _toolSummary = BuildToolSummary();
        _tools.Changed += OnStateChanged;
        _settings.Changed += OnStateChanged;
        _deviceService.Changed += OnDevicesChanged;
        _deviceStatus = string.Empty;
        RebuildDevices();
    }

    public ObservableCollection<DockItemViewModel> DockItems { get; } = new();

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    [ObservableProperty]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private string _deviceStatus;

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        if (!_syncingDevices && value is not null)
        {
            _ = _deviceService.SelectAsync(value.Serial);
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(RebuildDevices);

    private void RebuildDevices()
    {
        var wanted = _deviceService.SelectedDeviceId;
        _syncingDevices = true;
        try
        {
            Devices.Clear();
            foreach (var device in _deviceService.Devices)
            {
                Devices.Add(new DeviceItemViewModel(device));
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Serial == wanted);
        }
        finally
        {
            _syncingDevices = false;
        }

        var ready = Devices.Count(d => d.Device.IsOnline);
        DeviceStatus = Devices.Count == 0
            ? _deviceService.StatusMessage
            : SelectedDevice is { } selected && selected.HasGuidance
                ? selected.Guidance
                : $"{ready} of {Devices.Count} device(s) ready";
    }

    public IReadOnlyList<FeatureDescriptor> Features => _features.All;

    public event EventHandler<string>? OpenFeatureRequested;

    public event EventHandler? SetupRerunRequested;

    [ObservableProperty]
    private string _toolSummary;

    [ObservableProperty]
    private AppTheme _theme;

    public void RequestOpen(string featureId) => OpenFeatureRequested?.Invoke(this, featureId);

    public void RequestSetupRerun() => SetupRerunRequested?.Invoke(this, EventArgs.Empty);

    public Task SetThemeAsync(AppTheme theme) => _settings.UpdateAsync(s => s.Theme = theme);

    public void SetOpenWindows(IReadOnlySet<string> openFeatureIds)
    {
        foreach (var item in DockItems)
        {
            item.IsOpen = openFeatureIds.Contains(item.Id);
        }
    }

    public void Dispose()
    {
        _tools.Changed -= OnStateChanged;
        _settings.Changed -= OnStateChanged;
        _deviceService.Changed -= OnDevicesChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        ToolSummary = BuildToolSummary();
        Theme = _settings.Current.Theme;
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
            _ => $"{i.Definition.ShortName} checking…"
        });
        return string.Join("   ·   ", parts);
    }
}
