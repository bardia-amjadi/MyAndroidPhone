using System.Text;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Workspace;

namespace MyAndroidPhone.Android;

public sealed record TextContent(string Text, long TotalBytes, bool Truncated);

/// <summary>
/// One view over the two places a user can work in: the phone (.android) and the instance's own space on this computer
/// (.system). The terminal, File Explorer and Notepad all go through it so both places follow the same rules.
/// </summary>
public sealed class VirtualFileSystem(IFileExplorerService files, IAdbClient adb, IInstanceWorkspace workspace)
{
    public const int DefaultTextLimit = 1024 * 1024;

    public IInstanceWorkspace Workspace => workspace;

    public static IReadOnlyList<RemoteEntry> RootEntries { get; } = new[]
    {
        new RemoteEntry(VirtualPath.AndroidFolder, "/.android", RemoteEntryKind.Directory, null, null, "", "", null),
        new RemoteEntry(VirtualPath.SystemFolder, "/.system", RemoteEntryKind.Directory, null, null, "", "", null)
    };

    public async Task<OperationResult<IReadOnlyList<RemoteEntry>>> ListAsync(string? serial, VirtualLocation location, CancellationToken cancellationToken)
    {
        switch (location.Area)
        {
            case VirtualArea.Root:
                return OperationResult<IReadOnlyList<RemoteEntry>>.Success(RootEntries);
            case VirtualArea.System:
                return ListLocal(location);
            default:
                if (serial is null)
                {
                    return OperationResult<IReadOnlyList<RemoteEntry>>.Failure(ErrorKind.NotFound, "No phone is connected.");
                }

                var listing = await files.ListAsync(serial, location.Path, cancellationToken).ConfigureAwait(false);
                return listing.Succeeded
                    ? OperationResult<IReadOnlyList<RemoteEntry>>.Success(listing.Value.Entries)
                    : OperationResult<IReadOnlyList<RemoteEntry>>.Failure(listing.Error!);
        }
    }

