using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Settings;

namespace MyAndroidPhone.Core.Instances;

/// <summary>
/// A saved workspace for one Android device: its name, look and how to reach it. No debugging keys, pairing codes or
/// credentials are stored here.
/// </summary>
public sealed class AndroidInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string IconGlyph { get; set; } = DeviceDefaults.IconGlyph;

    public string AccentColor { get; set; } = DeviceDefaults.AccentColor;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>The adb serial this instance controls. All commands from the instance's desktop target this serial.</summary>
    public string DeviceSerial { get; set; } = string.Empty;

    public DeviceConnectionType ConnectionType { get; set; }

    /// <summary>"host:port" used to reconnect a wireless device that dropped. Null for USB.</summary>
    public string? Endpoint { get; set; }

    public string? DeviceModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastOpened { get; set; }

    public AndroidInstance Clone() => (AndroidInstance)MemberwiseClone();
}

public sealed class InstanceStore
{
    public int SchemaVersion { get; set; } = 1;

    public List<AndroidInstance> Instances { get; set; } = new();
}

public interface IInstanceService
{
    /// <summary>Snapshot, newest-opened first.</summary>
    IReadOnlyList<AndroidInstance> Instances { get; }

    event EventHandler? Changed;

    AndroidInstance? Find(Guid id);

    /// <summary>Fails when the name is empty or another instance already controls the same device.</summary>
    Task<OperationResult<AndroidInstance>> CreateAsync(AndroidInstance instance, CancellationToken cancellationToken = default);

    Task<OperationResult<AndroidInstance>> UpdateAsync(Guid id, Action<AndroidInstance> change, CancellationToken cancellationToken = default);

    /// <summary>Removes the saved instance only. The device and its files are not touched.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task MarkOpenedAsync(Guid id, CancellationToken cancellationToken = default);
}
