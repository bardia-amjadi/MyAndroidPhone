using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed record ChoiceOption(string Name, string Value);

/// <summary>Presentation of one device. Immutable: a new instance is created whenever the device changes.</summary>
public sealed class DeviceItemViewModel
{
    private Brush? _accentBrush;

    public DeviceItemViewModel(AndroidDevice device)
    {
        Device = device;
    }

    public AndroidDevice Device { get; }

    public string Serial => Device.Serial;

    public string Name => Device.DisplayName;

    public string IconGlyph => Device.IconGlyph;

    public string Label => $"{Device.DisplayName}  ({StateText})";

    public string StateText => Device.State switch
    {
        DeviceConnectionState.Online => "Ready",
        DeviceConnectionState.Offline => "Offline",
        DeviceConnectionState.Unauthorized => "Needs authorization",
        DeviceConnectionState.Authorizing => "Waiting for approval",
        DeviceConnectionState.NoPermissions => "No permission",
        DeviceConnectionState.Connecting => "Connecting",
        DeviceConnectionState.Recovery => "Recovery mode",
        DeviceConnectionState.Sideload => "Sideload mode",
        DeviceConnectionState.Bootloader => "Bootloader",
        _ => "Not connected"
    };

    public string ConnectionText => Device.ConnectionType switch
    {
        DeviceConnectionType.Usb => "USB",
        DeviceConnectionType.Wireless => "Wi-Fi",
        DeviceConnectionType.Emulator => "Emulator",
        _ => "Unknown connection"
    };

    public string Summary => $"{ConnectionText} · {StateText}";

    public Brush AccentBrush => _accentBrush ??= new SolidColorBrush(ParseColor(Device.AccentColor));

    public string Guidance => Device.Guidance ?? string.Empty;

    public bool HasGuidance => Device.Guidance is not null;

    public bool CanDisconnect => Device.ConnectionType == DeviceConnectionType.Wireless && Device.State != DeviceConnectionState.Disconnected;

    public bool CanSwitchToWifi => Device.IsOnline && Device.ConnectionType == DeviceConnectionType.Usb;

    public string Details
    {
        get
        {
            var lines = new StringBuilder();
            lines.AppendLine($"Identifier: {Device.Serial}");
            lines.AppendLine($"Connection: {ConnectionText}" + (Device.UsbPort is null ? string.Empty : $" (port {Device.UsbPort})"));
            lines.AppendLine($"State: {StateText}");
            var hw = Device.Hardware;
            if (hw is not null)
            {
                if (hw.Model is not null)
                {
                    lines.AppendLine($"Model: {hw.Manufacturer} {hw.Model}".Replace("  ", " ").TrimEnd());
                }

                if (hw.AndroidVersion is not null)
                {
                    lines.AppendLine($"Android: {hw.AndroidVersion}" + (hw.ApiLevel is null ? string.Empty : $" (API {hw.ApiLevel})"));
                }

                if (hw.BuildId is not null)
                {
                    lines.AppendLine($"Build: {hw.BuildId}");
                }

                if (hw.CpuAbi is not null)
                {
                    lines.AppendLine($"CPU: {hw.CpuAbi}");
                }

                if (hw.ScreenWidth is not null && hw.ScreenHeight is not null)
                {
                    lines.AppendLine($"Screen: {hw.ScreenWidth} × {hw.ScreenHeight}" + (hw.DensityDpi is null ? string.Empty : $" at {hw.DensityDpi} dpi"));
                }

                if (hw.Battery is { } battery)
                {
                    var parts = new List<string>();
                    if (battery.Level is not null)
                    {
                        parts.Add($"{battery.Level}%");
                    }

                    parts.Add(battery.Status);
                    parts.Add(battery.PowerSource == "Battery" ? "on battery" : battery.PowerSource + " power");
                    if (battery.TemperatureCelsius is not null)
                    {
                        parts.Add(battery.TemperatureCelsius.Value.ToString("0.0", CultureInfo.CurrentCulture) + " °C");
                    }

                    lines.AppendLine("Battery: " + string.Join(" · ", parts));
                }
            }
            else if (Device.IsOnline)
            {
                lines.AppendLine("Reading device details…");
            }
            else if (Device.AdbModel is not null)
            {
                lines.AppendLine($"Reported model: {Device.AdbModel}");
            }

            if (Device.LastConnected is { } last)
            {
                lines.AppendLine($"Last connected: {last.LocalDateTime:g}");
            }

            return lines.ToString().TrimEnd();
        }
    }

