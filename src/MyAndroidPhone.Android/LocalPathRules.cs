namespace MyAndroidPhone.Android;

/// <summary>Turns names that exist on Android into names Windows accepts, and keeps writes inside the chosen folder.</summary>
public static class LocalPathRules
{
    private static readonly char[] WindowsInvalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeName(string name)
    {
        var chars = name.Select(c => c < 0x20 || WindowsInvalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            return "_";
        }

        var stem = result.Contains('.') ? result[..result.IndexOf('.')] : result;
        return Reserved.Contains(stem) ? "_" + result : result;
    }

    /// <summary>True when <paramref name="path"/> resolves to somewhere inside <paramref name="root"/>.</summary>
    public static bool IsInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
