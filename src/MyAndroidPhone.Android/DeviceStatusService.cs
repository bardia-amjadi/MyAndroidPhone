using System.Globalization;
using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Android;

/// <summary>Turns the output of read-only status commands into typed values. Unknown or unparseable parts become null.</summary>
public static partial class DeviceStatusParser
{
    public const string WifiMarker = "@@WIFI";
    public const string IpMarker = "@@IP";
    public const string StorageMarker = "@@DF";
    public const string MemoryMarker = "@@MEM";
    public const string UptimeMarker = "@@UP";

    /// <summary>One command line so a single round trip returns everything. Every part is read-only.</summary>
    public const string Command =
        "dumpsys battery 2>&1; echo " + WifiMarker + "; cmd wifi status 2>&1; echo " + IpMarker + "; ip -f inet addr show wlan0 2>&1; "
        + "echo " + StorageMarker + "; df -k /sdcard 2>&1; echo " + MemoryMarker + "; head -n 3 /proc/meminfo 2>&1; echo " + UptimeMarker + "; cat /proc/uptime 2>&1";

    [GeneratedRegex(@"Wifi is connected to ""(?<ssid>.*)""")]
    private static partial Regex ConnectedTo();

    [GeneratedRegex(@"\bSSID:\s*""(?<ssid>[^""]*)""")]
    private static partial Regex InfoSsid();

    [GeneratedRegex(@"\bRSSI:\s*(?<rssi>-?\d+)")]
    private static partial Regex Rssi();

    [GeneratedRegex(@"(?<![A-Za-z])Link speed:\s*(?<speed>\d+)\s*Mbps")]
    private static partial Regex LinkSpeed();

    [GeneratedRegex(@"^(?<key>MemTotal|MemAvailable|MemFree):\s+(?<kb>\d+)\s*kB", RegexOptions.Multiline)]
    private static partial Regex MemLine();

    public static DeviceStatusSnapshot Parse(string output, DateTimeOffset now)
    {
        var sections = Split(output);
        var battery = AdbOutputParser.ParseBattery(sections.GetValueOrDefault("") ?? string.Empty);
        return new DeviceStatusSnapshot(
            battery,
            ParseWifi(sections.GetValueOrDefault(WifiMarker) ?? string.Empty, sections.GetValueOrDefault(IpMarker) ?? string.Empty),
            ParseStorage(sections.GetValueOrDefault(StorageMarker) ?? string.Empty),
            ParseMemory(sections.GetValueOrDefault(MemoryMarker) ?? string.Empty),
            ParseUptime(sections.GetValueOrDefault(UptimeMarker) ?? string.Empty),
            now);
    }

    private static Dictionary<string, string> Split(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var key = string.Empty;
        var buffer = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                result[key] = string.Join('\n', buffer);
                key = line.Trim();
                buffer.Clear();
            }
            else
            {
                buffer.Add(line);
            }
        }

        result[key] = string.Join('\n', buffer);
        return result;
    }

    public static WifiStatus? ParseWifi(string wifiStatus, string ipOutput)
    {
        var ip = AdbOutputParser.ParseWifiAddress(ipOutput);
        bool? enabled = null;
        if (wifiStatus.Contains("Wifi is enabled", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (wifiStatus.Contains("Wifi is disabled", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }

        string? ssid = null;
        var match = ConnectedTo().Match(wifiStatus);
        if (match.Success)
        {
            ssid = match.Groups["ssid"].Value;
        }
        else
        {
            var info = InfoSsid().Match(wifiStatus);
            if (info.Success)
            {
                ssid = info.Groups["ssid"].Value;
            }
        }

        // Android hides the network name from some callers and prints a placeholder instead.
        if (ssid is not null && (ssid.Length == 0 || ssid.Contains("unknown ssid", StringComparison.OrdinalIgnoreCase)))
        {
            ssid = null;
        }

        var connected = match.Success || (ip is not null && enabled != false);
        if (enabled is null && ip is null && !match.Success && wifiStatus.Trim().Length == 0)
        {
            return null;
        }

        int? rssi = Rssi().Match(wifiStatus) is { Success: true } r ? int.Parse(r.Groups["rssi"].Value, CultureInfo.InvariantCulture) : null;
        int? speed = LinkSpeed().Match(wifiStatus) is { Success: true } s ? int.Parse(s.Groups["speed"].Value, CultureInfo.InvariantCulture) : null;
        return new WifiStatus(enabled, connected, ssid, ip, rssi, speed);
    }

    public static StorageStatus? ParseStorage(string dfOutput)
    {
        foreach (var line in dfOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase)).Reverse())
        {
            // Read from the end: the file system name may be long enough to wrap onto its own line.
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
            {
                continue;
            }

            var mount = tokens[^1];
            if (long.TryParse(tokens[^5], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && long.TryParse(tokens[^4], NumberStyles.None, CultureInfo.InvariantCulture, out var used))
            {
                return new StorageStatus(mount, total * 1024, used * 1024);
            }
        }

        return null;
    }

    public static MemoryStatus? ParseMemory(string meminfo)
    {
        long? total = null;
        long? available = null;
        long? free = null;
        foreach (Match m in MemLine().Matches(meminfo))
        {
            var kb = long.Parse(m.Groups["kb"].Value, CultureInfo.InvariantCulture) * 1024;
            switch (m.Groups["key"].Value)
            {
                case "MemTotal": total = kb; break;
                case "MemAvailable": available = kb; break;
                default: free = kb; break;
            }
        }

        return total is null ? null : new MemoryStatus(total.Value, available ?? free ?? 0);
    }

    public static TimeSpan? ParseUptime(string text)
    {
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}

public sealed class DeviceStatusService(IAdbClient adb) : IDeviceStatusService
{
    public async Task<OperationResult<DeviceStatusSnapshot>> ReadAsync(string serial, CancellationToken cancellationToken)
    {
        var run = await adb.ShellAsync(serial, DeviceStatusParser.Command, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<DeviceStatusSnapshot>.Failure(run.Error!);
        }

        var text = run.Value.StandardOutput;
        if (string.IsNullOrWhiteSpace(text))
        {
            return OperationResult<DeviceStatusSnapshot>.Failure(ErrorKind.ProcessFailure, "The phone did not return any status information.", run.Value.Combined);
        }

        return OperationResult<DeviceStatusSnapshot>.Success(DeviceStatusParser.Parse(text, DateTimeOffset.Now));
    }
}
