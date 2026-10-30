using System.Globalization;
using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Files;

namespace MyAndroidPhone.Android;

/// <summary>
/// Parses `ls -l` output from Android (toybox, Android 7+, and the older toolbox layout). Lines that do not look like
/// directory entries are ignored; the caller decides whether that matters.
/// </summary>
public static partial class LsParser
{
    [GeneratedRegex(
        @"^(?<perm>[-dlcbps][-rwxsStT]{9})[.+]?\s+(?:(?<links>\d+)\s+)?(?<owner>\S+)\s+(?<group>\S+)\s+(?:(?<size>\d+)\s+)?(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?(?:\s+[+-]\d{4})?\s+(?<name>.+)$")]
    private static partial Regex EntryLine();

    /// <param name="directory">The listed directory; entry paths are built from it. Pass null when each name is already a full path (ls -d).</param>
    public static IReadOnlyList<RemoteEntry> Parse(string output, string? directory)
    {
        var entries = new List<RemoteEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.Ordinal))
            {
                continue;
            }

            var match = EntryLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var permissions = match.Groups["perm"].Value;
            var name = match.Groups["name"].Value;
            string? target = null;
            var kind = permissions[0] switch
            {
                '-' => RemoteEntryKind.File,
                'd' => RemoteEntryKind.Directory,
                'l' => RemoteEntryKind.Link,
                _ => RemoteEntryKind.Other
            };

            if (kind == RemoteEntryKind.Link)
            {
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0)
                {
                    target = name[(arrow + 4)..];
                    name = name[..arrow];
                }
            }

            string fullPath;
            if (directory is null)
            {
                fullPath = name;
                name = RemotePath.GetName(name);
            }
            else
            {
                fullPath = RemotePath.Combine(directory, name);
            }

            long? size = match.Groups["size"].Success && kind != RemoteEntryKind.Directory
                ? long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture)
                : null;
            DateTime? modified = DateTime.TryParse(
                match.Groups["date"].Value + " " + match.Groups["time"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;

            entries.Add(new RemoteEntry(name, fullPath, kind, size, modified, permissions, match.Groups["owner"].Value, target));
        }

        return entries;
    }
}