    public string CapabilitiesText => string.Join(
        Environment.NewLine,
        Device.Capabilities.Select(c => $"{(c.Supported ? "✓" : "✗")} {c.Name} - {c.Reason}"));

    private static Windows.UI.Color ParseColor(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        return Windows.UI.Color.FromArgb(255, 156, 163, 175);
    }
}

public sealed partial class DeviceCenterViewModel : ObservableObject, IDisposable
{
    private const int DefaultTcpPort = 5555;

    private readonly IDeviceService _devices;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _ui;
    private bool _rebuilding;
    private string? _editingSerial;

    public DeviceCenterViewModel(IDeviceService devices, INotificationService notifications, IUiDispatcher ui)
    {
        _devices = devices;
        _notifications = notifications;
        _ui = ui;
        _devices.Changed += OnDevicesChanged;
        Rebuild();
        _ = _devices.RefreshAsync();
    }

    public IReadOnlyList<ChoiceOption> IconOptions { get; } = new[]
    {
        new ChoiceOption("Phone", ""),
        new ChoiceOption("Tablet", ""),
        new ChoiceOption("Monitor", ""),
        new ChoiceOption("Game controller", ""),
        new ChoiceOption("Home", "")
    };

    public IReadOnlyList<ChoiceOption> ColorOptions { get; } = new[]
    {
        new ChoiceOption("Blue", "#3B82F6"),
        new ChoiceOption("Green", "#22C55E"),
        new ChoiceOption("Orange", "#F59E0B"),
        new ChoiceOption("Red", "#EF4444"),
        new ChoiceOption("Purple", "#A855F7"),
        new ChoiceOption("Pink", "#EC4899"),
        new ChoiceOption("Gray", "#9CA3AF")
    };

