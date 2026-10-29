using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Tools;

namespace MyAndroidPhone.Tools;

/// <summary>
/// Turns a tool's version output into a display version. Returns null when the output does not look like the
/// expected tool, which is how a wrong executable (or a renamed one) is rejected.
/// </summary>
public static partial class ToolVersionParser
{
    // "Android Debug Bridge version 1.0.41" / "Version 36.0.0-13206524"
    [GeneratedRegex(@"Android Debug Bridge version\s+(?<bridge>\d+(?:\.\d+)+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdbBridgeVersion();

    [GeneratedRegex(@"^Version\s+(?<tools>\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AdbToolsVersion();

    // "scrcpy 3.1 <https://github.com/Genymobile/scrcpy>"
    [GeneratedRegex(@"^scrcpy\s+(?<version>\d+(?:\.\d+)*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ScrcpyVersion();

    public static string? Parse(ToolId id, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        switch (id)
        {
            case ToolId.Adb:
            {
                var bridge = AdbBridgeVersion().Match(output);
                if (!bridge.Success)
                {
                    return null;
                }

                var tools = AdbToolsVersion().Match(output);
                return tools.Success
                    ? $"{tools.Groups["tools"].Value} (ADB {bridge.Groups["bridge"].Value})"
                    : $"ADB {bridge.Groups["bridge"].Value}";
            }

            case ToolId.Scrcpy:
            {
                var match = ScrcpyVersion().Match(output);
                return match.Success ? match.Groups["version"].Value : null;
            }

            default:
                return null;
        }
    }
}
