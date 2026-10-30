using System.Text;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Android;

/// <summary>
/// On-device file operations through `adb -s serial shell`. All paths are normalized and single-quoted; writes into
/// protected or system areas are refused before any command runs. adb often reports failures with exit code 0, so
/// the command output is inspected as well as the exit code.
/// </summary>
public sealed class FileExplorerService(IAdbClient adb, ILogger<FileExplorerService> logger) : IFileExplorerService
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(15);
    private const int SearchLimit = 500;

    private static readonly string[] ProtectedRoots =
    {
        "/", "/sdcard", "/storage", "/storage/emulated", "/storage/emulated/0", "/storage/self", "/storage/self/primary", "/data/local", "/data/local/tmp",
        "/sdcard/Android", "/storage/emulated/0/Android"
    };

    public async Task<IReadOnlyList<StorageLocation>> GetLocationsAsync(string serial, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Name, string Path, string Icon)>
        {
            ("Internal storage", "/sdcard", ""),
            ("Downloads", "/sdcard/Download", ""),
            ("Camera (DCIM)", "/sdcard/DCIM", ""),
            ("Pictures", "/sdcard/Pictures", ""),
            ("Documents", "/sdcard/Documents", ""),
            ("Music", "/sdcard/Music", ""),
            ("Movies", "/sdcard/Movies", ""),
            ("App folders", "/sdcard/Android/data", ""),
            ("Shell workspace", "/data/local/tmp", "")
        };

        var existing = new HashSet<string>(StringComparer.Ordinal);
        var probe = string.Join("; ", candidates.Select(c => $"[ -d {RemotePath.ShellQuote(c.Path)} ] && echo {RemotePath.ShellQuote(c.Path)}"))
                    + "; ls -1 /storage 2>/dev/null";
        var run = await adb.ShellAsync(serial, probe, QuickTimeout, cancellationToken).ConfigureAwait(false);
        var removable = new List<string>();
        if (run.Succeeded)
        {
            foreach (var line in run.Value.StandardOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                if (line.StartsWith('/'))
                {
                    existing.Add(line);
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(line, "^[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"))
                {
                    removable.Add("/storage/" + line);
                }
            }
        }
        else
        {
            logger.LogWarning("Could not probe storage locations: {Message}", run.ErrorMessage);
        }

        var result = new List<StorageLocation>();
        foreach (var c in candidates)
        {
            // If probing failed entirely, still offer the root locations; listing will explain any problem.
            if (existing.Contains(c.Path) || (!run.Succeeded && c.Path is "/sdcard"))
            {
                result.Add(new StorageLocation(c.Name, c.Path, c.Icon, StorageClassifier.Classify(c.Path)));
            }
        }

        foreach (var path in removable)
        {
            result.Add(new StorageLocation("SD card / USB storage (" + RemotePath.GetName(path) + ")", path, "", StorageClassifier.Classify(path)));
        }

        result.Add(new StorageLocation("Device root", "/", "", StorageClassifier.Classify("/")));
        return result;
    }

    public async Task<OperationResult<DirectoryListing>> ListAsync(string serial, string path, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(path);
        if (normalized is null)
        {
            return OperationResult<DirectoryListing>.Failure(ErrorKind.InvalidInput, "That is not a valid device path.");
        }

        var classification = StorageClassifier.Classify(normalized);
        var target = RemotePath.ShellQuote(normalized == "/" ? "/" : normalized + "/");
        var run = await adb.ShellAsync(serial, $"ls -lA -- {target}", QuickTimeout, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<DirectoryListing>.Failure(run.Error!);
        }

        var output = run.Value;
        var entries = LsParser.Parse(output.StandardOutput, normalized);
        // Only lines that are not directory entries can be error text; a file may legitimately be named "Permission denied".
        var leftover = string.Join('\n', output.StandardOutput.Split('\n')
            .Where(l => l.Trim().Length > 0 && !l.StartsWith("total ", StringComparison.Ordinal) && LsParser.Parse(l, normalized).Count == 0));
        var failure = Interpret(output with { StandardOutput = leftover }, normalized, classification);
        if (failure is not null && entries.Count == 0)
        {
            return OperationResult<DirectoryListing>.Failure(failure);
        }

        string? warning = null;
        if (failure is not null)
        {
            warning = "Some items could not be read: " + failure.Message;
        }
        else if (entries.Count == 0 && output.StandardOutput.Trim().Length > 0 && !output.StandardOutput.StartsWith("total", StringComparison.Ordinal))
        {
            warning = "The device returned output this app could not read as a file list.";
            logger.LogWarning("Unparsed ls output for a directory listing ({Length} characters).", output.StandardOutput.Length);
        }

        return OperationResult<DirectoryListing>.Success(new DirectoryListing(normalized, entries, classification, warning));
    }

    public async Task<OperationResult<RemoteEntry>> StatAsync(string serial, string path, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(path);
        if (normalized is null)
        {
            return OperationResult<RemoteEntry>.Failure(ErrorKind.InvalidInput, "That is not a valid device path.");
        }

        var run = await adb.ShellAsync(serial, $"ls -ld -- {RemotePath.ShellQuote(normalized)}", QuickTimeout, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<RemoteEntry>.Failure(run.Error!);
        }

        var entry = LsParser.Parse(run.Value.StandardOutput, null).FirstOrDefault();
        if (entry is not null)
        {
            return OperationResult<RemoteEntry>.Success(entry with { FullPath = normalized, Name = normalized == "/" ? "/" : RemotePath.GetName(normalized) });
        }

        return OperationResult<RemoteEntry>.Failure(Interpret(run.Value, normalized, StorageClassifier.Classify(normalized))
            ?? new OperationError(ErrorKind.NotFound, "That item does not exist."));
    }

    public async Task<OperationResult<string>> CreateFolderAsync(string serial, string parent, string name, CancellationToken cancellationToken)
    {
        var normalizedParent = RemotePath.Normalize(parent);
        if (normalizedParent is null || !RemotePath.IsSafeName(name))
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Folder names cannot be empty or contain '/'.");
        }

        var blocked = CheckWritable(normalizedParent);
        if (blocked is not null)
        {
            return OperationResult<string>.Failure(blocked);
        }

        var full = RemotePath.Combine(normalizedParent, name);
        var run = await adb.ShellAsync(
            serial,
            $"if [ -e {RemotePath.ShellQuote(full)} ] || [ -L {RemotePath.ShellQuote(full)} ]; then echo __EXISTS__; else mkdir -- {RemotePath.ShellQuote(full)}; fi",
            QuickTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        if (run.Value.StandardOutput.Contains("__EXISTS__", StringComparison.Ordinal))
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, $"\"{name}\" already exists here.");
        }

        var failure = Interpret(run.Value, full, StorageClassifier.Classify(full));
        return failure is null ? OperationResult<string>.Success(full) : OperationResult<string>.Failure(failure);
    }

    public async Task<OperationResult<string>> RenameAsync(string serial, string path, string newName, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(path);
        if (normalized is null || normalized == "/" || !RemotePath.IsSafeName(newName))
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Names cannot be empty or contain '/'.");
        }

        var blocked = CheckWritable(normalized) ?? CheckNotProtectedRoot(normalized);
        if (blocked is not null)
        {
            return OperationResult<string>.Failure(blocked);
        }

        var destination = RemotePath.Combine(RemotePath.GetParent(normalized)!, newName);
        if (destination == normalized)
        {
            return OperationResult<string>.Success(destination);
        }

        var run = await adb.ShellAsync(
            serial,
            $"if [ -e {RemotePath.ShellQuote(destination)} ] || [ -L {RemotePath.ShellQuote(destination)} ]; then echo __EXISTS__; else mv -- {RemotePath.ShellQuote(normalized)} {RemotePath.ShellQuote(destination)}; fi",
            QuickTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        if (run.Value.StandardOutput.Contains("__EXISTS__", StringComparison.Ordinal))
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, $"\"{newName}\" already exists here.");
        }

        var failure = Interpret(run.Value, normalized, StorageClassifier.Classify(normalized));
        return failure is null ? OperationResult<string>.Success(destination) : OperationResult<string>.Failure(failure);
    }

    public async Task<OperationResult<string>> DeleteAsync(string serial, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Nothing is selected.");
        }

        var normalizedPaths = new List<string>();
        foreach (var path in paths)
        {
            var normalized = RemotePath.Normalize(path);
            var blocked = normalized is null
                ? new OperationError(ErrorKind.InvalidInput, "That is not a valid device path.")
                : CheckWritable(normalized) ?? CheckNotProtectedRoot(normalized);
            if (blocked is not null)
            {
                return OperationResult<string>.Failure(blocked);
            }

            normalizedPaths.Add(normalized!);
        }

        var command = "rm -rf -- " + string.Join(' ', normalizedPaths.Select(RemotePath.ShellQuote));
        var run = await adb.ShellAsync(serial, command, LongTimeout, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<string>.Failure(run.Error!);
        }

        var failure = Interpret(run.Value, normalizedPaths[0], StorageClassifier.Classify(normalizedPaths[0]));
        if (failure is not null)
        {
            return OperationResult<string>.Failure(failure);
        }

        logger.LogInformation("Deleted {Count} item(s) on a device.", normalizedPaths.Count);
        return OperationResult<string>.Success(normalizedPaths.Count == 1 ? "Deleted 1 item." : $"Deleted {normalizedPaths.Count} items.");
    }

    public async Task<OperationResult<string>> PasteAsync(
        string serial, IReadOnlyList<string> sources, string destinationDirectory, bool move, ConflictPolicy policy, CancellationToken cancellationToken)
    {
        var destination = RemotePath.Normalize(destinationDirectory);
        if (destination is null || sources.Count == 0)
        {
            return OperationResult<string>.Failure(ErrorKind.InvalidInput, "Nothing to paste, or the destination is not valid.");
        }

        var blocked = CheckWritable(destination);
        if (blocked is not null)
        {
            return OperationResult<string>.Failure(blocked);
        }

        var existing = await ListNamesAsync(serial, destination, cancellationToken).ConfigureAwait(false);
        if (!existing.Succeeded)
        {
            return OperationResult<string>.Failure(existing.Error!);
        }

        var taken = new HashSet<string>(existing.Value, StringComparer.Ordinal);
        var done = 0;
        var skipped = 0;
        var problems = new List<string>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = RemotePath.Normalize(source);
            if (from is null || from == "/")
            {
                problems.Add("Invalid source path.");
                continue;
            }

            if (move)
            {
                var sourceBlocked = CheckWritable(from) ?? CheckNotProtectedRoot(from);
                if (sourceBlocked is not null)
                {
                    problems.Add($"{RemotePath.GetName(from)}: {sourceBlocked.Message}");
                    continue;
                }
            }

            var name = RemotePath.GetName(from);
            if (RemotePath.IsSameOrInside(destination, from))
            {
                problems.Add($"{name}: a folder cannot be placed inside itself.");
                continue;
            }

            var targetName = name;
            if (taken.Contains(name))
            {
                if (policy == ConflictPolicy.Skip || (policy == ConflictPolicy.Overwrite && RemotePath.Combine(destination, name) == from))
                {
                    skipped++;
                    continue;
                }

                if (policy == ConflictPolicy.KeepBoth)
                {
                    targetName = RemotePath.UniqueName(name, taken.Contains);
                }
                else
                {
                    var overwrite = await DescribeTargetAsync(serial, RemotePath.Combine(destination, name), cancellationToken).ConfigureAwait(false);
                    if (overwrite == TargetKind.Directory)
                    {
                        problems.Add($"{name}: a folder with this name exists. Choose Keep both or Skip instead of Overwrite.");
                        continue;
                    }
                }
            }

            var target = RemotePath.Combine(destination, targetName);
            var verb = move ? "mv -f" : "cp -rf";
            var run = await adb.ShellAsync(
                serial, $"{verb} -- {RemotePath.ShellQuote(from)} {RemotePath.ShellQuote(target)}", LongTimeout, cancellationToken).ConfigureAwait(false);
            if (!run.Succeeded)
            {
                return OperationResult<string>.Failure(run.Error!);
            }

            var failure = Interpret(run.Value, from, StorageClassifier.Classify(from));
            if (failure is not null)
            {
                problems.Add($"{name}: {failure.Message}");
                continue;
            }

            taken.Add(targetName);
            done++;
        }

        var summary = new StringBuilder();
        summary.Append(move ? "Moved " : "Copied ").Append(done).Append(done == 1 ? " item" : " items");
        if (skipped > 0)
        {
            summary.Append(", skipped ").Append(skipped);
        }

        if (problems.Count > 0)
        {
            return OperationResult<string>.Failure(
                done == 0 ? ErrorKind.Io : ErrorKind.ProcessFailure,
                (done > 0 ? summary + ". But " : string.Empty) + string.Join(" ", problems.Take(3)) + (problems.Count > 3 ? $" (+{problems.Count - 3} more)" : string.Empty),
                string.Join(Environment.NewLine, problems));
        }

        return OperationResult<string>.Success(summary + ".");
    }

    public async Task<OperationResult<IReadOnlyList<string>>> FindExistingAsync(
        string serial, string directory, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(directory);
        if (normalized is null)
        {
            return OperationResult<IReadOnlyList<string>>.Failure(ErrorKind.InvalidInput, "That is not a valid device path.");
        }

        var listing = await ListNamesAsync(serial, normalized, cancellationToken).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return OperationResult<IReadOnlyList<string>>.Failure(listing.Error!);
        }

        var present = new HashSet<string>(listing.Value, StringComparer.Ordinal);
        return OperationResult<IReadOnlyList<string>>.Success(names.Where(present.Contains).ToList());
    }

    public async Task<OperationResult<SearchResults>> SearchAsync(string serial, string root, string pattern, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.Normalize(root);
        var term = pattern.Trim();
        if (normalized is null || term.Length == 0 || term.Length > 100 || term.Any(c => c < 0x20))
        {
            return OperationResult<SearchResults>.Failure(ErrorKind.InvalidInput, "Type a search term (up to 100 characters).");
        }

        // The term is matched literally: glob characters are escaped, then the whole thing is quoted for the shell.
        var glob = new StringBuilder("*");
        foreach (var c in term)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                glob.Append('\\');
            }

            glob.Append(c);
        }

        glob.Append('*');
        var command = $"find {RemotePath.ShellQuote(normalized)} -iname {RemotePath.ShellQuote(glob.ToString())} -exec ls -ld -- {{}} + 2>&1 | head -n {SearchLimit + 200}";
        var run = await adb.ShellAsync(serial, command, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<SearchResults>.Failure(run.Error!);
        }

        var lines = run.Value.StandardOutput.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var unreadable = lines.Count(l => l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || l.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase));
        var entries = LsParser.Parse(string.Join('\n', lines), null).ToList();
        var truncated = entries.Count > SearchLimit;
        if (truncated)
        {
            entries = entries.Take(SearchLimit).ToList();
        }

        if (entries.Count == 0 && unreadable > 0)
        {
            return OperationResult<SearchResults>.Failure(ErrorKind.AccessDenied, "Android did not allow searching here.");
        }

        return OperationResult<SearchResults>.Success(new SearchResults(entries, truncated, unreadable));
    }

    private enum TargetKind { Missing, File, Directory }

    private async Task<TargetKind> DescribeTargetAsync(string serial, string path, CancellationToken cancellationToken)
    {
        var stat = await StatAsync(serial, path, cancellationToken).ConfigureAwait(false);
        return !stat.Succeeded ? TargetKind.Missing : stat.Value.IsDirectory ? TargetKind.Directory : TargetKind.File;
    }

    private async Task<OperationResult<IReadOnlyList<string>>> ListNamesAsync(string serial, string directory, CancellationToken cancellationToken)
    {
        var target = RemotePath.ShellQuote(directory == "/" ? "/" : directory + "/");
        var run = await adb.ShellAsync(serial, $"ls -1A -- {target}", QuickTimeout, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return OperationResult<IReadOnlyList<string>>.Failure(run.Error!);
        }

        var errorText = string.Join('\n', run.Value.StandardOutput.Split('\n').Where(l => l.StartsWith("ls:", StringComparison.Ordinal)));
        var failure = Interpret(run.Value with { StandardOutput = errorText }, directory, StorageClassifier.Classify(directory));
        if (failure is not null)
        {
            return OperationResult<IReadOnlyList<string>>.Failure(failure);
        }

        IReadOnlyList<string> names = run.Value.StandardOutput.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("ls:", StringComparison.Ordinal)).ToList();
        return OperationResult<IReadOnlyList<string>>.Success(names);
    }

    private static OperationError? CheckWritable(string normalizedPath)
    {
        var classification = StorageClassifier.Classify(normalizedPath);
        return classification.WritesBlocked
            ? new OperationError(ErrorKind.AccessDenied, $"{classification.Title}: this app does not change files here.", classification.Explanation)
            : null;
    }

    private static OperationError? CheckNotProtectedRoot(string normalizedPath) =>
        ProtectedRoots.Contains(normalizedPath, StringComparer.Ordinal)
            ? new OperationError(ErrorKind.InvalidInput, "This is a top-level storage folder and cannot be renamed, moved or deleted from here.")
            : null;

    /// <summary>Maps typical toybox/adb error text to an error; null when the output shows no problem.</summary>
    internal static OperationError? Interpret(AdbCommandOutput output, string path, StorageClassification classification)
    {
        var text = output.Combined;
        var lower = text.ToLowerInvariant();
        string FirstProblemLine() => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "The device reported an error.";

        if (lower.Contains("device offline", StringComparison.Ordinal) || lower.Contains("device not found", StringComparison.Ordinal)
            || lower.Contains("no devices/emulators", StringComparison.Ordinal) || lower.Contains("device unauthorized", StringComparison.Ordinal)
            || lower.Contains("device still authorizing", StringComparison.Ordinal))
        {
            return new OperationError(ErrorKind.ProcessFailure, "The device is not reachable. Check the cable or Wi-Fi connection and that you allowed debugging on the phone.", text);
        }

        if (lower.Contains("permission denied", StringComparison.Ordinal) || lower.Contains("operation not permitted", StringComparison.Ordinal)
            || lower.Contains("read-only file system", StringComparison.Ordinal))
        {
            var detail = classification.Access is StorageAccess.ProtectedAppData or StorageAccess.System ? classification.Explanation : null;
            return new OperationError(
                ErrorKind.AccessDenied,
                $"Android did not allow access to {(path == "/" ? "this location" : "\"" + RemotePath.GetName(path) + "\"")}. {classification.Title}.",
                detail ?? text);
        }

        if (lower.Contains("no such file or directory", StringComparison.Ordinal) || lower.Contains("not found", StringComparison.Ordinal) && lower.Contains(path.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return new OperationError(ErrorKind.NotFound, "That folder or file no longer exists on the device.", text);
        }

        if (lower.Contains("not a directory", StringComparison.Ordinal))
        {
            return new OperationError(ErrorKind.InvalidInput, "That is not a folder.", text);
        }

        if (lower.Contains("no space left", StringComparison.Ordinal))
        {
            return new OperationError(ErrorKind.Io, "The device has no space left.", text);
        }

        if (lower.Contains("file exists", StringComparison.Ordinal))
        {
            return new OperationError(ErrorKind.InvalidInput, "An item with that name already exists.", text);
        }

        if (output.ExitCode != 0 && text.Length > 0)
        {
            return new OperationError(ErrorKind.ProcessFailure, FirstProblemLine(), text);
        }

        return output.ExitCode != 0 ? new OperationError(ErrorKind.ProcessFailure, $"The device command failed (exit code {output.ExitCode}).") : null;
    }
}