    public ObservableCollection<DeviceItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedDetails), nameof(SelectedGuidance),
        nameof(SelectedHasGuidance), nameof(SelectedCapabilities), nameof(SelectedCanSwitchToWifi), nameof(SelectedCanDisconnect))]
    private DeviceItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private int _editIconIndex;

    [ObservableProperty]
    private int _editColorIndex;

    [ObservableProperty]
    private string _pairEndpoint = string.Empty;

    [ObservableProperty]
    private string _pairCode = string.Empty;

    [ObservableProperty]
    private string _connectEndpoint = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public bool HasSelection => SelectedItem is not null;

    public string SelectedTitle => SelectedItem?.Name ?? string.Empty;

    public string SelectedDetails => SelectedItem?.Details ?? string.Empty;

    public string SelectedGuidance => SelectedItem?.Guidance ?? string.Empty;

    public bool SelectedHasGuidance => SelectedItem?.HasGuidance ?? false;

    public string SelectedCapabilities => SelectedItem?.CapabilitiesText ?? string.Empty;

    public bool SelectedCanSwitchToWifi => SelectedItem?.CanSwitchToWifi ?? false;

    public bool SelectedCanDisconnect => SelectedItem?.CanDisconnect ?? false;

    partial void OnSelectedItemChanged(DeviceItemViewModel? value)
    {
        if (_rebuilding)
        {
            return;
        }

        LoadEditFields(value);
        if (value is not null)
        {
            _ = _devices.SelectAsync(value.Serial);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync("Refresh failed", async () =>
    {
        await _devices.RefreshAsync();
        return OperationResult<string>.Success(string.Empty);
    }, "Device list refreshed", quiet: true);

    [RelayCommand]
    private Task RestartServerAsync() => RunAsync("Could not restart adb", () => _devices.RestartAdbServerAsync(), "adb restarted");

    [RelayCommand]
    private Task SaveAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var icon = IconOptions[Math.Clamp(EditIconIndex, 0, IconOptions.Count - 1)].Value;
        var color = ColorOptions[Math.Clamp(EditColorIndex, 0, ColorOptions.Count - 1)].Value;
        return RunAsync("Could not save", () => _devices.UpdatePreferencesAsync(item.Serial, EditName, icon, color), "Saved");
    }

    [RelayCommand]
    private async Task ForgetAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        await _devices.ForgetAsync(item.Serial);
        _notifications.Post(NotificationSeverity.Information, "Settings forgotten", "The saved name, icon and color for this device were removed.");
    }

    [RelayCommand]
    private Task DisconnectAsync()
    {
        var item = SelectedItem;
        return item is null
            ? Task.CompletedTask
            : RunAsync("Could not disconnect", () => _devices.DisconnectAsync(item.Serial), "Disconnected");
    }

    [RelayCommand]
    private async Task SwitchToWifiAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _devices.EnableTcpIpAsync(item.Serial, DefaultTcpPort);
            if (!result.Succeeded)
            {
                _notifications.Post(NotificationSeverity.Error, "Could not switch to Wi-Fi", result.ErrorMessage);
                return;
            }

            if (result.Value.Endpoint is not null)
            {
                ConnectEndpoint = result.Value.Endpoint;
            }

            _notifications.Post(
                result.Value.Endpoint is null ? NotificationSeverity.Warning : NotificationSeverity.Success,
                "Wi-Fi mode enabled",
                result.Value.Message);
        }
        catch (Exception ex)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not switch to Wi-Fi", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PairAsync()
    {
        var endpoint = PairEndpoint;
        var code = PairCode;
        PairCode = string.Empty; // the code is single-use and is never kept in memory longer than needed
        await RunAsync("Pairing failed", () => _devices.PairAsync(endpoint, code),
            "Paired. Now enter the address and port from the main Wireless debugging screen and press Connect.");
    }

    [RelayCommand]
    private Task ConnectAsync()
    {
        var endpoint = ConnectEndpoint;
        return RunAsync("Connection failed", () => _devices.ConnectAsync(endpoint), "Connected");
    }

    public void Dispose() => _devices.Changed -= OnDevicesChanged;

    private async Task RunAsync(string failureTitle, Func<Task<OperationResult<string>>> action, string successTitle, bool quiet = false)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (result.Succeeded)
            {
                if (!quiet)
                {
                    _notifications.Post(NotificationSeverity.Success, successTitle, result.Value);
                }
            }
            else
            {
                _notifications.Post(NotificationSeverity.Error, failureTitle, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _notifications.Post(NotificationSeverity.Error, failureTitle, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(Rebuild);

    private void Rebuild()
    {
        var wanted = SelectedItem?.Serial ?? _devices.SelectedDeviceId;
        _rebuilding = true;
        try
        {
            Items.Clear();
            foreach (var device in _devices.Devices)
            {
                Items.Add(new DeviceItemViewModel(device));
            }

            SelectedItem = Items.FirstOrDefault(i => i.Serial == wanted);
        }
        finally
        {
            _rebuilding = false;
        }

        StatusMessage = _devices.StatusMessage;

        // Edit boxes are only reloaded when the user moves to another device, never on a background refresh.
        if (SelectedItem?.Serial != _editingSerial)
        {
            LoadEditFields(SelectedItem);
        }
    }

    private void LoadEditFields(DeviceItemViewModel? item)
    {
        _editingSerial = item?.Serial;
        if (item is null)
        {
            return;
        }

        EditName = item.Device.HasCustomName ? item.Device.DisplayName : string.Empty;
        EditIconIndex = Math.Max(0, IndexOf(IconOptions, item.Device.IconGlyph));
        EditColorIndex = Math.Max(0, IndexOf(ColorOptions, item.Device.AccentColor));
    }

    private static int IndexOf(IReadOnlyList<ChoiceOption> options, string value)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
