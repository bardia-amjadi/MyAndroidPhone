using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Transfers;
using MyAndroidPhone.Core.Workspace;

namespace MyAndroidPhone.Android;

public enum TerminalLineKind
{
    Output,
    Error,
    Info,
    Success,
    Warning,
    Command,
    Directory,
    Hint
}

public interface ITerminalOutput
{
    /// <summary>Called from any thread.</summary>
    void Write(TerminalLineKind kind, string text);
}

/// <summary>What the terminal asks the desktop to do. Implemented by the app.</summary>
public interface ITerminalHost
{
    void OpenInNotepad(string virtualPath);

    void OpenInExplorer(string virtualFolder);

    void ClearScreen();
}

public sealed record CommandInfo(string Name, string Usage, string Description, string[] Examples);

public sealed record Completion(int ReplaceStart, IReadOnlyList<string> Options);

/// <summary>
/// The terminal's brain: built-in "manual" commands (ls, cd, open, pull, show ...) and adb pass-through, always for the one
/// phone of the open instance. Commands never go through a PC shell: arguments are passed to adb one by one, and the only
/// text interpreted by a shell is what the user types after "shell", which runs on the phone.
/// </summary>
public sealed class TerminalEngine
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);
    private const int CatLimit = 256 * 1024;

    // adb commands that would change the connection, restart the server for every instance, or pretend to unlock the phone.
    private static readonly Dictionary<string, string> BlockedAdb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["root"] = "This app does not root or restart adbd as root.",
        ["unroot"] = "This app does not change adbd's privileges.",
        ["remount"] = "This app does not remount system partitions.",
        ["disable-verity"] = "This app does not change verified boot.",
        ["enable-verity"] = "This app does not change verified boot.",
        ["kill-server"] = "Restarting adb would disconnect every instance. Use Connections in the Device Center.",
        ["start-server"] = "adb is started by the app. Use Connections in the Device Center.",
        ["connect"] = "Connections are managed in the Device Center (Connections).",
        ["disconnect"] = "Connections are managed in the Device Center (Connections).",
        ["pair"] = "Pairing is done in the Device Center (Connections).",
        ["tcpip"] = "Switching to Wi-Fi is done in the Device Center (Connections).",
        ["usb"] = "Switching connection mode is done in the Device Center (Connections).",
        ["devices"] = "Use the built-in \"devices\" command; this terminal only talks to one phone.",
        ["wait-for-device"] = "Not needed: this terminal is bound to one phone.",
        ["-s"] = "The phone is chosen by the instance; you cannot target another one here.",
        ["-d"] = "The phone is chosen by the instance.",
        ["-e"] = "The phone is chosen by the instance.",
        ["-t"] = "The phone is chosen by the instance.",
        ["-H"] = "The adb server is managed by the app.",
        ["-P"] = "The adb server is managed by the app.",
        ["-L"] = "The adb server is managed by the app."
    };

    private static readonly string[] ShowTopics = { "logs", "battery", "wifi", "storage", "memory", "device", "transfers" };

    private static readonly string[] AdbHints =
    {
        "shell", "logcat", "install", "uninstall", "push", "pull", "reboot", "forward", "reverse", "get-state", "bugreport", "exec-out", "version", "help"
    };

    private readonly VirtualFileSystem _vfs;
    private readonly IAdbClient _adb;
    private readonly IFileExplorerService _files;
    private readonly ITransferService _transfers;
    private readonly IDeviceStatusService _status;
    private readonly IDeviceService _devices;
    private readonly IInstanceWorkspace _workspace;
    private readonly ITerminalHost _host;
    private readonly Func<string?> _serial;
    private readonly Func<string> _instanceName;
    private readonly ILogger _logger;
    private readonly List<string> _history = new();
    private readonly Dictionary<string, (CommandInfo Info, Func<Context, Task> Run)> _commands = new(StringComparer.OrdinalIgnoreCase);

    public TerminalEngine(
        VirtualFileSystem vfs,
        IAdbClient adb,
        IFileExplorerService files,
        ITransferService transfers,
        IDeviceStatusService status,
        IDeviceService devices,
        IInstanceWorkspace workspace,
        ITerminalHost host,
        Func<string?> serial,
        Func<string> instanceName,
        ILogger logger)
    {
        _vfs = vfs;
        _adb = adb;
        _files = files;
        _transfers = transfers;
        _status = status;
        _devices = devices;
        _workspace = workspace;
        _host = host;
        _serial = serial;
        _instanceName = instanceName;
        _logger = logger;
        Cwd = new VirtualLocation(VirtualArea.Android, "/sdcard");
        RegisterCommands();
    }

    public VirtualLocation Cwd { get; private set; }

    public string Prompt => $"{_instanceName()}:{Cwd.Display}$";

    public IReadOnlyList<string> History => _history;

    public IReadOnlyList<CommandInfo> Commands => _commands.Values.Select(c => c.Info).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    public bool IsKnownCommand(string name) => _commands.ContainsKey(name);

    private sealed record Context(string Line, IReadOnlyList<string> Words, ITerminalOutput Out, CancellationToken Cancel)
    {
        public IReadOnlyList<string> Args => Words.Skip(1).ToList();

        public IReadOnlyList<string> PositionalArgs => Words.Skip(1).Where(w => !(w.StartsWith('-') && w.Length > 1)).ToList();

        public bool HasFlag(params string[] names) => Words.Skip(1).Any(w => names.Contains(w, StringComparer.Ordinal));

        public void Print(string text, TerminalLineKind kind = TerminalLineKind.Output) => Out.Write(kind, text);

        public void Fail(string message) => Out.Write(TerminalLineKind.Error, message);
    }

    // ----- execution -----

    public async Task ExecuteAsync(string line, ITerminalOutput output, CancellationToken cancellationToken)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }

        _history.Add(line);
        if (_history.Count > 500)
        {
            _history.RemoveAt(0);
        }

        _workspace.Log("terminal", "> " + line);
        var words = CommandLine.Words(line);
        var context = new Context(line, words, new LoggingOutput(output, _workspace), cancellationToken);
        if (words.Count == 0)
        {
            return;
        }

        if (!_commands.TryGetValue(words[0], out var command))
        {
            context.Fail($"Unknown command \"{words[0]}\". Type help for the list. To run a command on the phone use: shell {words[0]} ...");
            return;
        }

        try
        {
            await command.Run(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.Print("^C stopped.", TerminalLineKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A terminal command failed unexpectedly.");
            context.Fail("The command failed: " + ex.Message);
        }
    }

    private sealed class LoggingOutput(ITerminalOutput inner, IInstanceWorkspace workspace) : ITerminalOutput
    {
        private int _logged;

        public void Write(TerminalLineKind kind, string text)
        {
            inner.Write(kind, text);

            // The session log keeps errors, results and a bounded amount of normal output, not a full copy of a logcat stream.
            if (kind is TerminalLineKind.Error or TerminalLineKind.Warning or TerminalLineKind.Success || Interlocked.Increment(ref _logged) <= 40)
            {
                workspace.Log("terminal", (kind == TerminalLineKind.Error ? "error: " : string.Empty) + text);
            }
        }
    }

    // ----- command registry -----

    private void Add(string name, string usage, string description, string[] examples, Func<Context, Task> run) =>
        _commands[name] = (new CommandInfo(name, usage, description, examples), run);

    private void RegisterCommands()
    {
        Add("help", "help [command]", "List the commands, or explain one.", new[] { "help", "help pull" }, Help);
        Add("clear", "clear", "Clear the screen.", new[] { "clear" }, c => { _host.ClearScreen(); return Task.CompletedTask; });
        Add("echo", "echo <text>", "Print text.", new[] { "echo hello" }, c => { c.Print(CommandLine.RestAfter(c.Line, 1)); return Task.CompletedTask; });
        Add("history", "history", "Show the commands you typed in this session.", new[] { "history" }, c =>
        {
            for (var i = 0; i < _history.Count; i++)
            {
                c.Print($"{i + 1,4}  {_history[i]}");
            }

            return Task.CompletedTask;
        });
        Add("pwd", "pwd", "Show the current folder.", new[] { "pwd" }, c => { c.Print(Cwd.Display); return Task.CompletedTask; });
        Add("cd", "cd [folder]", "Change folder. / holds .android (the phone) and .system (this instance's space on the PC).", new[] { "cd /sdcard/DCIM", "cd ..", "cd /.system/logs" }, ChangeDirectory);
        Add("ls", "ls [-l] [folder]", "List a folder. -l shows size and date.", new[] { "ls", "ls -l /sdcard/Download", "ls /.system" }, List);
        Add("cat", "cat <file>", "Print a text file (up to 256 KB).", new[] { "cat /sdcard/notes.txt" }, Cat);
        Add("open", "open <file or folder>", "Open a file in Notepad, or a folder in File Explorer.", new[] { "open /.system/logs/2026-10-31_18-20-11.log", "open /sdcard/Download" }, Open);
        Add("mkdir", "mkdir <folder>", "Create a folder.", new[] { "mkdir /sdcard/Backup" }, MakeDirectory);
        Add("rm", "rm [-r] -y <path> ...", "Delete files or folders. Needs -y because it cannot be undone.", new[] { "rm -y /sdcard/old.txt", "rm -r -y /sdcard/Temp" }, Remove);
        Add("mv", "mv <path> <new name | folder>", "Rename an item, or move it into a folder on the phone.", new[] { "mv /sdcard/a.txt b.txt", "mv /sdcard/a.txt /sdcard/Documents" }, Move);
        Add("pull", "pull <phone path> [PC folder]", "Copy from the phone to this PC (default: /.system/downloads).", new[] { "pull /sdcard/DCIM/photo.jpg", "pull /sdcard/Download C:\\Backup" }, Pull);
        Add("push", "push <PC path> [phone folder]", "Copy from this PC to the phone (default: the current folder).", new[] { "push C:\\Temp\\song.mp3 /sdcard/Music" }, Push);
        Add("show", "show <logs|battery|wifi|storage|memory|device|transfers>", "Show information. \"show logs\" opens the session log in Notepad.", new[] { "show logs", "show battery", "show wifi" }, Show);
        Add("info", "info", "Summary of the phone: model, battery, Wi-Fi, storage, memory.", new[] { "info" }, c => Show(c with { Words = new[] { "show", "device" } }));
        Add("devices", "devices", "List the phones this app knows and their state.", new[] { "devices" }, Devices);
        Add("screenshot", "screenshot", "Save a screenshot of the phone in /.system/screenshots.", new[] { "screenshot" }, Screenshot);
        Add("adb", "adb <command> [arguments]", "Run an adb command for this phone. The phone is chosen for you.", new[] { "adb get-state", "adb install C:\\apps\\app.apk", "adb reboot" }, Adb);
        Add("shell", "shell <command>", "Run a command on the phone's shell. Interactive programs are not supported.", new[] { "shell getprop ro.product.model", "shell ls -la /sdcard" }, Shell);
        Add("logcat", "logcat [options]", "Stream the phone's log. Press Stop (Ctrl+C) to end it.", new[] { "logcat", "logcat -s MyTag" }, Logcat);
    }

    // ----- helpers -----

    private string? RequireSerial(Context c)
    {
        var serial = _serial();
        if (serial is null)
        {
            c.Fail("No phone is connected for this instance. Open the Device Center and choose Reconnect.");
        }

        return serial;
    }

    private bool TryResolve(Context c, string? input, out VirtualLocation location)
    {
        var resolved = VirtualPath.Resolve(Cwd, input);
        if (resolved is null)
        {
            c.Fail($"\"{input}\" is not a valid path.");
            location = VirtualLocation.Root;
            return false;
        }

        location = resolved;
        return true;
    }

    private static void Report(Context c, OperationError? error)
    {
        c.Fail(error?.Message ?? "The command failed.");
        if (error?.Kind == ErrorKind.AccessDenied && error.Detail is { Length: > 0 } detail && detail.Length < 400)
        {
            c.Print(detail, TerminalLineKind.Hint);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    // ----- built-in commands -----

    private Task Help(Context c)
    {
        var name = c.Args.FirstOrDefault();
        if (name is not null)
        {
            if (!_commands.TryGetValue(name, out var found))
            {
                c.Fail($"No command called \"{name}\".");
                return Task.CompletedTask;
            }

            c.Print("Usage:  " + found.Info.Usage, TerminalLineKind.Info);
            c.Print(found.Info.Description);
            foreach (var example in found.Info.Examples)
            {
                c.Print("  " + example, TerminalLineKind.Hint);
            }

            return Task.CompletedTask;
        }

        c.Print("Commands (type help <command> for details):", TerminalLineKind.Info);
        var width = _commands.Keys.Max(k => k.Length);
        foreach (var info in Commands)
        {
            c.Print($"  {info.Name.PadRight(width)}  {info.Description}");
        }

        c.Print(string.Empty);
        c.Print("Places: /.android is the phone, /.system is this instance's space on the PC (logs, screenshots, downloads).", TerminalLineKind.Hint);
        c.Print("Keys: Tab completes, Up/Down browse history, Ctrl+C stops a running command, right-click for copy and paste.", TerminalLineKind.Hint);
        return Task.CompletedTask;
    }

    private async Task ChangeDirectory(Context c)
    {
        var target = c.PositionalArgs.FirstOrDefault();
        VirtualLocation location;
        if (target is null)
        {
            location = new VirtualLocation(VirtualArea.Android, "/sdcard");
        }
        else if (!TryResolve(c, target, out location))
        {
            return;
        }

        var stat = await _vfs.StatAsync(_serial(), location, c.Cancel).ConfigureAwait(false);
        if (!stat.Succeeded)
        {
            Report(c, stat.Error);
            return;
        }

        if (!stat.Value.IsDirectory && stat.Value.Kind != RemoteEntryKind.Link)
        {
            c.Fail($"\"{location.Display}\" is not a folder.");
            return;
        }

        Cwd = location;
    }

    private async Task List(Context c)
    {
        var target = c.PositionalArgs.FirstOrDefault();
        var location = Cwd;
        if (target is not null && !TryResolve(c, target, out location))
        {
            return;
        }

        var listing = await _vfs.ListAsync(_serial(), location, c.Cancel).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            Report(c, listing.Error);
            return;
        }

        var entries = listing.Value.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (entries.Count == 0)
        {
            c.Print("(empty)", TerminalLineKind.Hint);
            return;
        }

        var detailed = c.HasFlag("-l", "-la", "-al");
        foreach (var e in entries)
        {
            var name = e.Name + (e.IsDirectory ? "/" : e.Kind == RemoteEntryKind.Link && e.LinkTarget is { } t ? " -> " + t : string.Empty);
            if (detailed)
            {
                var size = e.IsDirectory ? "-" : FormatBytes(e.Size ?? 0);
                var date = e.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
                c.Print($"{(e.Permissions ?? (e.IsDirectory ? "d" : "-")),-11} {size,9}  {date,-16}  {name}", e.IsDirectory ? TerminalLineKind.Directory : TerminalLineKind.Output);
            }
            else
            {
                c.Print(name, e.IsDirectory ? TerminalLineKind.Directory : TerminalLineKind.Output);
            }
        }
    }

    private async Task Cat(Context c)
    {
        var target = c.PositionalArgs.FirstOrDefault();
        if (target is null)
        {
            c.Fail("Usage: cat <file>");
            return;
        }

        if (!TryResolve(c, target, out var location))
        {
            return;
        }

        var read = await _vfs.ReadTextAsync(_serial(), location, CatLimit, c.Cancel).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            Report(c, read.Error);
            return;
        }

        foreach (var line in read.Value.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            c.Print(line);
        }

        if (read.Value.Truncated)
        {
            c.Print($"... showing the first {FormatBytes(CatLimit)} of {FormatBytes(read.Value.TotalBytes)}. Use open to see the rest.", TerminalLineKind.Warning);
        }
    }

    private async Task Open(Context c)
    {
        var target = c.PositionalArgs.FirstOrDefault();
        if (target is null)
        {
            c.Fail("Usage: open <file or folder>   for example: open /.system/logs/2026-10-31_18-20-11.log");
            return;
        }

        if (!TryResolve(c, target, out var location))
        {
            return;
        }

        var stat = await _vfs.StatAsync(_serial(), location, c.Cancel).ConfigureAwait(false);
        if (!stat.Succeeded)
        {
            Report(c, stat.Error);
            return;
        }

        if (stat.Value.IsDirectory)
        {
            _host.OpenInExplorer(location.Display);
            c.Print("Opened " + location.Display + " in File Explorer.", TerminalLineKind.Success);
        }
        else
        {
            _host.OpenInNotepad(location.Display);
            c.Print("Opened " + location.Display + " in Notepad.", TerminalLineKind.Success);
        }
    }

    private async Task MakeDirectory(Context c)
    {
        var target = c.PositionalArgs.FirstOrDefault();
        if (target is null || !TryResolve(c, target, out var location))
        {
            if (target is null)
            {
                c.Fail("Usage: mkdir <folder>");
            }

            return;
        }

        if (location.Parent is null)
        {
            c.Fail("Choose a folder name inside .android or .system.");
            return;
        }

        var made = await _vfs.CreateFolderAsync(_serial(), location.Parent, location.Name, c.Cancel).ConfigureAwait(false);
        if (made.Succeeded)
        {
            c.Print("Created " + made.Value, TerminalLineKind.Success);
        }
        else
        {
            Report(c, made.Error);
        }
    }

    private async Task Remove(Context c)
    {
        var targets = new List<VirtualLocation>();
        foreach (var arg in c.PositionalArgs)
        {
            if (!TryResolve(c, arg, out var location))
            {
                return;
            }

            targets.Add(location);
        }

        if (targets.Count == 0)
        {
            c.Fail("Usage: rm [-r] -y <path> ...");
            return;
        }

        var confirmed = c.HasFlag("-y", "--yes");
        if (!confirmed)
        {
            c.Print($"This permanently deletes {targets.Count} item(s) and cannot be undone. Run again with -y to confirm:", TerminalLineKind.Warning);
            c.Print("  rm -y " + string.Join(' ', c.PositionalArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), TerminalLineKind.Hint);
            return;
        }

        // Folders need an explicit -r so that a typo does not remove a whole tree.
        var recursive = c.HasFlag("-r", "-R", "-rf", "-fr");
        foreach (var target in targets)
        {
            var stat = await _vfs.StatAsync(_serial(), target, c.Cancel).ConfigureAwait(false);
            if (stat.Succeeded && stat.Value.IsDirectory && !recursive)
            {
                c.Fail($"\"{target.Display}\" is a folder. Add -r to delete it with everything inside.");
                return;
            }
        }

        var result = await _vfs.DeleteAsync(_serial(), targets, c.Cancel).ConfigureAwait(false);
        if (result.Succeeded)
        {
            c.Print(result.Value, TerminalLineKind.Success);
        }
        else
        {
            Report(c, result.Error);
        }
    }

    private async Task Move(Context c)
    {
        var args = c.PositionalArgs;
        if (args.Count != 2 || !TryResolve(c, args[0], out var from))
        {
            if (args.Count != 2)
            {
                c.Fail("Usage: mv <path> <new name | folder>");
            }

            return;
        }

        if (!args[1].Contains('/') && !args[1].Contains('\\') && RemotePath.IsSafeName(args[1]) && args[1] != ".")
        {
            var renamed = await _vfs.RenameAsync(_serial(), from, args[1], c.Cancel).ConfigureAwait(false);
            if (renamed.Succeeded)
            {
                c.Print("Renamed to " + renamed.Value, TerminalLineKind.Success);
            }
            else
            {
                Report(c, renamed.Error);
            }

            return;
        }

        if (!TryResolve(c, args[1], out var folder))
        {
            return;
        }

        if (from.Area != VirtualArea.Android || folder.Area != VirtualArea.Android)
        {
            c.Fail("Moving between places is not supported. Use pull or push to copy, then rm to delete.");
            return;
        }

        var serial = RequireSerial(c);
        if (serial is null)
        {
            return;
        }

        var moved = await _files.PasteAsync(serial, new[] { from.Path }, folder.Path, true, ConflictPolicy.Skip, c.Cancel).ConfigureAwait(false);
        if (moved.Succeeded)
        {
            c.Print(moved.Value, TerminalLineKind.Success);
        }
        else
        {
            Report(c, moved.Error);
        }
    }

    private async Task Pull(Context c)
    {
        var serial = RequireSerial(c);
        var args = c.PositionalArgs;
        if (serial is null)
        {
            return;
        }

        if (args.Count is < 1 or > 2)
        {
            c.Fail("Usage: pull <phone path> [PC folder]");
            return;
        }

        if (!TryResolve(c, args[0], out var source) || source.Area != VirtualArea.Android)
        {
            if (source?.Area is VirtualArea.System or VirtualArea.Root)
            {
                c.Fail("pull copies from the phone. That path is not on the phone.");
            }

            return;
        }

        string? destination;
        if (args.Count == 2)
        {
            destination = ResolveLocalFolder(args[1]);
        }
        else
        {
            destination = _workspace.ResolveLocal("/downloads");
        }

        if (destination is null)
        {
            c.Fail("The destination must be a full Windows path (like C:\\Backup) or a folder in /.system.");
            return;
        }

        c.Print($"Copying {source.Display} to {_workspace.ToVirtualPath(destination)} ...", TerminalLineKind.Info);
        var snapshot = await _transfers.RunAsync(
            new TransferRequest(serial, _instanceName(), TransferDirection.Download, new[] { source.Path }, destination, ConflictPolicy.KeepBoth), c.Cancel).ConfigureAwait(false);
        ReportTransfer(c, snapshot);
    }

    private async Task Push(Context c)
    {
        var serial = RequireSerial(c);
        var args = c.PositionalArgs;
        if (serial is null)
        {
            return;
        }

        if (args.Count is < 1 or > 2)
        {
            c.Fail("Usage: push <PC path> [phone folder]");
            return;
        }

        var local = ResolveLocalPath(args[0]);
        if (local is null || !(File.Exists(local) || Directory.Exists(local)))
        {
            c.Fail("That PC file or folder does not exist. Use a full Windows path (like C:\\Temp\\file.txt) or a path in /.system.");
            return;
        }

        VirtualLocation target = Cwd.Area == VirtualArea.Android ? Cwd : new VirtualLocation(VirtualArea.Android, "/sdcard/Download");
        if (args.Count == 2 && !TryResolve(c, args[1], out target))
        {
            return;
        }

        if (target.Area != VirtualArea.Android)
        {
            c.Fail("push copies to the phone. Choose a folder on the phone (for example /sdcard/Download).");
            return;
        }

        c.Print($"Copying {local} to {target.Display} ...", TerminalLineKind.Info);
        var snapshot = await _transfers.RunAsync(
            new TransferRequest(serial, _instanceName(), TransferDirection.Upload, new[] { local }, target.Path, ConflictPolicy.KeepBoth), c.Cancel).ConfigureAwait(false);
        ReportTransfer(c, snapshot);
    }

    private string? ResolveLocalFolder(string text)
    {
        var path = ResolveLocalPath(text);
        return path;
    }

    private string? ResolveLocalPath(string text)
    {
        if (text.StartsWith("/.system", StringComparison.Ordinal))
        {
            var location = VirtualPath.Resolve(VirtualLocation.Root, text);
            return location is { Area: VirtualArea.System } ? _workspace.ResolveLocal(location.Path) : null;
        }

        return Path.IsPathFullyQualified(text) && !text.StartsWith('/') ? text : null;
    }

    private static void ReportTransfer(Context c, TransferSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case TransferState.Completed:
                c.Print($"Done: {snapshot.FilesDone} file(s), {FormatBytes(snapshot.BytesDone)}" + (snapshot.FilesSkipped > 0 ? $", {snapshot.FilesSkipped} skipped" : string.Empty) + ".", TerminalLineKind.Success);
                break;
            case TransferState.Cancelled:
                c.Print("Transfer cancelled.", TerminalLineKind.Warning);
                break;
            default:
                c.Print(snapshot.Message ?? "The transfer did not finish.", snapshot.FilesDone > 0 ? TerminalLineKind.Warning : TerminalLineKind.Error);
                break;
        }

        foreach (var failure in snapshot.Failures.Take(10))
        {
            c.Fail($"  {failure.Path}: {failure.Message}");
        }
    }

    private async Task Show(Context c)
    {
        var topic = c.Args.FirstOrDefault()?.ToLowerInvariant();
        if (topic is null || !ShowTopics.Contains(topic))
        {
            c.Fail("Usage: show <" + string.Join('|', ShowTopics) + ">");
            return;
        }

        if (topic == "logs")
        {
            var logs = _workspace.ListLogs();
            if (logs.Count == 0)
            {
                c.Print("There are no session logs yet.", TerminalLineKind.Hint);
                return;
            }

            c.Print("Session logs (newest first):", TerminalLineKind.Info);
            foreach (var log in logs.Take(15))
            {
                c.Print($"  {log.VirtualPath}   {FormatBytes(log.Size)}");
            }

            if (logs.Count > 15)
            {
                c.Print($"  ... and {logs.Count - 15} older log(s) in /.system/logs", TerminalLineKind.Hint);
            }

            if (_workspace.CurrentLogPath is { } current)
            {
                var virtualPath = _workspace.ToVirtualPath(current);
                _host.OpenInNotepad(virtualPath);
                c.Print("Opened the current session log in Notepad: " + virtualPath, TerminalLineKind.Success);
            }

            return;
        }

        if (topic == "transfers")
        {
            var all = _transfers.Transfers;
            if (all.Count == 0)
            {
                c.Print("No transfers yet.", TerminalLineKind.Hint);
            }

            foreach (var t in all.Take(20))
            {
                c.Print($"  {t.State,-20} {t.Title}  {FormatBytes(t.BytesDone)}" + (t.BytesTotal is { } total ? " of " + FormatBytes(total) : string.Empty));
            }

            return;
        }

        var serial = RequireSerial(c);
        if (serial is null)
        {
            return;
        }

        var read = await _status.ReadAsync(serial, c.Cancel).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            Report(c, read.Error);
            return;
        }

        var s = read.Value;
        if (topic is "battery" or "device")
        {
            if (s.Battery is { } b)
            {
                c.Print($"Battery   {(b.Level is null ? "unknown" : b.Level + "%")}  {b.Status}  power: {b.PowerSource}" + (b.TemperatureCelsius is { } t ? $"  {t:0.#} °C" : string.Empty));
            }
            else
            {
                c.Print("Battery   not reported by this phone", TerminalLineKind.Hint);
            }
        }

        if (topic is "wifi" or "device")
        {
            if (s.Wifi is { } w)
            {
                c.Print("Wi-Fi     " + (w.Enabled == false ? "off" : w.Connected ? "connected" : "on, not connected")
                    + (w.Ssid is null ? string.Empty : $"  \"{w.Ssid}\"")
                    + (w.IpAddress is null ? string.Empty : "  " + w.IpAddress)
                    + (w.SignalDbm is null ? string.Empty : $"  {w.SignalDbm} dBm")
                    + (w.LinkSpeedMbps is null ? string.Empty : $"  {w.LinkSpeedMbps} Mbps"));
            }
            else
            {
                c.Print("Wi-Fi     not reported by this phone", TerminalLineKind.Hint);
            }
        }

        if (topic is "storage" or "device")
        {
            c.Print(s.Storage is { } st
                ? $"Storage   {FormatBytes(st.UsedBytes)} used of {FormatBytes(st.TotalBytes)} ({st.UsedFraction:P0})  free {FormatBytes(st.FreeBytes)}"
                : "Storage   not reported by this phone");
        }

        if (topic is "memory" or "device")
        {
            c.Print(s.Memory is { } m
                ? $"Memory    {FormatBytes(m.TotalBytes - m.AvailableBytes)} in use of {FormatBytes(m.TotalBytes)} ({m.UsedFraction:P0})"
                : "Memory    not reported by this phone");
        }

        if (topic == "device")
        {
            var device = _devices.Devices.FirstOrDefault(d => d.Serial == serial);
            if (device is not null)
            {
                c.Print($"Phone     {device.DisplayName}  {device.ConnectionType}" + (device.Hardware?.AndroidVersion is { } v ? $"  Android {v}" : string.Empty));
            }

            if (s.Uptime is { } up)
            {
                c.Print($"Uptime    {(int)up.TotalHours} h {up.Minutes} min");
            }
        }
    }

    private Task Devices(Context c)
    {
        var serial = _serial();
        if (_devices.Devices.Count == 0)
        {
            c.Print("No devices known. " + _devices.StatusMessage, TerminalLineKind.Hint);
        }

        foreach (var d in _devices.Devices)
        {
            c.Print($"{(d.Serial == serial ? "*" : " ")} {d.DisplayName,-24} {d.ConnectionType,-9} {d.State}", d.Serial == serial ? TerminalLineKind.Success : TerminalLineKind.Output);
        }

        c.Print("* = the phone of this instance", TerminalLineKind.Hint);
        return Task.CompletedTask;
    }

    private async Task Screenshot(Context c)
    {
        var serial = RequireSerial(c);
        if (serial is null)
        {
            return;
        }

        var folder = _workspace.ResolveLocal("/screenshots");
        if (folder is null)
        {
            c.Fail("The .system space is not available.");
            return;
        }

        Directory.CreateDirectory(folder);
        var remote = "/data/local/tmp/ma_screenshot.png";
        var capture = await _adb.ShellAsync(serial, $"screencap -p {RemotePath.ShellQuote(remote)}", Quick, c.Cancel).ConfigureAwait(false);
        if (!capture.Succeeded || capture.Value.ExitCode != 0)
        {
            c.Fail(capture.Succeeded ? "The phone could not take a screenshot: " + capture.Value.Combined : capture.ErrorMessage);
            return;
        }

        var name = "screenshot-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".png";
        var local = Path.Combine(folder, name);
        var pulled = await _adb.PullAsync(serial, remote, local, c.Cancel).ConfigureAwait(false);
        await _adb.ShellAsync(serial, $"rm -f -- {RemotePath.ShellQuote(remote)}", Quick, CancellationToken.None).ConfigureAwait(false);
        if (!pulled.Succeeded || !File.Exists(local))
        {
            c.Fail(pulled.Succeeded ? "Could not copy the screenshot: " + pulled.Value.Combined : pulled.ErrorMessage);
            return;
        }

        c.Print("Saved " + _workspace.ToVirtualPath(local) + $"  ({FormatBytes(new FileInfo(local).Length)})", TerminalLineKind.Success);
    }

    // ----- adb -----

    private async Task Adb(Context c)
    {
        var serial = RequireSerial(c);
        if (serial is null)
        {
            return;
        }

        var args = c.Args;
        if (args.Count == 0)
        {
            c.Fail("Usage: adb <command> [arguments]   (for example: adb get-state)");
            return;
        }

        // adb options before the command would choose another phone or server; both are refused.
        if (BlockedAdb.TryGetValue(args[0], out var reason))
        {
            c.Fail($"adb {args[0]} is not available here. {reason}");
            return;
        }

        if (args[0].StartsWith('-'))
        {
            c.Fail($"adb option {args[0]} is not available here. The phone and server are managed by the app.");
            return;
        }

        if (args[0] == "shell")
        {
            await RunShell(c, serial, CommandLine.RestAfter(c.Line, 2)).ConfigureAwait(false);
            return;
        }

        await Stream(c, serial, args).ConfigureAwait(false);
    }

    private async Task Shell(Context c)
    {
        var serial = RequireSerial(c);
        if (serial is null)
        {
            return;
        }

        await RunShell(c, serial, CommandLine.RestAfter(c.Line, 1)).ConfigureAwait(false);
    }

    private async Task RunShell(Context c, string serial, string command)
    {
        if (command.Length == 0)
        {
            c.Fail("An interactive shell is not supported here. Give a command: shell ls -la /sdcard");
            return;
        }

        // The text goes to the phone's shell as one argument; nothing is interpreted on this PC.
        await Stream(c, serial, new[] { "shell", command }).ConfigureAwait(false);
    }

    private Task Logcat(Context c)
    {
        var serial = RequireSerial(c);
        if (serial is null)
        {
            return Task.CompletedTask;
        }

        var options = c.Args.Count == 0 ? new[] { "-v", "time" } : c.Args.ToArray();
        return Stream(c, serial, new[] { "logcat" }.Concat(options).ToArray());
    }

    private async Task Stream(Context c, string serial, IReadOnlyList<string> arguments)
    {
        var result = await _adb.StreamAsync(serial, arguments, (text, isError) => c.Out.Write(isError ? TerminalLineKind.Error : TerminalLineKind.Output, text), c.Cancel).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Error!.Kind == ErrorKind.Cancelled)
            {
                c.Print("^C stopped.", TerminalLineKind.Info);
            }
            else
            {
                Report(c, result.Error);
            }

            return;
        }

        if (result.Value != 0)
        {
            c.Print($"exit code {result.Value}", TerminalLineKind.Warning);
        }
    }

    // ----- completion -----

    /// <summary>Suggestions for the word being typed at the end of <paramref name="line"/>.</summary>
    public async Task<Completion> CompleteAsync(string line, CancellationToken cancellationToken)
    {
        var tokens = CommandLine.Tokenize(line, null);
        var words = tokens.Where(t => t.Kind != TokenKind.Space).ToList();
        var endsWithSpace = line.Length > 0 && char.IsWhiteSpace(line[^1]);
        var current = endsWithSpace || words.Count == 0 ? null : words[^1];
        var start = current?.Start ?? line.Length;
        var prefix = current?.Value ?? string.Empty;
        var index = endsWithSpace ? words.Count : words.Count - 1;

        if (index <= 0)
        {
            return new Completion(start, _commands.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList());
        }

        var command = words[0].Value;
        if (command == "show")
        {
            return new Completion(start, ShowTopics.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        if (command == "adb" && index == 1)
        {
            return new Completion(start, AdbHints.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        if (command is not ("cd" or "ls" or "cat" or "open" or "mkdir" or "rm" or "mv" or "pull" or "push"))
        {
            return new Completion(start, Array.Empty<string>());
        }

        var slash = prefix.LastIndexOf('/');
        var directoryPart = slash >= 0 ? prefix[..(slash + 1)] : string.Empty;
        var namePrefix = slash >= 0 ? prefix[(slash + 1)..] : prefix;
        var folder = directoryPart.Length == 0 ? Cwd : VirtualPath.Resolve(Cwd, directoryPart);
        if (folder is null)
        {
            return new Completion(start, Array.Empty<string>());
        }

        var listing = await _vfs.ListAsync(_serial(), folder, cancellationToken).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return new Completion(start, Array.Empty<string>());
        }

        var options = listing.Value
            .Where(e => e.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) && (!(command == "cd") || e.IsDirectory))
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e =>
            {
                var text = directoryPart + e.Name + (e.IsDirectory ? "/" : string.Empty);
                return text.Any(char.IsWhiteSpace) ? "\"" + text + "\"" : text;
            })
            .Take(60)
            .ToList();
        return new Completion(start, options);
    }
}
