using MyAndroidPhone.Core.Instances;

namespace MyAndroidPhone.Core.Workspace;

/// <param name="VirtualPath">The path as the terminal and File Explorer show it, e.g. /.system/logs/2026-10-31_18-20-11.log.</param>
public sealed record WorkspaceFile(string Name, string FullPath, string VirtualPath, long Size, DateTime Modified);

/// <summary>
/// The ".system" space of the open instance: a folder on this computer that belongs to the instance (session logs,
/// screenshots, notes). Nothing in it is ever sent to the phone unless the user copies it there.
/// </summary>
public interface IInstanceWorkspace
{
    /// <summary>Name of the virtual top-level folder for this space.</summary>
    const string VirtualRoot = "/.system";

    bool IsOpen { get; }

    /// <summary>The folder on this computer; null while no instance is open.</summary>
    string? RootDirectory { get; }

    string? LogsDirectory { get; }

    /// <summary>The log of the running session, e.g. .../logs/2026-10-31_18-20-11.log.</summary>
    string? CurrentLogPath { get; }

    /// <summary>Creates the folders and starts a new session log named with the current date and time.</summary>
    void Open(AndroidInstance instance);

    void Close();

    /// <summary>Appends one line to the session log. Safe from any thread; secrets are redacted before writing.</summary>
    void Log(string source, string message);

    /// <summary>Session logs, newest first.</summary>
    IReadOnlyList<WorkspaceFile> ListLogs();

    /// <summary>
    /// Turns a path below /.system (relative, '/' separated) into a full local path. Returns null for anything that would
    /// leave the folder, so a crafted name can never reach another place on the computer.
    /// </summary>
    string? ResolveLocal(string relativePath);

    string ToVirtualPath(string localPath);
}
