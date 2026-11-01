using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Android;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Workspace;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

/// <summary>
/// The .system folder of the instance: the instance's own space on this PC (logs, downloads, screenshots, notes).
/// It uses the same virtual file system as the terminal, so both follow the same safety rules.
/// </summary>
public sealed partial class SystemFilesViewModel : ObservableObject
{
    private readonly VirtualFileSystem _vfs;
    private readonly IInstanceWorkspace _workspace;
    private readonly ActiveInstance _active;
    private readonly DesktopCommands _commands;
    private readonly IFolderLauncher _folders;
    private readonly INotificationService _notifications;
    private readonly ILogger<SystemFilesViewModel> _logger;
    private List<FileEntryViewModel> _selection = new();

    public SystemFilesViewModel(
        VirtualFileSystem vfs,
        IInstanceWorkspace workspace,
        ActiveInstance active,
        DesktopCommands commands,
        IFolderLauncher folders,
        INotificationService notifications,
        ILogger<SystemFilesViewModel> logger)
    {
        _vfs = vfs;
        _workspace = workspace;
        _active = active;
        _commands = commands;
        _folders = folders;
        _notifications = notifications;
        _logger = logger;
        _current = VirtualLocation.SystemRoot;
    }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    /// <summary>Ask the user for text (view provides the dialog).</summary>
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }

    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>The user went above .system: show the root with both folders.</summary>
    public event EventHandler? HomeRequested;

    [ObservableProperty]
    private VirtualLocation _current;

    [ObservableProperty]
    private string _pathText = "/.system";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isLoading;

    public string RootText => _workspace.RootDirectory ?? "(not open)";

    public async Task NavigateAsync(VirtualLocation location)
    {
        IsLoading = true;
        HasError = false;
        try
        {
            var result = await _vfs.ListAsync(_active.Current?.DeviceSerial, location, CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = result.ErrorMessage;
                HasError = true;
                return;
            }

            Current = location;
            PathText = location.Display;
            Entries.Clear();
            foreach (var entry in result.Value.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Entries.Add(new FileEntryViewModel(entry));
            }

            StatusText = $"{Entries.Count} item(s)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing .system failed.");
            ErrorText = "Could not read this folder: " + ex.Message;
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task InitializeAsync() => NavigateAsync(Current);

    public Task NavigateToPathAsync(string virtualPath)
    {
        var location = VirtualPath.Resolve(VirtualLocation.Root, virtualPath);
        if (location is null || location.Area != VirtualArea.System)
        {
            return NavigateAsync(VirtualLocation.SystemRoot);
        }

        return NavigateAsync(location);
    }

    public void SetSelection(IEnumerable<FileEntryViewModel> selected) => _selection = selected.ToList();

    public async Task OpenAsync(FileEntryViewModel entry)
    {
        var location = VirtualPath.Resolve(VirtualLocation.Root, entry.FullPath);
        if (location is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await NavigateAsync(location);
        }
        else
        {
            _commands.OpenInNotepad(location.Display);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => NavigateAsync(Current);

    [RelayCommand]
    private async Task GoUpAsync()
    {
        var parent = Current.Parent;
        if (parent is null || parent.Area == VirtualArea.Root)
        {
            HomeRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await NavigateAsync(parent);
    }

    [RelayCommand]
    private void GoHome() => HomeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = PromptAsync is null ? null : await PromptAsync("New folder", "Name", "New folder");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var result = await _vfs.CreateFolderAsync(_active.Current?.DeviceSerial, Current, name.Trim(), CancellationToken.None);
        await AfterAsync(result.Succeeded, result.ErrorMessage, "Created folder " + Current.Child(name.Trim()).Display);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (_selection.Count != 1)
        {
            return;
        }

        var item = _selection[0];
        var name = PromptAsync is null ? null : await PromptAsync("Rename", "New name", item.Name);
        var target = VirtualPath.Resolve(VirtualLocation.Root, item.FullPath);
        if (string.IsNullOrWhiteSpace(name) || target is null)
        {
            return;
        }

        var result = await _vfs.RenameAsync(_active.Current?.DeviceSerial, target, name.Trim(), CancellationToken.None);
        await AfterAsync(result.Succeeded, result.ErrorMessage, $"Renamed {target.Display} to {name.Trim()}");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var targets = _selection.Select(s => VirtualPath.Resolve(VirtualLocation.Root, s.FullPath)).OfType<VirtualLocation>().ToList();
        var text = targets.Count == 1 ? $"Delete \"{targets[0].Name}\"?" : $"Delete {targets.Count} items?";
        if (ConfirmAsync is null || !await ConfirmAsync(text, "This permanently deletes the items from this PC. It cannot be undone.", "Delete"))
        {
            return;
        }

        var result = await _vfs.DeleteAsync(_active.Current?.DeviceSerial, targets, CancellationToken.None);
        await AfterAsync(result.Succeeded, result.ErrorMessage, "Deleted " + string.Join(", ", targets.Select(t => t.Display)));
    }

    [RelayCommand]
    private void OpenInNotepad()
    {
        foreach (var item in _selection.Where(s => !s.IsDirectory))
        {
            _commands.OpenInNotepad(item.FullPath);
        }
    }

    [RelayCommand]
    private void OpenTerminalHere() => _commands.OpenModule(ModuleIds.Terminal);

    [RelayCommand]
    private async Task ShowInWindowsAsync()
    {
        var location = _selection.Count == 1 ? VirtualPath.Resolve(VirtualLocation.Root, _selection[0].FullPath) : Current;
        if (location is null)
        {
            return;
        }

        var local = _workspace.ResolveLocal(location.Path);
        if (local is null)
        {
            return;
        }

        if (File.Exists(local))
        {
            local = Path.GetDirectoryName(local) ?? local;
        }

        if (!await _folders.OpenFolderAsync(local))
        {
            _notifications.Post(NotificationSeverity.Warning, "Could not open the folder", local);
        }
    }

    /// <summary>Copies files and folders dropped from Windows into the current folder.</summary>
    public async Task ImportAsync(IReadOnlyList<string> localPaths)
    {
        var destination = _workspace.ResolveLocal(Current.Path);
        if (destination is null)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in localPaths)
                {
                    if (Directory.Exists(source))
                    {
                        CopyDirectory(source, Path.Combine(destination, UniqueName(destination, Path.GetFileName(source))));
                    }
                    else if (File.Exists(source))
                    {
                        File.Copy(source, Path.Combine(destination, UniqueName(destination, Path.GetFileName(source))));
                    }
                }
            });
            await AfterAsync(true, string.Empty, $"Copied {localPaths.Count} item(s) into {Current.Display}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying into .system failed.");
            await AfterAsync(false, "Could not copy: " + ex.Message, string.Empty);
        }
    }

    private static string UniqueName(string folder, string name) =>
        RemotePath.UniqueName(name, n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n)));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private async Task AfterAsync(bool ok, string error, string logMessage)
    {
        if (ok)
        {
            HasError = false;
            if (logMessage.Length > 0)
            {
                _workspace.Log("explorer", logMessage);
            }
        }
        else
        {
            ErrorText = error;
            HasError = true;
        }

        await NavigateAsync(Current);
    }
}
