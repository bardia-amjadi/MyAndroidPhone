using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Android;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Transfers;
using MyAndroidPhone.Core.Workspace;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

/// <summary>One line on the terminal screen. A command line carries its prompt and colored tokens.</summary>
public sealed class TerminalLineItem
{
    public TerminalLineItem(TerminalLineKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public TerminalLineKind Kind { get; }

    public string Text { get; }

    public string Prefix { get; init; } = string.Empty;

    public IReadOnlyList<CommandToken> Tokens { get; init; } = Array.Empty<CommandToken>();

    /// <summary>Plain text of the line, as copied to the clipboard.</summary>
    public string PlainText => Kind == TerminalLineKind.Command ? Prefix + " " + Text : Text;
}

/// <summary>
/// The Terminal window's state. Output arrives from any thread (adb streams, transfers), is queued and appended to the
/// screen in batches on the UI thread so that a busy logcat cannot freeze the window.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, ITerminalOutput, ITerminalHost, IDisposable
{
    private const int MaxLines = 5000;
    private const int BatchSize = 400;

    private readonly TerminalEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly DesktopCommands _commands;
    private readonly IInstanceWorkspace _workspace;
    private readonly ConcurrentQueue<TerminalLineItem> _pending = new();
    private int _flushScheduled;
    private CancellationTokenSource? _running;
    private int _historyIndex = -1;
    private bool _disposed;

    public TerminalViewModel(
        VirtualFileSystem vfs,
        IAdbClient adb,
        IFileExplorerService files,
        ITransferService transfers,
        IDeviceStatusService status,
        IDeviceService devices,
        IInstanceWorkspace workspace,
        ActiveInstance active,
        DesktopCommands commands,
        IUiDispatcher ui,
        ILogger<TerminalEngine> logger)
    {
        _ui = ui;
        _commands = commands;
        _workspace = workspace;
        _engine = new TerminalEngine(
            vfs, adb, files, transfers, status, devices, workspace, this,
            () => active.Current?.DeviceSerial,
            () => active.Current?.Name ?? "phone",
            logger);
        _prompt = _engine.Prompt;
        _instanceName = active.Current?.Name ?? "phone";

        Write(TerminalLineKind.Info, $"{_instanceName} - terminal");
        Write(TerminalLineKind.Hint, "Type help for the list of commands. adb commands work too: adb get-state, shell ls /sdcard, logcat.");
        Write(TerminalLineKind.Hint, "Logs of this session: show logs   or   open /.system/logs/<DATETIME>.log");
        Write(TerminalLineKind.Output, string.Empty);
    }

    public ObservableCollection<TerminalLineItem> Lines { get; } = new();

    /// <summary>Raised after lines were appended, so the view can keep the end in view.</summary>
    public event EventHandler? LinesAppended;

    public string SessionLogName => _workspace.CurrentLogPath is { } path ? Path.GetFileName(path) : string.Empty;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _prompt;

    [ObservableProperty]
    private string _instanceName;

    [ObservableProperty]
    private bool _isRunning;

    // ----- output (any thread) -----

    public void Write(TerminalLineKind kind, string text)
    {
        foreach (var part in (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            _pending.Enqueue(new TerminalLineItem(kind, part));
        }

        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ui.Post(Flush);
        }
    }

    private void Flush()
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _flushScheduled, 0);
        var count = 0;
        while (count < BatchSize && _pending.TryDequeue(out var line))
        {
            Lines.Add(line);
            count++;
        }

        if (Lines.Count > MaxLines + 200)
        {
            for (var i = Lines.Count - MaxLines; i > 0; i--)
            {
                Lines.RemoveAt(0);
            }
        }

        if (count > 0)
        {
            LinesAppended?.Invoke(this, EventArgs.Empty);
        }

        if (!_pending.IsEmpty)
        {
            ScheduleFlush();
        }
    }

    // ----- host callbacks (the engine may call these from any thread) -----

    public void OpenInNotepad(string virtualPath) => _commands.OpenInNotepad(virtualPath);

    public void OpenInExplorer(string virtualFolder) => _commands.OpenInExplorer(virtualFolder);

    public void ClearScreen()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        _ui.Post(() => Lines.Clear());
    }

    // ----- commands -----

    public bool IsKnownCommand(string name) => _engine.IsKnownCommand(name);

    [RelayCommand]
    private async Task RunAsync()
    {
        var line = Input.Trim();
        if (line.Length == 0 || IsRunning)
        {
            return;
        }

        Input = string.Empty;
        _historyIndex = -1;
        _pending.Enqueue(new TerminalLineItem(TerminalLineKind.Command, line)
        {
            Prefix = _engine.Prompt,
            Tokens = CommandLine.Tokenize(line, _engine.IsKnownCommand)
        });
        ScheduleFlush();

        using var cts = new CancellationTokenSource();
        _running = cts;
        IsRunning = true;
        try
        {
            await _engine.ExecuteAsync(line, this, cts.Token).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            _running = null;
            Prompt = _engine.Prompt;
            ScheduleFlush();
        }
    }

    /// <summary>Ctrl+C: stops the running command (logcat, a transfer wait, a long listing).</summary>
    [RelayCommand]
    private void Stop() => _running?.Cancel();

    [RelayCommand]
    private void Clear() => ClearScreen();

    [RelayCommand]
    private void ShowLogs() => RunText("show logs");

    [RelayCommand]
    private void ShowHelp() => RunText("help");

    /// <summary>Puts a command into the input box and runs it, like typing it.</summary>
    public void RunText(string line)
    {
        if (IsRunning)
        {
            return;
        }

        Input = line;
        _ = RunAsync();
    }

    public string? HistoryStep(int direction)
    {
        var history = _engine.History;
        if (history.Count == 0)
        {
            return null;
        }

        if (direction < 0)
        {
            _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
        }
        else
        {
            if (_historyIndex < 0)
            {
                return null;
            }

            _historyIndex++;
            if (_historyIndex >= history.Count)
            {
                _historyIndex = -1;
                return string.Empty;
            }
        }

        return history[_historyIndex];
    }

    public Task<Completion> CompleteAsync(string line) => _engine.CompleteAsync(line, CancellationToken.None);

    public void Dispose()
    {
        _disposed = true;
        _running?.Cancel();
    }
}
