namespace MyAndroidPhone.Android;

public enum VirtualArea
{
    /// <summary>The top level, which holds the two folders .android and .system.</summary>
    Root,

    /// <summary>The phone's file system.</summary>
    Android,

    /// <summary>The instance's own space on this computer.</summary>
    System
}

/// <param name="Path">
/// For <see cref="VirtualArea.Android"/>: the absolute device path. For <see cref="VirtualArea.System"/>: the path below
/// .system, always starting with '/'. For <see cref="VirtualArea.Root"/>: "/".
/// </param>
public sealed record VirtualLocation(VirtualArea Area, string Path)
{
    public static readonly VirtualLocation Root = new(VirtualArea.Root, "/");

    public static readonly VirtualLocation AndroidRoot = new(VirtualArea.Android, "/");

    public static readonly VirtualLocation SystemRoot = new(VirtualArea.System, "/");

    /// <summary>The path as users see and type it: /.android/sdcard, /.system/logs, or /.</summary>
    public string Display => Area switch
    {
        VirtualArea.Android => Path == "/" ? "/.android" : "/.android" + Path,
        VirtualArea.System => Path == "/" ? "/.system" : "/.system" + Path,
        _ => "/"
    };

    public string Name => Area == VirtualArea.Root ? "/" : Path == "/" ? (Area == VirtualArea.Android ? ".android" : ".system") : RemotePath.GetName(Path);

    public VirtualLocation? Parent => Area switch
    {
        VirtualArea.Root => null,
        _ when Path == "/" => Root,
        _ => this with { Path = RemotePath.GetParent(Path) ?? "/" }
    };

    public VirtualLocation Child(string name) => Area switch
    {
        VirtualArea.Root => name == ".android" ? AndroidRoot : name == ".system" ? SystemRoot : this,
        _ => this with { Path = RemotePath.Combine(Path, name) }
    };
}

/// <summary>
/// Resolves what the user typed (relative or absolute, with "." and "..") against the current folder. A path that starts
/// with / but not with /.android or /.system is taken as a phone path, so "ls /sdcard" works as expected.
/// </summary>
public static class VirtualPath
{
    public const string AndroidFolder = ".android";
    public const string SystemFolder = ".system";

    public static VirtualLocation? Resolve(VirtualLocation current, string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0') || input.Length > RemotePath.MaxPathLength)
        {
            return null;
        }

        var text = input.Trim().Replace('\\', '/');
        string absolute;
        if (text.StartsWith('/'))
        {
            absolute = text;
        }
        else
        {
            var baseDisplay = current.Display;
            absolute = (baseDisplay == "/" ? string.Empty : baseDisplay) + "/" + text;
        }

        var normalized = RemotePath.Normalize(absolute);
        if (normalized is null)
        {
            return null;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return VirtualLocation.Root;
        }

        if (parts[0] == SystemFolder)
        {
            return new VirtualLocation(VirtualArea.System, "/" + string.Join('/', parts.Skip(1)));
        }

        if (parts[0] == AndroidFolder)
        {
            return new VirtualLocation(VirtualArea.Android, "/" + string.Join('/', parts.Skip(1)));
        }

        // Anything else that was typed absolute (or came from the Android area's relative base) is a phone path.
        return new VirtualLocation(VirtualArea.Android, normalized);
    }
}
