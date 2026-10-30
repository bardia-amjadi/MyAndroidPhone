using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Processes;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Tools;

namespace MyAndroidPhone.Android;

/// <summary>The raw outcome of a device command. A non-zero exit code is data here, not an error: callers decide what it means.</summary>
public sealed record AdbCommandOutput(int ExitCode, string StandardOutput, string StandardError)
{
    public string Combined => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

public interface IAdbClient
{
    /// <summary>Runs one shell command on the device. The command string must already be safely quoted.</summary>
    Task<OperationResult<AdbCommandOutput>> ShellAsync(string serial, string command, TimeSpan timeout, CancellationToken cancellationToken);

    Task<OperationResult<AdbCommandOutput>> PullAsync(string serial, string remotePath, string localPath, CancellationToken cancellationToken);

    Task<OperationResult<AdbCommandOutput>> PushAsync(string serial, string localPath, string remotePath, CancellationToken cancellationToken);

    Task<OperationResult<IReadOnlyList<AdbDeviceEntry>>> ListDevicesAsync(CancellationToken cancellationToken);

    Task<OperationResult<DeviceHardware>> ReadHardwareAsync(string serial, CancellationToken cancellationToken);

    Task<OperationResult<BatteryInfo?>> ReadBatteryAsync(string serial, CancellationToken cancellationToken);

    Task<OperationResult<string>> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken);

    Task<OperationResult<string>> ConnectAsync(string endpoint, CancellationToken cancellationToken);

    Task<OperationResult<string>> DisconnectAsync(string target, CancellationToken cancellationToken);

    Task<OperationResult<string>> TcpIpAsync(string serial, int port, CancellationToken cancellationToken);

    Task<OperationResult<string>> ReadWifiAddressAsync(string serial, CancellationToken cancellationToken);

    Task<OperationResult<string>> RestartServerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one place adb command lines are built. Per-device commands always start with "-s &lt;serial&gt;", so a command can
/// never be sent to "whichever device adb picks".
/// </summary>
public sealed class AdbClient(IProcessRunner runner, IToolService tools, ILogger<AdbClient> logger) : IAdbClient
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(20);

    public async Task<OperationResult<IReadOnlyList<AdbDeviceEntry>>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync(new[] { "devices", "-l" }, ListTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<IReadOnlyList<AdbDeviceEntry>>.Failure(run.Error!);
        }

