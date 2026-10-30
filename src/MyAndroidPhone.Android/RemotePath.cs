using System.Text;

namespace MyAndroidPhone.Android;

/// <summary>
/// Rules for paths on the device (always '/' separated). Every path or name that is placed into a shell command goes
/// through <see cref="Normalize"/> / <see cref="IsSafeName"/> and <see cref="ShellQuote"/> first.
/// </summary>
public static class RemotePath
{
    public const int MaxPathLength = 4096;

    /// <summary>Returns an absolute, normalized path ("." and ".." resolved), or null when the input is not acceptable.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path[0] != '/' || path.Any(IsControl))
        {
            return null;
        }

        var stack = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return "/" + string.Join('/', stack);
    }

    public static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 255
        && name is not ("." or "..")
        && !name.Contains('/')
        && !name.Any(IsControl);

    public static string Combine(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    public static string? GetParent(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null || normalized == "/")
        {
            return null;
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    public static string GetName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    public static bool IsSameOrInside(string path, string container) =>
        container == "/" || path == container || path.StartsWith(container + "/", StringComparison.Ordinal);

    /// <summary>Single-quotes a value for the device's shell. Embedded single quotes are closed, escaped and reopened.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static IReadOnlyList<(string Name, string Path)> Breadcrumbs(string normalizedPath)
    {
        var result = new List<(string, string)> { ("Device", "/") };
        var current = string.Empty;
        foreach (var part in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            result.Add((part, current));
        }

        return result;
    }

    /// <summary>"name.ext" becomes "name (1).ext", "name (2).ext", ... until <paramref name="exists"/> says it is free.</summary>
    public static string UniqueName(string name, Func<string, bool> exists)
    {
        if (!exists(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = new StringBuilder(stem).Append(" (").Append(i).Append(')').Append(extension).ToString();
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return stem + " (" + Guid.NewGuid().ToString("N")[..8] + ")" + extension;
    }

    private static bool IsControl(char c) => c < 0x20 || c == 0x7F;
}