    private OperationResult<IReadOnlyList<RemoteEntry>> ListLocal(VirtualLocation location)
    {
        var local = workspace.ResolveLocal(location.Path);
        if (local is null || !Directory.Exists(local))
        {
            return OperationResult<IReadOnlyList<RemoteEntry>>.Failure(ErrorKind.NotFound, "That folder does not exist in .system.");
        }

        try
        {
            var entries = new DirectoryInfo(local).EnumerateFileSystemInfos()
                .Select(i => i is DirectoryInfo
                    ? new RemoteEntry(i.Name, location.Child(i.Name).Display, RemoteEntryKind.Directory, null, i.LastWriteTime, "", "", null)
                    : new RemoteEntry(i.Name, location.Child(i.Name).Display, RemoteEntryKind.File, ((FileInfo)i).Length, i.LastWriteTime, "", "", null))
                .ToList();
            return OperationResult<IReadOnlyList<RemoteEntry>>.Success(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<IReadOnlyList<RemoteEntry>>.Failure(ErrorKind.Io, "Could not read this folder: " + ex.Message);
        }
    }

    public async Task<OperationResult<RemoteEntry>> StatAsync(string? serial, VirtualLocation location, CancellationToken cancellationToken)
    {
        switch (location.Area)
        {
            case VirtualArea.Root:
                return OperationResult<RemoteEntry>.Success(new RemoteEntry("/", "/", RemoteEntryKind.Directory, null, null, "", "", null));
            case VirtualArea.System:
                var local = workspace.ResolveLocal(location.Path);
                if (local is null)
                {
                    return OperationResult<RemoteEntry>.Failure(ErrorKind.InvalidInput, "That path is not inside .system.");
                }

                if (Directory.Exists(local))
                {
                    return OperationResult<RemoteEntry>.Success(new RemoteEntry(location.Name, location.Display, RemoteEntryKind.Directory, null, Directory.GetLastWriteTime(local), "", "", null));
                }

                return File.Exists(local)
                    ? OperationResult<RemoteEntry>.Success(new RemoteEntry(location.Name, location.Display, RemoteEntryKind.File, new FileInfo(local).Length, File.GetLastWriteTime(local), "", "", null))
                    : OperationResult<RemoteEntry>.Failure(ErrorKind.NotFound, "That item does not exist in .system.");
            default:
                if (serial is null)
                {
                    return OperationResult<RemoteEntry>.Failure(ErrorKind.NotFound, "No phone is connected.");
                }

                return await files.StatAsync(serial, location.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a text file. Binary files are refused instead of showing garbage.</summary>
    public async Task<OperationResult<TextContent>> ReadTextAsync(string? serial, VirtualLocation location, int maxBytes, CancellationToken cancellationToken)
    {
        var stat = await StatAsync(serial, location, cancellationToken).ConfigureAwait(false);
        if (!stat.Succeeded)
        {
            return OperationResult<TextContent>.Failure(stat.Error!);
        }

        if (stat.Value.IsDirectory)
        {
            return OperationResult<TextContent>.Failure(ErrorKind.InvalidInput, "That is a folder, not a file.");
        }

        var total = stat.Value.Size ?? 0;
        if (location.Area == VirtualArea.System)
        {
            var local = workspace.ResolveLocal(location.Path)!;
            try
            {
                var buffer = new byte[Math.Min(total, maxBytes)];
                await using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                return Decode(buffer.AsSpan(0, read), total, total > maxBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult<TextContent>.Failure(ErrorKind.Io, "Could not read the file: " + ex.Message);
            }
        }

        if (serial is null)
        {
            return OperationResult<TextContent>.Failure(ErrorKind.NotFound, "No phone is connected.");
        }

        // head -c limits what travels over adb; the text is decoded from the command output as UTF-8.
        var run = await adb.ShellAsync(serial, $"head -c {maxBytes} -- {RemotePath.ShellQuote(location.Path)}", TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<TextContent>.Failure(run.Error!);
        }

        var output = run.Value;
        if (output.ExitCode != 0)
        {
            var problem = FileExplorerService.Interpret(output, location.Path, StorageClassifier.Classify(location.Path));
            return OperationResult<TextContent>.Failure(problem ?? new OperationError(ErrorKind.ProcessFailure, "Could not read the file."));
        }

        return Decode(Encoding.UTF8.GetBytes(output.StandardOutput), total, total > maxBytes);
    }

    private static OperationResult<TextContent> Decode(ReadOnlySpan<byte> bytes, long total, bool truncated)
    {
        if (bytes.Contains((byte)0))
        {
            return OperationResult<TextContent>.Failure(ErrorKind.Unsupported, "This looks like a binary file, so it is not shown as text.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Count(c => c == '�') > Math.Max(8, text.Length / 20))
        {
            return OperationResult<TextContent>.Failure(ErrorKind.Unsupported, "This file is not valid UTF-8 text, so it is not shown.");
        }

        return OperationResult<TextContent>.Success(new TextContent(text, total, truncated));
    }

    public async Task<OperationResult<string>> CreateFolderAsync(string? serial, VirtualLocation parent, string name, CancellationToken cancellationToken)
    {
        switch (parent.Area)
        {
            case VirtualArea.Root:
                return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Open .android or .system first. The top level only holds those two folders.");
            case VirtualArea.System:
                if (!RemotePath.IsSafeName(name) || LocalPathRules.SanitizeName(name) != name)
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, "That folder name cannot be used on Windows.");
                }

                var local = workspace.ResolveLocal(RemotePath.Combine(parent.Path, name));
                if (local is null)
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, "That path is not inside .system.");
                }

                if (Directory.Exists(local) || File.Exists(local))
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, $"\"{name}\" already exists here.");
                }

                Directory.CreateDirectory(local);
                return OperationResult<string>.Success(parent.Child(name).Display);
            default:
                if (serial is null)
                {
                    return OperationResult<string>.Failure(ErrorKind.NotFound, "No phone is connected.");
                }

                var made = await files.CreateFolderAsync(serial, parent.Path, name, cancellationToken).ConfigureAwait(false);
                return made.Succeeded ? OperationResult<string>.Success("/.android" + made.Value) : made;
        }
    }

    public async Task<OperationResult<string>> RenameAsync(string? serial, VirtualLocation target, string newName, CancellationToken cancellationToken)
    {
        switch (target.Area)
        {
            case VirtualArea.Root:
                return OperationResult<string>.Failure(ErrorKind.InvalidInput, "The top-level folders cannot be renamed.");
            case VirtualArea.System:
                if (target.Path == "/")
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, ".system itself cannot be renamed.");
                }

                if (!RemotePath.IsSafeName(newName) || LocalPathRules.SanitizeName(newName) != newName)
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, "That name cannot be used on Windows.");
                }

                var from = workspace.ResolveLocal(target.Path);
                var toLocation = target.Parent!.Child(newName);
                var to = workspace.ResolveLocal(toLocation.Path);
                if (from is null || to is null)
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, "That path is not inside .system.");
                }

                if (File.Exists(to) || Directory.Exists(to))
                {
                    return OperationResult<string>.Failure(ErrorKind.InvalidInput, $"\"{newName}\" already exists here.");
                }

                try
                {
                    if (Directory.Exists(from))
                    {
                        Directory.Move(from, to);
                    }
                    else if (File.Exists(from))
                    {
                        File.Move(from, to);
                    }
                    else
                    {
                        return OperationResult<string>.Failure(ErrorKind.NotFound, "That item does not exist in .system.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return OperationResult<string>.Failure(ErrorKind.Io, "Could not rename: " + ex.Message);
                }

                return OperationResult<string>.Success(toLocation.Display);
            default:
                if (serial is null)
                {
                    return OperationResult<string>.Failure(ErrorKind.NotFound, "No phone is connected.");
                }

                var renamed = await files.RenameAsync(serial, target.Path, newName, cancellationToken).ConfigureAwait(false);
                return renamed.Succeeded ? OperationResult<string>.Success("/.android" + renamed.Value) : renamed;
        }
    }

    public async Task<OperationResult<string>> DeleteAsync(string? serial, IReadOnlyList<VirtualLocation> targets, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Nothing to delete.");
        }

        if (targets.Select(t => t.Area).Distinct().Count() != 1)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Delete items from one place at a time.");
        }

        var area = targets[0].Area;
        if (area == VirtualArea.Root)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "The top-level folders cannot be deleted.");
        }

        if (area == VirtualArea.Android)
        {
            if (serial is null)
            {
                return OperationResult<string>.Failure(ErrorKind.NotFound, "No phone is connected.");
            }

            return await files.DeleteAsync(serial, targets.Select(t => t.Path).ToList(), cancellationToken).ConfigureAwait(false);
        }

        var count = 0;
        foreach (var target in targets)
        {
            if (target.Path == "/")
            {
                return OperationResult<string>.Failure(ErrorKind.InvalidInput, ".system itself cannot be deleted. Delete what is inside it instead.");
            }

            var local = workspace.ResolveLocal(target.Path);
            if (local is null)
            {
                return OperationResult<string>.Failure(ErrorKind.InvalidInput, "That path is not inside .system.");
            }

            // The running session log stays: it is being written right now.
            if (string.Equals(local, workspace.CurrentLogPath, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<string>.Failure(ErrorKind.InvalidInput, "This is the log of the running session. It can be deleted after the session ends.");
            }

            try
            {
                if (Directory.Exists(local))
                {
                    if (workspace.CurrentLogPath is { } current && current.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return OperationResult<string>.Failure(ErrorKind.InvalidInput, "This folder holds the log of the running session. Delete the older logs inside it instead.");
                    }

                    Directory.Delete(local, recursive: true);
                }
                else if (File.Exists(local))
                {
                    File.Delete(local);
                }
                else
                {
                    return OperationResult<string>.Failure(ErrorKind.NotFound, "That item does not exist in .system.");
                }

                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult<string>.Failure(ErrorKind.Io, "Could not delete: " + ex.Message);
            }
        }

        return OperationResult<string>.Success(count == 1 ? "Deleted 1 item." : $"Deleted {count} items.");
    }
}
