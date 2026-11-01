using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Workspace;
using MyAndroidPhone.Infrastructure.Logging;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Infrastructure.Workspace;

public sealed class InstanceWorkspace(AppPaths paths, ILogger<InstanceWorkspace> logger) : IInstanceWorkspace
{
    public const int RetainedLogs = 50;
    public const string LogTimestamp = "yyyy-MM-dd_HH-mm-ss";

    private readonly object _gate = new();

    public bool IsOpen { get; private set; }

    public string? RootDirectory { get; private set; }

    public string? LogsDirectory { get; private set; }

    public string? CurrentLogPath { get; private set; }

    public void Open(AndroidInstance instance)
    {
        lock (_gate)
        {
            if (IsOpen)
            {
                CloseCore();
            }

            var root = Path.Combine(paths.InstancesDirectory, instance.Id.ToString("N"), ".system");
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            RootDirectory = root;
            LogsDirectory = logs;

            var stamp = DateTime.Now.ToString(LogTimestamp, CultureInfo.InvariantCulture);
            var path = Path.Combine(logs, stamp + ".log");
            for (var i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(logs, $"{stamp}_{i}.log");
            }

            CurrentLogPath = path;
            IsOpen = true;
            Prune(logs, path);
        }

        Log("session", $"Session started for \"{instance.Name}\" ({instance.ConnectionType}).");
    }

    public void Close()
    {
        lock (_gate)
        {
            if (!IsOpen)
            {
                return;
            }
        }

        Log("session", "Session ended.");
        lock (_gate)
        {
            CloseCore();
        }
    }

    private void CloseCore()
    {
        IsOpen = false;
        RootDirectory = null;
        LogsDirectory = null;
        CurrentLogPath = null;
    }

    public void Log(string source, string message)
    {
        lock (_gate)
        {
            if (!IsOpen || CurrentLogPath is null)
            {
                return;
            }

            try
            {
                var clean = LogRedactor.Redact(message).Replace("\r", string.Empty, StringComparison.Ordinal);
                var builder = new StringBuilder();
                foreach (var line in clean.Split('\n'))
                {
                    builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append(" [").Append(source).Append("] ").AppendLine(line);
                }

                File.AppendAllText(CurrentLogPath, builder.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The activity log must never break the feature that is being logged.
                logger.LogWarning(ex, "Could not write to the session log.");
            }
        }
    }

    public IReadOnlyList<WorkspaceFile> ListLogs()
    {
        string? logs;
        lock (_gate)
        {
            logs = LogsDirectory;
        }

        if (logs is null || !Directory.Exists(logs))
        {
            return Array.Empty<WorkspaceFile>();
        }

        return new DirectoryInfo(logs).EnumerateFiles("*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new WorkspaceFile(f.Name, f.FullName, ToVirtualPath(f.FullName), f.Length, f.LastWriteTime))
            .ToList();
    }

    public string? ResolveLocal(string relativePath)
    {
        string? root;
        lock (_gate)
        {
            root = RootDirectory;
        }

        if (root is null || relativePath is null || relativePath.Contains('\0'))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var inside = full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside ? full : null;
    }

    public string ToVirtualPath(string localPath)
    {
        string? root;
        lock (_gate)
        {
            root = RootDirectory;
        }

        if (root is null)
        {
            return localPath;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(localPath);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return localPath;
        }

        var relative = full[rootFull.Length..].Replace(Path.DirectorySeparatorChar, '/');
        return IInstanceWorkspace.VirtualRoot + relative;
    }

    private void Prune(string logs, string keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(logs).EnumerateFiles("*.log")
                         .Where(f => !f.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(RetainedLogs - 1))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove old session logs.");
        }
    }
}
