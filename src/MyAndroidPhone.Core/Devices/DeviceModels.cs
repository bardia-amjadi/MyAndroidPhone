namespace MyAndroidPhone.Core.Devices;

public enum DeviceConnectionState
{
    Online,
    Offline,
    Unauthorized,
    Authorizing,
    NoPermissions,
    Connecting,
    Recovery,
    Sideload,
    Bootloader,

    /// <summary>Remembered by the app but not currently reported by adb.</summary>
    Disconnected
}

public enum DeviceConnectionType
{
    Unknown,
    Usb,
    Wireless,
    Emulator
}

/// <param name="Level">Percent, or null when it could not be read.</param>
/// <param name="PowerSource">"AC", "USB", "Wireless" or "Battery".</param>
public sealed record BatteryInfo(int? Level, string Status, string PowerSource, double? TemperatureCelsius);

/// <summary>Facts read from the device. Any value the device did not report is null; nothing is guessed.</summary>
public sealed record DeviceHardware(
    string? Manufacturer,
    string? Model,
    string? Brand,
    string? AndroidVersion,
    int? ApiLevel,
    string? BuildId,
    string? CpuAbi,
    int? ScreenWidth,
    int? ScreenHeight,
    int? DensityDpi,
    BatteryInfo? Battery,
    DateTimeOffset FetchedAt);

public sealed record DeviceCapability(string Key, string Name, bool Supported, string Reason);

public static class DeviceDefaults
{
    public const string IconGlyph = "";
    public const string AccentColor = "#3B82F6";
}

/// <summary>One device, identified by its adb serial (USB serial, "ip:port", mDNS name or emulator id).</summary>
public sealed record AndroidDevice(
    string Serial,
    DeviceConnectionType ConnectionType,
    DeviceConnectionState State,
    string? AdbModel,
    string? UsbPort,
    string DisplayName,
    bool HasCustomName,
    string IconGlyph,
    string AccentColor,
    DateTimeOffset? LastConnected,
    DeviceHardware? Hardware,
    IReadOnlyList<DeviceCapability> Capabilities,
    string? Guidance)
{
    public string Id => Serial;

    public bool IsOnline => State == DeviceConnectionState.Online;
}

public sealed record TcpIpResult(string Message, string? Endpoint);
