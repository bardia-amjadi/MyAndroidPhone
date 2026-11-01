using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Core.Devices;

/// <summary>Wi-Fi as the phone reports it. Fields the phone did not report are null; nothing is guessed.</summary>
public sealed record WifiStatus(bool? Enabled, bool Connected, string? Ssid, string? IpAddress, int? SignalDbm, int? LinkSpeedMbps);

public sealed record StorageStatus(string Mount, long TotalBytes, long UsedBytes)
{
    public long FreeBytes => Math.Max(0, TotalBytes - UsedBytes);

    public double UsedFraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / TotalBytes, 0, 1);
}

public sealed record MemoryStatus(long TotalBytes, long AvailableBytes)
{
    public double UsedFraction => TotalBytes <= 0 ? 0 : Math.Clamp(1 - (double)AvailableBytes / TotalBytes, 0, 1);
}

public sealed record DeviceStatusSnapshot(
    BatteryInfo? Battery,
    WifiStatus? Wifi,
    StorageStatus? Storage,
    MemoryStatus? Memory,
    TimeSpan? Uptime,
    DateTimeOffset ReadAt);

public interface IDeviceStatusService
{
    /// <summary>Reads battery, Wi-Fi, storage, memory and uptime in one round trip to the phone.</summary>
    Task<OperationResult<DeviceStatusSnapshot>> ReadAsync(string serial, CancellationToken cancellationToken);
}