        // Daemon start-up chatter ("* daemon started successfully") can arrive on either stream.
        var text = run.Value.StandardOutput + Environment.NewLine + run.Value.StandardError;
        return OperationResult<IReadOnlyList<AdbDeviceEntry>>.Success(AdbOutputParser.ParseDevices(text));
    }

    public async Task<OperationResult<AdbCommandOutput>> ShellAsync(string serial, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<AdbCommandOutput>.Failure(valid.Error!);
        }

        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0'))
        {
            return OperationResult<AdbCommandOutput>.Failure(ErrorKind.InvalidInput, "Invalid device command.");
        }

        var run = await RunAsync(DeviceArgs(serial, "shell", command), timeout, null, cancellationToken, allowNonZeroExit: true).ConfigureAwait(false);
        return ToOutput(run);
    }

    public async Task<OperationResult<AdbCommandOutput>> PullAsync(string serial, string remotePath, string localPath, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<AdbCommandOutput>.Failure(valid.Error!);
        }

        if (!remotePath.StartsWith('/') || !Path.IsPathFullyQualified(localPath))
        {
            return OperationResult<AdbCommandOutput>.Failure(ErrorKind.InvalidInput, "Transfers need a full device path and a full local path.");
        }

        // Pull/push use the sync protocol: paths are never interpreted by a shell, so no quoting is needed or wanted.
        var run = await RunAsync(DeviceArgs(serial, "pull", remotePath, localPath), null, null, cancellationToken, allowNonZeroExit: true).ConfigureAwait(false);
        return ToOutput(run);
    }

    public async Task<OperationResult<AdbCommandOutput>> PushAsync(string serial, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<AdbCommandOutput>.Failure(valid.Error!);
        }

        if (!remotePath.StartsWith('/') || !Path.IsPathFullyQualified(localPath))
        {
            return OperationResult<AdbCommandOutput>.Failure(ErrorKind.InvalidInput, "Transfers need a full device path and a full local path.");
        }

        var run = await RunAsync(DeviceArgs(serial, "push", localPath, remotePath), null, null, cancellationToken, allowNonZeroExit: true).ConfigureAwait(false);
        return ToOutput(run);
    }

    private static OperationResult<AdbCommandOutput> ToOutput(OperationResult<ProcessRunResult> run) =>
        run.Succeeded
            ? OperationResult<AdbCommandOutput>.Success(new AdbCommandOutput(run.Value.ExitCode ?? -1, run.Value.StandardOutput, run.Value.StandardError))
            : OperationResult<AdbCommandOutput>.Failure(run.Error!);

    public async Task<OperationResult<DeviceHardware>> ReadHardwareAsync(string serial, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<DeviceHardware>.Failure(valid.Error!);
        }

        var props = await RunAsync(DeviceArgs(serial, "shell", "getprop"), ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!props.Succeeded)
        {
            return OperationResult<DeviceHardware>.Failure(props.Error!);
        }

        var parsed = AdbOutputParser.ParseGetProp(props.Value.StandardOutput);
        if (parsed.Count == 0)
        {
            return OperationResult<DeviceHardware>.Failure(ErrorKind.ProcessFailure, "The device returned no properties.", props.Value.StandardError);
        }

        // Screen and battery are best effort: a device that does not answer them still gets its other facts.
        var size = await RunAsync(DeviceArgs(serial, "shell", "wm", "size"), ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        var density = await RunAsync(DeviceArgs(serial, "shell", "wm", "density"), ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        var battery = await ReadBatteryAsync(serial, cancellationToken).ConfigureAwait(false);

        var hardware = AdbOutputParser.BuildHardware(
            parsed,
            size.Succeeded ? AdbOutputParser.ParseWmSize(size.Value.StandardOutput) : null,
            density.Succeeded ? AdbOutputParser.ParseWmDensity(density.Value.StandardOutput) : null,
            battery.Succeeded ? battery.Value : null,
            DateTimeOffset.Now);
        return OperationResult<DeviceHardware>.Success(hardware);
    }

    public async Task<OperationResult<BatteryInfo?>> ReadBatteryAsync(string serial, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<BatteryInfo?>.Failure(valid.Error!);
        }

        var run = await RunAsync(DeviceArgs(serial, "shell", "dumpsys", "battery"), ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        return run.Succeeded
            ? OperationResult<BatteryInfo?>.Success(AdbOutputParser.ParseBattery(run.Value.StandardOutput))
            : OperationResult<BatteryInfo?>.Failure(run.Error!);
    }

    public async Task<OperationResult<string>> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken)
    {
        // The code is argument index 2 and is masked in the command log.
        var run = await RunAsync(new[] { "pair", endpoint, pairingCode }, NetworkTimeout, new[] { 2 }, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        return AdbOutputParser.ParsePairResult(run.Value.StandardOutput + Environment.NewLine + run.Value.StandardError);
    }

    public async Task<OperationResult<string>> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        var run = await RunAsync(new[] { "connect", endpoint }, NetworkTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        return AdbOutputParser.ParseConnectResult(run.Value.StandardOutput + Environment.NewLine + run.Value.StandardError);
    }

    public async Task<OperationResult<string>> DisconnectAsync(string target, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(target);
        if (!valid.Succeeded)
        {
            return OperationResult<string>.Failure(valid.Error!);
        }

        var run = await RunAsync(new[] { "disconnect", target }, ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        var text = (run.Value.StandardOutput + Environment.NewLine + run.Value.StandardError).Trim();
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("no such device", StringComparison.OrdinalIgnoreCase)
            ? OperationResult<string>.Failure(ErrorKind.ProcessFailure, "Disconnect failed: " + FirstLine(text), text)
            : OperationResult<string>.Success(FirstLine(text).Length == 0 ? "Disconnected." : FirstLine(text));
    }

    public async Task<OperationResult<string>> TcpIpAsync(string serial, int port, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<string>.Failure(valid.Error!);
        }

        if (port is < 1024 or > 65535)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Use a port between 1024 and 65535.");
        }

        var run = await RunAsync(
            DeviceArgs(serial, "tcpip", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ShellTimeout,
            null,
            cancellationToken).ConfigureAwait(false);
        return run.Succeeded
            ? AdbOutputParser.ParseTcpIpResult(run.Value.StandardOutput + Environment.NewLine + run.Value.StandardError)
            : OperationResult<string>.Failure(run.Error!);
    }

    public async Task<OperationResult<string>> ReadWifiAddressAsync(string serial, CancellationToken cancellationToken)
    {
        var valid = InputValidator.ValidateSerial(serial);
        if (!valid.Succeeded)
        {
            return OperationResult<string>.Failure(valid.Error!);
        }

        var run = await RunAsync(DeviceArgs(serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0"), ShellTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        var ip = AdbOutputParser.ParseWifiAddress(run.Value.StandardOutput);
        return ip is null
            ? OperationResult<string>.Failure(ErrorKind.NotFound, "The phone has no Wi-Fi address (is it connected to Wi-Fi?).")
            : OperationResult<string>.Success(ip);
    }

    public async Task<OperationResult<string>> RestartServerAsync(CancellationToken cancellationToken)
    {
        var kill = await RunAsync(new[] { "kill-server" }, ServerTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!kill.Succeeded)
        {
            return OperationResult<string>.Failure(kill.Error!);
        }

        var start = await RunAsync(new[] { "start-server" }, ServerTimeout, null, cancellationToken).ConfigureAwait(false);
        return start.Succeeded
            ? OperationResult<string>.Success("The adb server was restarted.")
            : OperationResult<string>.Failure(start.Error!);
    }

    private static string[] DeviceArgs(string serial, params string[] command) =>
        new[] { "-s", serial }.Concat(command).ToArray();

    private async Task<OperationResult<ProcessRunResult>> RunAsync(
        string[] arguments,
        TimeSpan? timeout,
        IReadOnlyCollection<int>? sensitive,
        CancellationToken cancellationToken,
        bool allowNonZeroExit = false)
    {
        var adb = tools.Get(ToolId.Adb);
        if (!adb.IsUsable || adb.ExecutablePath is null)
        {
            return OperationResult<ProcessRunResult>.Failure(
                ErrorKind.NotFound,
                "adb is not available. Open Tool Manager and select adb.exe from Android SDK Platform-Tools.");
        }

        // The index of a sensitive argument shifts by the size of the "-s serial" prefix only for device commands,
        // and sensitive values are only passed for "pair", which has no prefix.
        var result = await runner
            .RunAsync(new ProcessRunRequest(adb.ExecutablePath, arguments, timeout, null, sensitive), cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case ProcessRunStatus.Completed:
                if (result.ExitCode == 0 || allowNonZeroExit)
                {
                    return OperationResult<ProcessRunResult>.Success(result);
                }

                var detail = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
                logger.LogWarning("adb {Command} exited with {ExitCode}", arguments[0] == "-s" ? arguments[2] : arguments[0], result.ExitCode);
                return OperationResult<ProcessRunResult>.Failure(
                    ErrorKind.ProcessFailure,
                    FirstLine(detail).Length == 0 ? $"adb exited with code {result.ExitCode}." : FirstLine(detail),
                    detail);
            case ProcessRunStatus.TimedOut:
                return OperationResult<ProcessRunResult>.Failure(ErrorKind.Timeout, "adb did not answer in time. The phone may be locked, asleep or unreachable.");
            case ProcessRunStatus.Cancelled:
                return OperationResult<ProcessRunResult>.Failure(ErrorKind.Cancelled, "The operation was cancelled.");
            case ProcessRunStatus.ExecutableNotFound:
                return OperationResult<ProcessRunResult>.Failure(ErrorKind.NotFound, "adb.exe could not be found at the configured location.");
            default:
                return OperationResult<ProcessRunResult>.Failure(ErrorKind.ProcessFailure, result.FailureMessage ?? "adb could not be started.");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
}
