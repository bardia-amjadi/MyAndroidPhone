using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Android;

/// <summary>What the app remembers about a device between sessions. No debugging keys or credentials are stored.</summary>
public sealed class DeviceRecord
{
    public string? DisplayName { get; set; }

    public string IconGlyph { get; set; } = DeviceDefaults.IconGlyph;

    public string AccentColor { get; set; } = DeviceDefaults.AccentColor;

    public DateTimeOffset? LastConnected { get; set; }

    public string? LastKnownModel { get; set; }

    public DeviceConnectionType LastConnectionType { get; set; }
}

public sealed class DeviceStore
{
    public int SchemaVersion { get; set; } = 1;

    public string? LastSelected { get; set; }

    public Dictionary<string, DeviceRecord> Devices { get; set; } = new();
}

public sealed class DeviceStoreService : JsonStateService<DeviceStore>
{
    public DeviceStoreService(AppPaths paths, ILogger<DeviceStoreService> logger)
        : base(paths.DevicesFile, logger)
    {
    }

    public static DeviceRecord GetOrAdd(DeviceStore store, string serial)
    {
        if (!store.Devices.TryGetValue(serial, out var record))
        {
            record = new DeviceRecord();
            store.Devices[serial] = record;
        }

        return record;
    }
}
