using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Transfers;

namespace MyAndroidPhone.Android;

/// <summary>
/// Runs uploads and downloads. At most one transfer per device and <see cref="MaxConcurrent"/> overall run at a time; the
/// rest wait in the queue. A failed file never stops the others, and every failure is kept with its reason.
/// Transfers are not resumable: retry starts again and skips files that already arrived.
/// </summary>
public sealed class TransferService : ITransferService
{
    public const int MaxConcurrent = 2;

    private readonly IAdbClient _adb;
    private readonly IFileExplorerService _files;
    private readonly ILogger<TransferService> _logger;
    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly SemaphoreSlim _global = new(MaxConcurrent);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perDevice = new(StringComparer.Ordinal);
    private DateTime _lastNotify = DateTime.MinValue;

    public TransferService(IAdbClient adb, IFileExplorerService files, ILogger<TransferService> logger)
    {
        _adb = adb;
        _files = files;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<TransferSnapshot> Transfers
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Select(j => j.Snapshot).OrderByDescending(s => s.CreatedAt).ToList();
            }
        }
    }

    public Guid Enqueue(TransferRequest request)
    {
        var job = CreateJob(request);
        _ = Task.Run(() => ExecuteAsync(job, CancellationToken.None));
        return job.Id;
    }

    public async Task<TransferSnapshot> RunAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var job = CreateJob(request);
        using var registration = cancellationToken.Register(() => job.Cancel.Cancel());
        await ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
        return job.Snapshot;
    }

    public void Cancel(Guid id)
    {
        Job? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
        }

        job?.Cancel.Cancel();
    }

    public Guid? Retry(Guid id)
    {
        TransferSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _jobs.FirstOrDefault(j => j.Id == id)?.Snapshot;
        }

        if (snapshot is null || !snapshot.IsFinished || snapshot.State == TransferState.Completed)
        {
            return null;
        }

        // Skip policy: files that already arrived are left alone, so only the missing ones are transferred again.
        var request = snapshot.Request.Conflict == ConflictPolicy.Overwrite ? snapshot.Request : snapshot.Request with { Conflict = ConflictPolicy.Skip };
        if (snapshot.Request.Conflict == ConflictPolicy.KeepBoth)
        {
            request = snapshot.Request with { Conflict = ConflictPolicy.Skip };
        }

        return Enqueue(request);
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.Snapshot.IsFinished);
        }

        RaiseChanged(force: true);
    }

    private Job CreateJob(TransferRequest request)
    {
        var title = (request.Direction == TransferDirection.Upload ? "Send to " : "Save from ") + request.DeviceName;
        var job = new Job(request, title);
        lock (_gate)
        {
            _jobs.Add(job);
        }

        RaiseChanged(force: true);
        return job;
    }

    private async Task ExecuteAsync(Job job, CancellationToken external)
    {
        var deviceGate = _perDevice.GetOrAdd(job.Request.DeviceSerial, _ => new SemaphoreSlim(1));
        var token = job.Cancel.Token;
        var gatesHeld = 0;
        try
        {
            await deviceGate.WaitAsync(token).ConfigureAwait(false);
            gatesHeld = 1;
            await _global.WaitAsync(token).ConfigureAwait(false);
            gatesHeld = 2;

            var clock = Stopwatch.StartNew();
            Update(job, s => s with { State = TransferState.Running, Message = "Preparing..." }, force: true);
            if (job.Request.Direction == TransferDirection.Download)
            {
                await DownloadAsync(job, clock, token).ConfigureAwait(false);
            }
            else
            {
                await UploadAsync(job, clock, token).ConfigureAwait(false);
            }

            Finish(job, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Update(job, s => s with { State = TransferState.Cancelled, Message = "Cancelled.", CurrentItem = null }, force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A transfer failed unexpectedly.");
            Update(job, s => s with { State = TransferState.Failed, Message = ex.Message, CurrentItem = null }, force: true);
        }
        finally
        {
            if (gatesHeld >= 2)
            {
                _global.Release();
            }

            if (gatesHeld >= 1)
            {
                deviceGate.Release();
            }
        }
    }

    private void Finish(Job job, TimeSpan elapsed)
    {
        var s = job.Snapshot;
        var state = s.FilesFailed == 0 ? TransferState.Completed : s.FilesDone + s.FilesSkipped == 0 ? TransferState.Failed : TransferState.CompletedWithErrors;
        var message = $"{s.FilesDone} transferred" + (s.FilesSkipped > 0 ? $", {s.FilesSkipped} skipped" : string.Empty) + (s.FilesFailed > 0 ? $", {s.FilesFailed} failed" : string.Empty) + ".";
        Update(job, x => x with { State = state, Message = message, CurrentItem = null, Elapsed = elapsed, BytesPerSecond = 0 }, force: true);
    }

    // ----- Download: device -> Windows -----

    private async Task DownloadAsync(Job job, Stopwatch clock, CancellationToken token)
    {
        var request = job.Request;
        var destinationRoot = request.DestinationDirectory;
        if (!Path.IsPathFullyQualified(destinationRoot))
        {
            throw new InvalidOperationException("The destination folder is not a full path.");
        }

        Directory.CreateDirectory(destinationRoot);

        // 1. Expand folders into a flat list of files (and empty folders) so progress and totals are real numbers.
        var plan = new List<(string Remote, string Local, long? Size)>();
        long total = 0;
        foreach (var source in request.SourcePaths)
        {
            token.ThrowIfCancellationRequested();
            var remote = RemotePath.Normalize(source);
            if (remote is null || remote == "/")
            {
                Fail(job, source, "That is not a valid device path.");
                continue;
            }

            var stat = await _files.StatAsync(request.DeviceSerial, remote, token).ConfigureAwait(false);
            if (!stat.Succeeded)
            {
                Fail(job, remote, stat.ErrorMessage);
                continue;
            }

            var localBase = Path.Combine(destinationRoot, LocalPathRules.SanitizeName(RemotePath.GetName(remote)));
            if (!stat.Value.IsDirectory)
            {
                plan.Add((remote, localBase, stat.Value.Size));
                total += stat.Value.Size ?? 0;
                continue;
            }

            await ExpandDirectoryAsync(job, remote, localBase, plan, token).ConfigureAwait(false);
        }

        total = plan.Sum(p => p.Size ?? 0);
        Update(job, s => s with { FilesTotal = plan.Count + s.FilesFailed, BytesTotal = total });

        // 2. Pull each file. One failing file is recorded and the rest continue.
        foreach (var (remote, local, size) in plan)
        {
            token.ThrowIfCancellationRequested();
            Update(job, s => s with { CurrentItem = RemotePath.GetName(remote), Elapsed = clock.Elapsed });

            if (!LocalPathRules.IsInside(destinationRoot, local))
            {
                Fail(job, remote, "The file name would leave the destination folder.");
                continue;
            }

            var target = local;
            if (File.Exists(target) || Directory.Exists(target))
            {
                switch (request.Conflict)
                {
                    case ConflictPolicy.Skip:
                        SetFileSkipped(job, size ?? 0);
                        continue;
                    case ConflictPolicy.KeepBoth:
                        var folder = Path.GetDirectoryName(target)!;
                        target = Path.Combine(folder, RemotePath.UniqueName(Path.GetFileName(target), n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n))));
                        break;
                    case ConflictPolicy.Overwrite when Directory.Exists(target):
                        Fail(job, remote, "A folder with this name already exists on the computer.");
                        continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Pull into a temporary name, then move into place, so a cancelled or failed pull never leaves a half file
            // under the real name (and never destroys the file being overwritten).
            var temp = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];
            var progress = StartProgressWatcher(job, temp, clock, size, token);
            OperationResult<AdbCommandOutput> pull;
            try
            {
                pull = await _adb.PullAsync(request.DeviceSerial, remote, temp, token).ConfigureAwait(false);
            }
            finally
            {
                await progress.StopAsync().ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
            {
                TryDelete(temp);
                throw new OperationCanceledException(token);
            }

            var problem = PullProblem(pull, temp);
            if (problem is not null)
            {
                TryDelete(temp);
                Fail(job, remote, problem);
                continue;
            }

            try
            {
                File.Move(temp, target, overwrite: request.Conflict == ConflictPolicy.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                Fail(job, remote, "Could not save the file on this computer: " + ex.Message);
                continue;
            }

            var actual = new FileInfo(target).Length;
            SetFileDone(job, actual, clock);
        }
    }

    private async Task ExpandDirectoryAsync(Job job, string remote, string local, List<(string, string, long?)> plan, CancellationToken token)
    {
        var listing = await _files.ListAsync(job.Request.DeviceSerial, remote, token).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            Fail(job, remote, listing.ErrorMessage);
            return;
        }

        if (listing.Value.Entries.Count == 0)
        {
            Directory.CreateDirectory(local);
        }

        foreach (var entry in listing.Value.Entries)
        {
            token.ThrowIfCancellationRequested();
            var childLocal = Path.Combine(local, LocalPathRules.SanitizeName(entry.Name));
            if (entry.Kind == RemoteEntryKind.Directory)
            {
                await ExpandDirectoryAsync(job, entry.FullPath, childLocal, plan, token).ConfigureAwait(false);
            }
            else if (entry.Kind == RemoteEntryKind.File || entry.Kind == RemoteEntryKind.Link)
            {
                plan.Add((entry.FullPath, childLocal, entry.Size));
            }
            else
            {
                Fail(job, entry.FullPath, "Special files (sockets, devices) cannot be copied.");
            }
        }
    }

    private static string? PullProblem(OperationResult<AdbCommandOutput> pull, string temp)
    {
        if (!pull.Succeeded)
        {
            return pull.ErrorMessage;
        }

        var output = pull.Value;
        var text = output.Combined;
        // "1 file pulled" appears on success; adb prints errors as "adb: error: ..." and may still exit 0.
        var failed = output.ExitCode != 0
            || text.Contains("adb: error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to copy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("remote object", StringComparison.OrdinalIgnoreCase) && text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(temp);
        if (!failed)
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("permission denied", StringComparison.Ordinal))
        {
            return "Android did not allow reading this file (permission denied).";
        }

        if (lower.Contains("no such file", StringComparison.Ordinal) || lower.Contains("does not exist", StringComparison.Ordinal))
        {
            return "The file no longer exists on the device.";
        }

        if (lower.Contains("no space left", StringComparison.Ordinal))
        {
            return "This computer has no space left.";
        }

        if (lower.Contains("offline", StringComparison.Ordinal) || lower.Contains("not found", StringComparison.Ordinal) && lower.Contains("device", StringComparison.Ordinal))
        {
            return "The device disconnected during the transfer.";
        }

        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(first) ? "adb did not report success." : first;
    }

    // ----- Upload: Windows -> device -----

    private async Task UploadAsync(Job job, Stopwatch clock, CancellationToken token)
    {
        var request = job.Request;
        var destinationRoot = RemotePath.Normalize(request.DestinationDirectory);
        if (destinationRoot is null)
        {
            throw new InvalidOperationException("The destination is not a valid device path.");
        }

        var classification = StorageClassifier.Classify(destinationRoot);
        if (classification.WritesBlocked)
        {
            throw new InvalidOperationException($"{classification.Title}: this app does not copy files here. {classification.Explanation}");
        }

        // 1. Expand local folders into files. Symlinks and junctions are skipped, not followed, to avoid loops.
        var plan = new List<(string Local, string Remote, long Size)>();
        var emptyFolders = new List<string>();
        foreach (var source in request.SourcePaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(source);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Fail(job, source, "Links and junctions are not followed.");
                    continue;
                }

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!RemotePath.IsSafeName(name))
                {
                    Fail(job, source, "This name cannot be used on Android.");
                    continue;
                }

                var remoteBase = RemotePath.Combine(destinationRoot, name);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ExpandLocal(job, source, remoteBase, plan, emptyFolders);
                }
                else
                {
                    plan.Add((source, remoteBase, new FileInfo(source).Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Fail(job, source, "Could not read this item on the computer: " + ex.Message);
            }
        }

        Update(job, s => s with { FilesTotal = plan.Count + s.FilesFailed, BytesTotal = plan.Sum(p => p.Size) });

        // 2. Learn what already exists at the top level so conflict policy can apply to the top-level names.
        var topNames = request.SourcePaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).Where(RemotePath.IsSafeName).Distinct().ToList();
        var existingResult = await _files.FindExistingAsync(request.DeviceSerial, destinationRoot, topNames, token).ConfigureAwait(false);
        if (!existingResult.Succeeded)
        {
            throw new InvalidOperationException(existingResult.ErrorMessage);
        }

        var existing = new HashSet<string>(existingResult.Value, StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in topNames.Where(existing.Contains))
        {
            if (request.Conflict == ConflictPolicy.KeepBoth)
            {
                var unique = RemotePath.UniqueName(name, n => existing.Contains(n) || renamed.ContainsValue(n));
                renamed[name] = unique;
            }
        }

        foreach (var folder in emptyFolders)
        {
            var mapped = MapTop(folder, destinationRoot, renamed);
            var created = await EnsureRemoteFolderAsync(request.DeviceSerial, mapped, token).ConfigureAwait(false);
            if (created is not null)
            {
                Fail(job, mapped, created);
            }
        }

        foreach (var (local, remoteOriginal, size) in plan)
        {
            token.ThrowIfCancellationRequested();
            Update(job, s => s with { CurrentItem = Path.GetFileName(local), Elapsed = clock.Elapsed });

            var topName = RemotePath.GetName(TopLevelOf(remoteOriginal, destinationRoot));
            var remote = MapTop(remoteOriginal, destinationRoot, renamed);
            var isTopCollision = existing.Contains(topName);
            if (isTopCollision && request.Conflict == ConflictPolicy.Skip && remote == remoteOriginal)
            {
                // Skip only skips what is really there: for a folder, check the individual file.
                var stat = await _files.StatAsync(request.DeviceSerial, remote, token).ConfigureAwait(false);
                if (stat.Succeeded)
                {
                    SetFileSkipped(job, size);
                    continue;
                }
            }
            else if (isTopCollision && request.Conflict == ConflictPolicy.Overwrite)
            {
                var stat = await _files.StatAsync(request.DeviceSerial, remote, token).ConfigureAwait(false);
                if (stat.Succeeded && stat.Value.IsDirectory)
                {
                    Fail(job, local, "A folder with this name exists on the device.");
                    continue;
                }
            }

            var parent = RemotePath.GetParent(remote)!;
            if (parent != destinationRoot)
            {
                var mk = await EnsureRemoteFolderAsync(job.Request.DeviceSerial, parent, token).ConfigureAwait(false);
                if (mk is not null)
                {
                    Fail(job, local, mk);
                    continue;
                }
            }

            var progress = StartRemoteProgress(job, clock, size, token);
            OperationResult<AdbCommandOutput> push;
            try
            {
                push = await _adb.PushAsync(request.DeviceSerial, local, remote, token).ConfigureAwait(false);
            }
            finally
            {
                await progress.StopAsync().ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            var problem = PushProblem(push);
            if (problem is not null)
            {
                Fail(job, local, problem);
                continue;
            }

            SetFileDone(job, size, clock);
        }
    }

    private void ExpandLocal(Job job, string directory, string remoteBase, List<(string, string, long)> plan, List<string> emptyFolders)
    {
        var any = false;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            any = true;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Fail(job, entry.FullName, "Links and junctions are not followed.");
                continue;
            }

            if (!RemotePath.IsSafeName(entry.Name))
            {
                Fail(job, entry.FullName, "This name cannot be used on Android.");
                continue;
            }

            var remote = RemotePath.Combine(remoteBase, entry.Name);
            if (entry is DirectoryInfo sub)
            {
                ExpandLocal(job, sub.FullName, remote, plan, emptyFolders);
            }
            else if (entry is FileInfo file)
            {
                plan.Add((file.FullName, remote, file.Length));
            }
        }

        if (!any)
        {
            emptyFolders.Add(remoteBase);
        }
    }

    private static string TopLevelOf(string remote, string root)
    {
        var relative = remote[(root == "/" ? 1 : root.Length + 1)..];
        var first = relative.Split('/')[0];
        return RemotePath.Combine(root, first);
    }

    private static string MapTop(string remote, string root, Dictionary<string, string> renamed)
    {
        var top = TopLevelOf(remote, root);
        var name = RemotePath.GetName(top);
        return renamed.TryGetValue(name, out var unique) ? RemotePath.Combine(root, unique) + remote[top.Length..] : remote;
    }

    private async Task<string?> EnsureRemoteFolderAsync(string serial, string path, CancellationToken token)
    {
        var stat = await _files.StatAsync(serial, path, token).ConfigureAwait(false);
        if (stat.Succeeded)
        {
            return stat.Value.IsDirectory ? null : $"\"{RemotePath.GetName(path)}\" exists on the device and is not a folder.";
        }

        var parent = RemotePath.GetParent(path);
        if (parent is null)
        {
            return "Cannot create the folder on the device.";
        }

        var up = await EnsureRemoteFolderAsync(serial, parent, token).ConfigureAwait(false);
        if (up is not null)
        {
            return up;
        }

        var created = await _files.CreateFolderAsync(serial, parent, RemotePath.GetName(path), token).ConfigureAwait(false);
        return created.Succeeded ? null : created.ErrorMessage;
    }

    private static string? PushProblem(OperationResult<AdbCommandOutput> push)
    {
        if (!push.Succeeded)
        {
            return push.ErrorMessage;
        }

        var output = push.Value;
        var text = output.Combined;
        var lower = text.ToLowerInvariant();
        var succeeded = output.ExitCode == 0
            && !lower.Contains("adb: error", StringComparison.Ordinal)
            && !lower.Contains("failed to copy", StringComparison.Ordinal)
            && !lower.Contains("no such file", StringComparison.Ordinal);
        if (succeeded)
        {
            return null;
        }

        if (lower.Contains("permission denied", StringComparison.Ordinal) || lower.Contains("read-only", StringComparison.Ordinal) || lower.Contains("operation not permitted", StringComparison.Ordinal))
        {
            return "Android did not allow writing to this folder.";
        }

        if (lower.Contains("no space left", StringComparison.Ordinal))
        {
            return "The device has no space left.";
        }

        if (lower.Contains("no such file", StringComparison.Ordinal))
        {
            return "The destination folder does not exist on the device.";
        }

        if (lower.Contains("offline", StringComparison.Ordinal))
        {
            return "The device disconnected during the transfer.";
        }

        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(first) ? "adb did not report success." : first;
    }

    // ----- progress and bookkeeping -----

    private sealed class Watcher(CancellationTokenSource stop, Task task)
    {
        public async Task StopAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            stop.Dispose();
        }
    }

    /// <summary>For downloads: watches the size of the temporary file, which is the real number of bytes received.</summary>
    private Watcher StartProgressWatcher(Job job, string tempFile, Stopwatch clock, long? expected, CancellationToken token)
    {
        var baseBytes = job.CompletedBytes + job.SkippedBytes;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var task = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(300, stop.Token).ConfigureAwait(false);
                    if (File.Exists(tempFile))
                    {
                        var current = new FileInfo(tempFile).Length;
                        Update(job, s => s with { BytesDone = baseBytes + current, BytesPerSecond = Rate(baseBytes + current, clock), Elapsed = clock.Elapsed });
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
        }, CancellationToken.None);
        return new Watcher(stop, task);
    }

    /// <summary>
    /// For uploads adb does not expose progress through a pipe-free API, so only elapsed time and the current file are
    /// shown while a file is in flight; bytes advance when each file completes. The UI says so instead of faking a bar.
    /// </summary>
    private Watcher StartRemoteProgress(Job job, Stopwatch clock, long size, CancellationToken token)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var task = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, stop.Token).ConfigureAwait(false);
                    Update(job, s => s with { Elapsed = clock.Elapsed });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);
        return new Watcher(stop, task);
    }

    private void SetFileDone(Job job, long actualSize, Stopwatch clock)
    {
        // BytesDone may include partial progress for this file; settle it to completed + skipped bytes.
        job.CompletedBytes += actualSize;
        var settled = job.CompletedBytes + job.SkippedBytes;
        Update(job, s => s with { FilesDone = s.FilesDone + 1, BytesDone = settled, BytesPerSecond = Rate(settled, clock), Elapsed = clock.Elapsed }, force: true);
    }

    private void SetFileSkipped(Job job, long size)
    {
        job.SkippedBytes += size;
        Update(job, s => s with { FilesSkipped = s.FilesSkipped + 1, BytesDone = job.CompletedBytes + job.SkippedBytes });
    }

    private static double Rate(long bytes, Stopwatch clock) => clock.Elapsed.TotalSeconds < 0.5 ? 0 : bytes / clock.Elapsed.TotalSeconds;

    private void Fail(Job job, string path, string message)
    {
        _logger.LogWarning("Transfer item failed: {Message}", message);
        Update(job, s => s with { FilesFailed = s.FilesFailed + 1, Failures = s.Failures.Append(new TransferFailure(path, message)).ToList() }, force: true);
    }

    private void Update(Job job, Func<TransferSnapshot, TransferSnapshot> change, bool force = false)
    {
        lock (_gate)
        {
            job.Snapshot = change(job.Snapshot);
        }

        RaiseChanged(force);
    }

    private void RaiseChanged(bool force)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastNotify).TotalMilliseconds < 250)
            {
                return;
            }

            _lastNotify = now;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Job
    {
        public Job(TransferRequest request, string title)
        {
            Request = request;
            Id = Guid.NewGuid();
            Snapshot = new TransferSnapshot(
                Id, request, TransferState.Queued, title, 0, null, 0, 0, 0, null, null, 0, TimeSpan.Zero, Array.Empty<TransferFailure>(), "Waiting in the queue.", DateTimeOffset.Now);
        }

        public Guid Id { get; }

        public TransferRequest Request { get; }

        public TransferSnapshot Snapshot { get; set; }

        public CancellationTokenSource Cancel { get; } = new();

        public long CompletedBytes { get; set; }

        public long SkippedBytes { get; set; }
    }
}
