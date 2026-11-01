using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Workspace;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed class LauncherItem(string id, string name, string glyph, Action launch)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Glyph { get; } = glyph;

    public void Launch() => launch();
}

public sealed class RunningWindowItem(string id, string title, string glyph, bool isMinimized, Action focus, Action close)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public string StateText { get; } = isMinimized ? "Minimized" : "Open";

    public ICommand FocusCommand { get; } = new RelayCommand(focus);

    public ICommand CloseCommand { get; } = new RelayCommand(close);
}

/// <summary>
/// The Device Center start menu: live phone information (battery, Wi-Fi, storage, memory), launchers and the list of
/// open windows (the task manager). The phone is only polled while the menu is open.
/// </summary>
public sealed partial class StartMenuViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly IDeviceStatusService _status;
    private readonly ActiveInstance _active;
    private readonly IUiDispatcher _ui;
    private readonly IInstanceWorkspace _workspace;
    private readonly ILogger<StartMenuViewModel> _logger;
    private DispatcherQueueTimer? _timer;
    private int _reading;
    private bool _open;
    private string? _lastLoggedProblem;

    public StartMenuViewModel(IDeviceStatusService status, ActiveInstance active, IUiDispatcher ui, IInstanceWorkspace workspace, ILogger<StartMenuViewModel> logger)
    {
        _status = status;
        _active = active;
        _ui = ui;
        _workspace = workspace;
        _logger = logger;
    }

    public ObservableCollection<LauncherItem> Launchers { get; } = new();

    public ObservableCollection<RunningWindowItem> Windows { get; } = new();

    public event EventHandler? ConnectionsRequested;

    public event EventHandler? SwitchInstanceRequested;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private string _batteryText = "Not read yet";

    [ObservableProperty]
    private string _batteryDetail = string.Empty;

    [ObservableProperty]
    private double _batteryPercent;

    [ObservableProperty]
    private string _wifiText = "Not read yet";

    [ObservableProperty]
    private string _wifiDetail = string.Empty;

    [ObservableProperty]
    private string _storageText = "Not read yet";

    [ObservableProperty]
    private string _storageDetail = string.Empty;

    [ObservableProperty]
    private double _storagePercent;

    [ObservableProperty]
    private string _memoryText = "Not read yet";

    [ObservableProperty]
    private string _memoryDetail = string.Empty;

    [ObservableProperty]
    private double _memoryPercent;

    [ObservableProperty]
    private string _uptimeText = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasNoWindows = true;

    public void SetWindows(IEnumerable<RunningWindowItem> windows)
    {
        Windows.Clear();
        foreach (var window in windows)
        {
            Windows.Add(window);
        }

        HasNoWindows = Windows.Count == 0;
    }

    public void OnOpened()
    {
        _open = true;
        InstanceName = _active.Current?.Name ?? string.Empty;
        _ = RefreshAsync();
        _timer ??= CreateTimer();
        _timer?.Start();
    }

    public void OnClosed()
    {
        _open = false;
        _timer?.Stop();
    }

    private DispatcherQueueTimer? CreateTimer()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return null;
        }

        var timer = queue.CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_open)
            {
                _ = RefreshAsync();
            }
        };
        return timer;
    }

    [RelayCommand]
    private void OpenConnections() => ConnectionsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SwitchInstance() => SwitchInstanceRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var serial = _active.Current?.DeviceSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            ShowProblem("No phone is attached to this instance.");
            return;
        }

        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var result = await _status.ReadAsync(serial, CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ShowProblem(result.ErrorMessage);
                return;
            }

            Apply(result.Value);
            HasError = false;
            ErrorText = string.Empty;
            _lastLoggedProblem = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the phone status failed.");
            ShowProblem("Could not read the phone: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
            IsLoading = false;
        }
    }

    private void ShowProblem(string message)
    {
        HasError = true;
        ErrorText = message;
        if (_lastLoggedProblem != message)
        {
            _lastLoggedProblem = message;
            _workspace.Log("device", "Status not available: " + message);
        }
    }

    private void Apply(DeviceStatusSnapshot s)
    {
        if (s.Battery is { } b)
        {
            BatteryPercent = b.Level ?? 0;
            BatteryText = b.Level is { } level ? $"{level}%" : "Unknown";
            BatteryDetail = string.Join(" - ", new[]
            {
                string.IsNullOrWhiteSpace(b.Status) ? null : b.Status,
                string.IsNullOrWhiteSpace(b.PowerSource) ? null : b.PowerSource,
                b.TemperatureCelsius is { } t ? $"{t:0.#} C" : null
            }.Where(x => x is not null));
        }
        else
        {
            BatteryText = "Not reported";
            BatteryDetail = string.Empty;
            BatteryPercent = 0;
        }

        if (s.Wifi is { } w)
        {
            WifiText = w.Enabled == false ? "Off" : w.Connected ? (w.Ssid ?? "Connected") : "Not connected";
            WifiDetail = string.Join(" - ", new[]
            {
                w.IpAddress,
                w.SignalDbm is { } dbm ? $"{dbm} dBm" : null,
                w.LinkSpeedMbps is { } speed ? $"{speed} Mbps" : null
            }.Where(x => !string.IsNullOrEmpty(x)));
        }
        else
        {
            WifiText = "Not reported";
            WifiDetail = string.Empty;
        }

        if (s.Storage is { } st)
        {
            StoragePercent = st.UsedFraction * 100;
            StorageText = $"{Formatting.Bytes(st.UsedBytes)} of {Formatting.Bytes(st.TotalBytes)}";
            StorageDetail = $"{Formatting.Bytes(st.FreeBytes)} free";
        }
        else
        {
            StorageText = "Not reported";
            StorageDetail = string.Empty;
            StoragePercent = 0;
        }

        if (s.Memory is { } m)
        {
            MemoryPercent = m.UsedFraction * 100;
            MemoryText = $"{Formatting.Bytes(m.TotalBytes - m.AvailableBytes)} of {Formatting.Bytes(m.TotalBytes)}";
            MemoryDetail = $"{Formatting.Bytes(m.AvailableBytes)} available";
        }
        else
        {
            MemoryText = "Not reported";
            MemoryDetail = string.Empty;
            MemoryPercent = 0;
        }

        UptimeText = s.Uptime is { } up ? $"Up {(up.TotalDays >= 1 ? $"{(int)up.TotalDays} d " : string.Empty)}{up.Hours} h {up.Minutes} min" : string.Empty;
        UpdatedText = "Updated " + s.ReadAt.LocalDateTime.ToString("T");
    }

    public void Dispose()
    {
        _open = false;
        _timer?.Stop();
    }
}
