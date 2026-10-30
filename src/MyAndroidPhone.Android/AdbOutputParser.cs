using System.Globalization;
using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Android;

public sealed record AdbDeviceEntry(
    string Serial,
    DeviceConnectionState State,
    DeviceConnectionType ConnectionType,
    string? Model,
    string? UsbPort);

/// <summary>
/// Parses adb's text output. adb often reports failures on stdout with exit code 0, so success and failure are
/// decided from the text, not from the exit code.
/// </summary>
public static partial class AdbOutputParser
{
    [GeneratedRegex(@"^\[(?<key>[^\]]+)\]: \[(?<value>.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex PropLine();

    [GeneratedRegex(@"^\s*level:\s*(?<v>\d+)", RegexOptions.Multiline)]
    private static partial Regex BatteryLevel();

    [GeneratedRegex(@"^\s*status:\s*(?<v>\d+)", RegexOptions.Multiline)]
    private static partial Regex BatteryStatus();

    [GeneratedRegex(@"^\s*temperature:\s*(?<v>-?\d+)", RegexOptions.Multiline)]
    private static partial Regex BatteryTemperature();

    [GeneratedRegex(@"^\s*(?<src>AC|USB|Wireless) powered:\s*true", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatteryPower();

    [GeneratedRegex(@"(?<kind>Physical|Override) size:\s*(?<w>\d+)x(?<h>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WmSize();

    [GeneratedRegex(@"(?<kind>Physical|Override) density:\s*(?<d>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WmDensity();

    [GeneratedRegex(@"inet\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})/")]
    private static partial Regex InetAddress();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}:\d+$")]
    private static partial Regex Ipv4Endpoint();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static IReadOnlyList<AdbDeviceEntry> ParseDevices(string output)
    {
        var entries = new List<AdbDeviceEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '*' || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = Whitespace().Split(line);
            if (tokens.Length < 2)
            {
                continue;
            }

            var serial = tokens[0];
            var rawState = tokens[1];
            var next = 2;
            if (rawState == "no" && tokens.Length > 2 && tokens[2] == "permissions")
            {
                rawState = "no permissions";
                next = 3;
            }

            // Lines whose second word is not a known state (adb warnings, "adb server version ... doesn't match") are not devices.
            if (!TryMapState(rawState, out var state))
            {
                continue;
            }

            string? model = null;
            string? usb = null;
            for (var i = next; i < tokens.Length; i++)
            {
                var colon = tokens[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = tokens[i][..colon];
                var value = tokens[i][(colon + 1)..];
                if (key == "model")
                {
                    model = value.Replace('_', ' ');
                }
                else if (key == "usb")
                {
                    usb = value;
                }
            }

            entries.Add(new AdbDeviceEntry(serial, state, Classify(serial, usb), model, usb));
        }

        return entries;
    }

    public static DeviceConnectionType Classify(string serial, string? usbPort)
    {
        if (serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            return DeviceConnectionType.Emulator;
        }

        if (Ipv4Endpoint().IsMatch(serial) || serial.Contains("._adb-tls-connect._tcp", StringComparison.Ordinal))
        {
            return DeviceConnectionType.Wireless;
        }

        if (usbPort is not null || !serial.Contains(':'))
        {
            return DeviceConnectionType.Usb;
        }

        return DeviceConnectionType.Unknown;
    }

    private static bool TryMapState(string raw, out DeviceConnectionState state)
    {
        switch (raw)
        {
            case "device": state = DeviceConnectionState.Online; return true;
            case "offline": state = DeviceConnectionState.Offline; return true;
            case "unauthorized": state = DeviceConnectionState.Unauthorized; return true;
            case "authorizing": state = DeviceConnectionState.Authorizing; return true;
            case "no permissions": state = DeviceConnectionState.NoPermissions; return true;
            case "connecting": state = DeviceConnectionState.Connecting; return true;
            case "recovery": state = DeviceConnectionState.Recovery; return true;
            case "sideload": state = DeviceConnectionState.Sideload; return true;
            case "bootloader": state = DeviceConnectionState.Bootloader; return true;
            default: state = default; return false;
        }
    }

    public static Dictionary<string, string> ParseGetProp(string output)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in PropLine().Matches(output))
        {
            props[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return props;
    }

    public static DeviceHardware BuildHardware(
        IReadOnlyDictionary<string, string> props,
        (int Width, int Height)? screen,
        int? density,
        BatteryInfo? battery,
        DateTimeOffset now)
    {
        string? Get(string key) => props.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        int? api = int.TryParse(Get("ro.build.version.sdk"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new DeviceHardware(
            Get("ro.product.manufacturer"),
            Get("ro.product.model"),
            Get("ro.product.brand"),
            Get("ro.build.version.release"),
            api,
            Get("ro.build.display.id"),
            Get("ro.product.cpu.abi"),
            screen?.Width,
            screen?.Height,
            density,
            battery,
            now);
    }

    public static BatteryInfo? ParseBattery(string output)
    {
        var level = BatteryLevel().Match(output);
        var status = BatteryStatus().Match(output);
        if (!level.Success && !status.Success)
        {
            return null;
        }

        var source = BatteryPower().Match(output);
        var temperature = BatteryTemperature().Match(output);
        return new BatteryInfo(
            level.Success ? int.Parse(level.Groups["v"].Value, CultureInfo.InvariantCulture) : null,
            status.Success ? DescribeBatteryStatus(int.Parse(status.Groups["v"].Value, CultureInfo.InvariantCulture)) : "Unknown",
            source.Success ? source.Groups["src"].Value.ToUpperInvariant() switch { "AC" => "AC", "USB" => "USB", _ => "Wireless" } : "Battery",
            temperature.Success ? int.Parse(temperature.Groups["v"].Value, CultureInfo.InvariantCulture) / 10.0 : null);
    }

    private static string DescribeBatteryStatus(int code) => code switch
    {
        2 => "Charging",
        3 => "Discharging",
        4 => "Not charging",
        5 => "Full",
        _ => "Unknown"
    };

    /// <summary>The effective size: an override set with "wm size" wins over the physical panel size.</summary>
    public static (int Width, int Height)? ParseWmSize(string output)
    {
        (int, int)? physical = null;
        foreach (Match match in WmSize().Matches(output))
        {
            var size = (int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
            if (match.Groups["kind"].Value.Equals("Override", StringComparison.OrdinalIgnoreCase))
            {
                return size;
            }

            physical ??= size;
        }

        return physical;
    }

    public static int? ParseWmDensity(string output)
    {
        int? physical = null;
        foreach (Match match in WmDensity().Matches(output))
        {
            var value = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["kind"].Value.Equals("Override", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            physical ??= value;
        }

        return physical;
    }

    public static string? ParseWifiAddress(string output)
    {
        foreach (Match match in InetAddress().Matches(output))
        {
            var ip = match.Groups["ip"].Value;
            if (!ip.StartsWith("127.", StringComparison.Ordinal))
            {
                return ip;
            }
        }

        return null;
    }

    public static OperationResult<string> ParsePairResult(string output)
    {
        var text = output.Trim();
        if (text.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<string>.Success(FirstLine(text));
        }

        var message = FirstLine(text);
        if (message.Length == 0)
        {
            message = "adb gave no reason.";
        }

        var hint = " Check that the pairing dialog is still open on the phone (the code expires when it closes), that the IP address and pairing port are the ones shown in that dialog, and that both devices are on the same network.";
        return OperationResult<string>.Failure(ErrorKind.ProcessFailure, "Pairing failed: " + message + hint, text);
    }

    public static OperationResult<string> ParseConnectResult(string output)
    {
        var text = output.Trim();
        var line = FirstLine(text);
        var lower = text.ToLowerInvariant();
        var failed = lower.Contains("failed") || lower.Contains("cannot") || lower.Contains("unable") || lower.Contains("error") || lower.Contains("refused");
        if (!failed && lower.Contains("connected to"))
        {
            return OperationResult<string>.Success(line);
        }

        var hint = lower.Contains("failed to authenticate")
            ? " The phone has not paired with this computer yet. Pair it first (Android 11+), or approve the prompt on the phone."
            : " Check that Wireless debugging is on, that the port is the one shown on the main Wireless debugging screen (not the pairing port), and that both devices are on the same network.";
        return OperationResult<string>.Failure(ErrorKind.ProcessFailure, (line.Length == 0 ? "Connection failed." : "Connection failed: " + line) + hint, text);
    }

    public static OperationResult<string> ParseTcpIpResult(string output)
    {
        var text = output.Trim();
        if (text.Contains("restarting in TCP mode", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<string>.Success(FirstLine(text));
        }

        return OperationResult<string>.Failure(
            ErrorKind.ProcessFailure,
            "The device did not switch to TCP/IP mode: " + (FirstLine(text).Length == 0 ? "adb gave no reason." : FirstLine(text)),
            text);
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
}
