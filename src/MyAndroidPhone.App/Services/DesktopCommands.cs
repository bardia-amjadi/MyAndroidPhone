namespace MyAndroidPhone.Desktop.Services;

/// <summary>
/// How modules ask the desktop to do things for each other: the Terminal opens a file in Notepad or a folder in File
/// Explorer, the Dock opens the Device Center menu. Requests are queued, so they are not lost when the target window
/// has to be created first.
/// </summary>
public sealed class DesktopCommands(IUiDispatcher ui)
{
    private readonly Queue<string> _notepadFiles = new();
    private readonly Queue<string> _explorerFolders = new();
    private readonly object _gate = new();

    /// <summary>Open (or bring to front) the window of a feature.</summary>
    public event EventHandler<string>? ModuleRequested;

    /// <summary>Notepad has files waiting in <see cref="TakeNotepadFile"/>.</summary>
    public event EventHandler? NotepadFilesQueued;

    /// <summary>File Explorer has folders waiting in <see cref="TakeExplorerFolder"/>.</summary>
    public event EventHandler? ExplorerFoldersQueued;

    public event EventHandler? StartMenuToggleRequested;

    public void OpenModule(string featureId) => ui.Post(() => ModuleRequested?.Invoke(this, featureId));

    public void ToggleStartMenu() => ui.Post(() => StartMenuToggleRequested?.Invoke(this, EventArgs.Empty));

    /// <summary>Opens a file such as /.system/logs/2026-01-01_10-00-00.log in Notepad.</summary>
    public void OpenInNotepad(string virtualPath)
    {
        lock (_gate)
        {
            _notepadFiles.Enqueue(virtualPath);
        }

        ui.Post(() =>
        {
            NotepadFilesQueued?.Invoke(this, EventArgs.Empty);
            ModuleRequested?.Invoke(this, ModuleIds.Notepad);
        });
    }

    /// <summary>Opens a folder such as /.system/logs or /.android/sdcard/Download in File Explorer.</summary>
    public void OpenInExplorer(string virtualFolder)
    {
        lock (_gate)
        {
            _explorerFolders.Enqueue(virtualFolder);
        }

        ui.Post(() =>
        {
            ExplorerFoldersQueued?.Invoke(this, EventArgs.Empty);
            ModuleRequested?.Invoke(this, ModuleIds.FileExplorer);
        });
    }

    public string? TakeNotepadFile()
    {
        lock (_gate)
        {
            return _notepadFiles.Count > 0 ? _notepadFiles.Dequeue() : null;
        }
    }

    public string? TakeExplorerFolder()
    {
        lock (_gate)
        {
            return _explorerFolders.Count > 0 ? _explorerFolders.Dequeue() : null;
        }
    }
}
