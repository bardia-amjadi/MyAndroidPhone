using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Tools;

namespace MyAndroidPhone.Android;

public sealed class DeviceService : IDeviceService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(15);

    private readonly IAdbClient _adb;
    private readonly IToolService _tools;
    private readonly DeviceStoreService _store;
    private readonly ILogger<DeviceService> _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, LiveDevice> _live = new();

    private IReadOnlyList<AndroidDevice> _devices = Array.Empty<AndroidDevice>();
    private string? _selected;
    private string _status = "Starting…";
    private CancellationTokenSource? _cts;
    private int _started;

    public DeviceService(IAdbClient adb, IToolService tools, DeviceStoreService store, ILogger<DeviceService> logger)
    {
        _adb = adb;
        _tools = tools;
        _store = store;
        _logger = logger;
        _selected = store.Current.LastSelected;
        Rebuild();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AndroidDevice> Devices => Volatile.Read(ref _devices);

    public string? SelectedDeviceId => Volatile.Read(ref _selected);

    public AndroidDevice? SelectedDevice => Devices.FirstOrDefault(d => d.Serial == SelectedDeviceId);

    public string StatusMessage => Volatile.Read(ref _status);

    public void StartMonitoring()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _tools.Changed += OnToolsChanged;
        _ = Task.Run(() => MonitorAsync(token), CancellationToken.None);
    }

    public void Dispose()
    {
        _tools.Changed -= OnToolsChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void OnToolsChanged(object? sender, EventArgs e) => _ = SafeRefreshAsync();

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            await SafeRefreshAsync(token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SafeRefreshAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task SafeRefreshAsync(CancellationToken token = default)
    {
        try
        {
            await RefreshAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device refresh failed.");
            SetStatus("Device refresh failed: " + ex.Message);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<string> needDetails;
        List<string> newlyOnline;
        try
        {
            var adb = _tools.Get(ToolId.Adb);
            if (adb.Status == ToolStatus.NotChecked)
            {
                SetStatus("Checking for adb…");
                return;
            }

            if (!adb.IsUsable)
            {
                lock (_sync)
                {
                    _live.Clear();
                }

                SetStatus(adb.Status == ToolStatus.Invalid
                    ? "adb needs attention. Open Tool Manager to fix it; devices appear here once adb works."
                    : "adb was not found. Open Tool Manager to select adb.exe from Android SDK Platform-Tools; devices appear here once adb works.");
                Rebuild();
                return;
            }

            var list = await _adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (!list.Succeeded)
            {
                // Keep the last known list; a transient adb error must not make devices vanish and reappear.
                SetStatus("Could not list devices: " + list.ErrorMessage);
                return;
            }

            newlyOnline = Apply(list.Value, out needDetails);
            var online = list.Value.Count(e => e.State == DeviceConnectionState.Online);
            SetStatus(list.Value.Count == 0
                ? "No devices found. Connect a phone with USB debugging on, or pair a wireless device in Device Center."
                : $"{list.Value.Count} device(s) found, {online} ready.");
            EnsureSelection();
            Rebuild();
        }
        finally
        {
            _refreshGate.Release();
        }

        if (newlyOnline.Count > 0)
        {
            var now = DateTimeOffset.Now;
            await _store.UpdateAsync(s =>
            {
                foreach (var serial in newlyOnline)
                {
                    var record = DeviceStoreService.GetOrAdd(s, serial);
                    record.LastConnected = now;
                    lock (_sync)
                    {
                        if (_live.TryGetValue(serial, out var live))
                        {
                            record.LastConnectionType = live.Entry.ConnectionType;
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            Rebuild();
        }

        if (needDetails.Count > 0)
        {
            await Task.WhenAll(needDetails.Select(serial => RefreshDetailsAsync(serial, cancellationToken))).ConfigureAwait(false);
        }
    }

    private List<string> Apply(IReadOnlyList<AdbDeviceEntry> entries, out List<string> needDetails)
    {
        var newlyOnline = new List<string>();
        needDetails = new List<string>();
        var now = DateTimeOffset.Now;
        lock (_sync)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                seen.Add(entry.Serial);
                if (!_live.TryGetValue(entry.Serial, out var live))
                {
                    live = new LiveDevice(entry);
                    _live[entry.Serial] = live;
                }

                var wasOnline = live.Entry.State == DeviceConnectionState.Online && live.Seen;
                live.Entry = entry;
                live.Seen = true;
                if (entry.State == DeviceConnectionState.Online && !wasOnline)
                {
                    newlyOnline.Add(entry.Serial);
                    live.NeedsHardware = true;
                    live.RetryAfter = null;
                }

                if (entry.State != DeviceConnectionState.Online)
                {
                    continue;
                }

                var retryOk = live.RetryAfter is null || live.RetryAfter <= now;
                var batteryDue = live.Hardware is not null && (live.BatteryAt is null || now - live.BatteryAt > BatteryInterval);
                if (retryOk && (live.NeedsHardware || batteryDue))
                {
                    needDetails.Add(entry.Serial);
                }
            }

            foreach (var gone in _live.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _live.Remove(gone);
            }
        }

        return newlyOnline;
    }

    private async Task RefreshDetailsAsync(string serial, CancellationToken cancellationToken)
    {
        var gate = _deviceLocks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // another operation owns this device right now
        }

        try
        {
            bool needHardware;
            lock (_sync)
            {
                if (!_live.TryGetValue(serial, out var current) || current.Entry.State != DeviceConnectionState.Online)
                {
                    return;
                }

                needHardware = current.NeedsHardware;
            }

            if (needHardware)
            {
                var result = await _adb.ReadHardwareAsync(serial, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_live.TryGetValue(serial, out var live))
                    {
                        if (result.Succeeded)
                        {
                            live.Hardware = result.Value;
                            live.NeedsHardware = false;
                            live.BatteryAt = DateTimeOffset.Now;
                            live.RetryAfter = null;
                        }
                        else
                        {
                            live.RetryAfter = DateTimeOffset.Now + RetryAfterFailure;
                            _logger.LogWarning("Could not read device details: {Message}", result.ErrorMessage);
                        }
                    }
                }

                if (result.Succeeded)
                {
                    var model = DisplayModel(result.Value);
                    await _store.UpdateAsync(s =>
                    {
                        var record = DeviceStoreService.GetOrAdd(s, serial);
                        record.LastKnownModel = model;
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var battery = await _adb.ReadBatteryAsync(serial, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_live.TryGetValue(serial, out var live) && live.Hardware is not null)
                    {
                        live.BatteryAt = DateTimeOffset.Now; // also on failure, so a broken dumpsys is not retried every poll
                        if (battery.Succeeded)
                        {
                            live.Hardware = live.Hardware with { Battery = battery.Value };
                        }
                    }
                }
            }

            Rebuild();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SelectAsync(string? serial)
    {
        if (serial is not null && Devices.All(d => d.Serial != serial))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(SelectedDeviceId, serial, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        Volatile.Write(ref _selected, serial);
        Raise();
        return PersistSelectionAsync(serial);
    }

    private async Task PersistSelectionAsync(string? serial)
    {
        try
        {
            await _store.UpdateAsync(s => s.LastSelected = serial).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remember the selected device.");
        }
    }

    public async Task<OperationResult<string>> UpdatePreferencesAsync(string serial, string? displayName, string iconGlyph, string accentColor)
    {
        if (Devices.All(d => d.Serial != serial))
        {
            return OperationResult<string>.Failure(ErrorKind.NotFound, "That device is not known.");
        }

        var name = displayName?.Trim();
        if (name is { Length: > 60 })
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Use a name of 60 characters or fewer.");
        }

        if (string.IsNullOrEmpty(iconGlyph) || iconGlyph.Length > 2)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Choose one of the listed icons.");
        }

        if (!InputValidator.IsValidColor(accentColor))
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Choose one of the listed colors.");
        }

        try
        {
            await _store.UpdateAsync(s =>
            {
                var record = DeviceStoreService.GetOrAdd(s, serial);
                record.DisplayName = string.IsNullOrEmpty(name) ? null : name;
                record.IconGlyph = iconGlyph;
                record.AccentColor = accentColor.ToUpperInvariant();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<string>.Failure(ErrorKind.Io, "Could not save the device settings: " + ex.Message);
        }

        Rebuild();
        return OperationResult<string>.Success("Device settings saved.");
    }

    public async Task ForgetAsync(string serial)
    {
        await _store.UpdateAsync(s =>
        {
            s.Devices.Remove(serial);
            if (s.LastSelected == serial)
            {
                s.LastSelected = null;
            }
        }).ConfigureAwait(false);
        if (SelectedDeviceId == serial)
        {
            Volatile.Write(ref _selected, null);
        }

        Rebuild();
    }

    public async Task<OperationResult<string>> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default)
    {
        var normalized = InputValidator.NormalizeEndpoint(endpoint);
        if (!normalized.Succeeded)
        {
            return normalized;
        }

        var code = InputValidator.ValidatePairingCode(pairingCode);
        if (!code.Succeeded)
        {
            return code;
        }

        return await _adb.PairAsync(normalized.Value, code.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<string>> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var normalized = InputValidator.NormalizeEndpoint(endpoint);
        if (!normalized.Succeeded)
        {
            return normalized;
        }

        var result = await _adb.ConnectAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await SafeRefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<OperationResult<string>> DisconnectAsync(string serial, CancellationToken cancellationToken = default)
    {
        var device = Devices.FirstOrDefault(d => d.Serial == serial);
        if (device is null)
        {
            return OperationResult<string>.Failure(ErrorKind.NotFound, "That device is not known.");
        }

        if (device.ConnectionType != DeviceConnectionType.Wireless)
        {
            return OperationResult<string>.Failure(ErrorKind.Unsupported, "Only wireless connections can be disconnected here. Unplug a USB device to disconnect it.");
        }

        var result = await _adb.DisconnectAsync(serial, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await SafeRefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<OperationResult<TcpIpResult>> EnableTcpIpAsync(string serial, int port, CancellationToken cancellationToken = default)
    {
        var device = Devices.FirstOrDefault(d => d.Serial == serial);
        if (device is null)
        {
            return OperationResult<TcpIpResult>.Failure(ErrorKind.NotFound, "That device is not known.");
        }

        if (device.ConnectionType != DeviceConnectionType.Usb || !device.IsOnline)
        {
            return OperationResult<TcpIpResult>.Failure(
                ErrorKind.Unsupported,
                "Switching to Wi-Fi needs the phone connected over USB and authorized. Connect it with a cable first.");
        }

        var gate = _deviceLocks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The address is read before restarting adbd, because the USB link may drop during the restart.
            var address = await _adb.ReadWifiAddressAsync(serial, cancellationToken).ConfigureAwait(false);
            var switched = await _adb.TcpIpAsync(serial, port, cancellationToken).ConfigureAwait(false);
            if (!switched.Succeeded)
            {
                return OperationResult<TcpIpResult>.Failure(switched.Error!);
            }

            if (!address.Succeeded)
            {
                return OperationResult<TcpIpResult>.Success(new TcpIpResult(
                    $"The phone is listening on TCP port {port}, but its Wi-Fi address could not be read ({address.ErrorMessage}). " +
                    "Find the IP in Settings > About phone > Status, then connect to IP:" + port + ".",
                    null));
            }

            var endpoint = $"{address.Value}:{port}";
            return OperationResult<TcpIpResult>.Success(new TcpIpResult(
                $"The phone is listening on {endpoint}. Press Connect to continue; you can then unplug the cable.",
                endpoint));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult<string>> RestartAdbServerAsync(CancellationToken cancellationToken = default)
    {
        var result = await _adb.RestartServerAsync(cancellationToken).ConfigureAwait(false);
        await SafeRefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private void EnsureSelection()
    {
        var current = SelectedDeviceId;
        var known = _store.Current.Devices.ContainsKey(current ?? string.Empty);
        bool liveHas;
        lock (_sync)
        {
            liveHas = current is not null && _live.ContainsKey(current);
        }

        if (current is not null && (liveHas || known))
        {
            return;
        }

        string? first;
        lock (_sync)
        {
            first = _live.Where(p => p.Value.Entry.State == DeviceConnectionState.Online).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        }

        Volatile.Write(ref _selected, first);
        if (first is not null)
        {
            _ = PersistSelectionAsync(first);
        }
    }

    private void SetStatus(string message)
    {
        if (string.Equals(StatusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(ref _status, message);
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private void Rebuild()
    {
        var store = _store.Current;
        var result = new List<AndroidDevice>();
        lock (_sync)
        {
            var serials = new HashSet<string>(_live.Keys, StringComparer.Ordinal);
            serials.UnionWith(store.Devices.Keys);
            foreach (var serial in serials)
            {
                store.Devices.TryGetValue(serial, out var record);
                _live.TryGetValue(serial, out var live);

                var state = live?.Entry.State ?? DeviceConnectionState.Disconnected;
                var type = live?.Entry.ConnectionType ?? record?.LastConnectionType ?? DeviceConnectionType.Unknown;
                var hardware = live?.Hardware;
                var custom = !string.IsNullOrWhiteSpace(record?.DisplayName);
                var name = custom
                    ? record!.DisplayName!
                    : hardware is not null ? DisplayModel(hardware)
                    : live?.Entry.Model ?? record?.LastKnownModel ?? serial;

                result.Add(new AndroidDevice(
                    serial,
                    type,
                    state,
                    live?.Entry.Model,
                    live?.Entry.UsbPort,
                    name,
                    custom,
                    record?.IconGlyph ?? DeviceDefaults.IconGlyph,
                    record?.AccentColor ?? DeviceDefaults.AccentColor,
                    record?.LastConnected,
                    hardware,
                    DeviceGuidance.CapabilitiesFor(state, type, hardware),
                    DeviceGuidance.ForState(state, type)));
            }
        }

        var sorted = result
            .OrderBy(d => d.State == DeviceConnectionState.Disconnected ? 1 : 0)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Serial, StringComparer.Ordinal)
            .ToList();
        var previous = Volatile.Read(ref _devices);
        Volatile.Write(ref _devices, sorted);

        // Polling rebuilds the list every few seconds; listeners are only woken when something visible changed.
        if (!SameDevices(previous, sorted))
        {
            Raise();
        }
    }

    private static bool SameDevices(IReadOnlyList<AndroidDevice> a, IReadOnlyList<AndroidDevice> b) =>
        a.Count == b.Count
        && a.Zip(b).All(p =>
            p.First.Serial == p.Second.Serial
            && p.First.ConnectionType == p.Second.ConnectionType
            && p.First.State == p.Second.State
            && p.First.DisplayName == p.Second.DisplayName
            && p.First.IconGlyph == p.Second.IconGlyph
            && p.First.AccentColor == p.Second.AccentColor
            && p.First.LastConnected == p.Second.LastConnected
            && Equals(p.First.Hardware, p.Second.Hardware));

    private static string DisplayModel(DeviceHardware hardware)
    {
        var manufacturer = hardware.Manufacturer;
        var model = hardware.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.IsNullOrWhiteSpace(manufacturer) ? "Android device" : Capitalize(manufacturer);
        }

        if (string.IsNullOrWhiteSpace(manufacturer) || model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            return model;
        }

        return $"{Capitalize(manufacturer)} {model}";
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private sealed class LiveDevice(AdbDeviceEntry entry)
    {
        public AdbDeviceEntry Entry { get; set; } = entry;

        public bool Seen { get; set; }

        public DeviceHardware? Hardware { get; set; }

        public bool NeedsHardware { get; set; } = true;

        public DateTimeOffset? BatteryAt { get; set; }

        public DateTimeOffset? RetryAfter { get; set; }
    }
}
