using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Core.Devices;

/// <summary>
/// The only owner of device state. Every adb command that targets a device goes through here with an explicit
/// serial; nothing assumes there is one device.
/// </summary>
public interface IDeviceService
{
    /// <summary>Live devices plus remembered devices that are currently disconnected.</summary>
    IReadOnlyList<AndroidDevice> Devices { get; }

    string? SelectedDeviceId { get; }

    AndroidDevice? SelectedDevice { get; }

    /// <summary>Why the list may be empty (adb missing, no devices, adb error). Never fabricated device data.</summary>
    string StatusMessage { get; }

    /// <summary>Raised on a background thread.</summary>
    event EventHandler? Changed;

    void StartMonitoring();

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task SelectAsync(string? serial);

    Task<OperationResult<string>> UpdatePreferencesAsync(string serial, string? displayName, string iconGlyph, string accentColor);

    /// <summary>Forgets the saved name, icon and color. A connected device simply reappears with defaults.</summary>
    Task ForgetAsync(string serial);

    Task<OperationResult<string>> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default);

    Task<OperationResult<string>> ConnectAsync(string endpoint, CancellationToken cancellationToken = default);

    Task<OperationResult<string>> DisconnectAsync(string serial, CancellationToken cancellationToken = default);

    Task<OperationResult<TcpIpResult>> EnableTcpIpAsync(string serial, int port, CancellationToken cancellationToken = default);

    Task<OperationResult<string>> RestartAdbServerAsync(CancellationToken cancellationToken = default);
}
