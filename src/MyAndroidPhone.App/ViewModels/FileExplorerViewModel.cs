using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Android;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Transfers;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed class FileEntryViewModel
{
    public FileEntryViewModel(RemoteEntry entry, string? location = null)
    {
        Entry = entry;
        Location = location;
    }

    public RemoteEntry Entry { get; }

    /// <summary>Containing folder, shown for search results.</summary>
    public string? Location { get; }

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public string Glyph => Entry.Kind switch
    {
        RemoteEntryKind.Directory => "",
        RemoteEntryKind.Link => "",
        _ => ""
    };

    public string SizeText => Entry.Kind == RemoteEntryKind.File ? Formatting.Bytes(Entry.Size) : string.Empty;

    public string ModifiedText => Entry.Modified?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public string SecondaryText => Location is null ? Entry.Permissions ?? string.Empty : Location;
}

public sealed record BreadcrumbItem(string Name, string Path);

public sealed record LocationItem(StorageLocation Location)
{
    public string Name => Location.Name;

    public string Path => Location.Path;

    public string Glyph => Location.IconGlyph;
}

/// <summary>
/// File Explorer for the selected device: browse, sort, filter, search, create/rename/delete, copy/cut/paste on the
/// device, and upload/download through the transfer service. Dialogs are provided by the view (they need a XamlRoot).
/// </summary>
public sealed partial class FileExplorerViewModel : ObservableObject, IDisposable
{
    private const string DefaultPath = "/sdcard";

    private readonly IDeviceService _devices;
    private readonly ActiveInstance _active;
    private readonly IFileExplorerService _files;
    private readonly ITransferService _transfers;
    private readonly IFilePickerService _pickers;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService _notifications;
    private readonly ILogger<FileExplorerViewModel> _logger;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly HashSet<Guid> _refreshedAfter = new();
    private List<FileEntryViewModel> _all = new();
    private List<FileEntryViewModel> _selection = new();
    private CancellationTokenSource? _navigation;
    private string? _serial;
    private string _deviceName = string.Empty;
    private List<string> _clipboard = new();
    private bool _clipboardMove;
    private string? _clipboardSerial;
    private bool _disposed;

    public FileExplorerViewModel(
        IDeviceService devices,
        IFileExplorerService files,
        ITransferService transfers,
        IFilePickerService pickers,
        IUiDispatcher ui,
        INotificationService notifications,
        ActiveInstance active,
        ILogger<FileExplorerViewModel> logger)
    {
        _devices = devices;
        _active = active;
        _files = files;
        _transfers = transfers;
        _pickers = pickers;
        _ui = ui;
        _notifications = notifications;
        _logger = logger;
        _devices.Changed += OnDevicesChanged;
        _transfers.Changed += OnTransfersChanged;
        CleanOldDragFolders();
    }

    // Dialogs, supplied by the view.
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }

    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public Func<string, IReadOnlyList<string>, Task<ConflictPolicy?>>? ChooseConflictAsync { get; set; }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    public ObservableCollection<LocationItem> Locations { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice), nameof(NoDevice), nameof(CanModify))]
    private string _deviceTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _errorDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private string _currentPath = DefaultPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify), nameof(AreaBlockedText))]
    private StorageClassification? _classification;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocationColumn))]
    private bool _isSearchResult;

    [ObservableProperty]
    private string _sortKey = "Name";

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPaste))]
    private int _clipboardCount;

    public bool HasDevice => _serial is not null;

    public bool NoDevice => _serial is null;

    public bool HasError => ErrorText.Length > 0;

    public bool HasWarning => WarningText.Length > 0;

    public bool ShowLocationColumn => IsSearchResult;

    public bool CanModify => HasDevice && Classification is { WritesBlocked: false } && !IsSearchResult;

    public bool CanPaste => CanModify && ClipboardCount > 0 && _clipboardSerial == _serial;

    public string AreaBlockedText => Classification is { WritesBlocked: true } c ? c.Title + ": changes are not available here." : string.Empty;

    public string AreaText => Classification is null ? string.Empty : Classification.Title + ". " + Classification.Explanation;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => RemotePath.GetParent(CurrentPath) is not null;

    partial void OnClassificationChanged(StorageClassification? value) => OnPropertyChanged(nameof(AreaText));

    partial void OnFilterTextChanged(string value) => ApplyView();

    partial void OnSortKeyChanged(string value) => ApplyView();

    partial void OnSortDescendingChanged(bool value) => ApplyView();

    public async Task InitializeAsync()
    {
        await SyncDeviceAsync(forceReload: true).ConfigureAwait(true);
    }

    public void SetSelection(IEnumerable<FileEntryViewModel> selected)
    {
        _selection = selected.ToList();
        SelectionText = _selection.Count switch
        {
            0 => string.Empty,
            1 => DescribeOne(_selection[0].Entry),
            _ => $"{_selection.Count} items selected" + (_selection.All(s => s.Entry.Size is not null) ? ", " + Formatting.Bytes(_selection.Sum(s => s.Entry.Size ?? 0)) : string.Empty)
        };
        NotifyCommands();
    }

    private static string DescribeOne(RemoteEntry e) =>
        $"{e.Name} - {(e.IsDirectory ? "Folder" : Formatting.Bytes(e.Size))}"
        + (e.Modified is { } m ? $" - modified {m:g}" : string.Empty)
        + (e.Permissions is { } p ? $" - {p}" : string.Empty)
        + (e.Owner is { } o ? $" - owner {o}" : string.Empty)
        + (e.LinkTarget is { } t ? $" - link to {t}" : string.Empty);

    // ----- navigation -----

    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        if (_serial is null)
        {
            return;
        }

        var previous = CurrentPath;
        var serial = _serial;
        _navigation?.Cancel();
        var cts = _navigation = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = string.Empty;
        ErrorDetail = string.Empty;
        try
        {
            var result = await _files.ListAsync(serial, path, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || serial != _serial)
            {
                return;
            }

            if (!result.Succeeded)
            {
                ErrorText = result.ErrorMessage;
                ErrorDetail = result.Error?.Detail ?? string.Empty;
                // Keep showing the folder we were in; the error explains why we did not move.
                return;
            }

            if (recordHistory && previous != result.Value.Path && Entries.Count + Breadcrumbs.Count > 0)
            {
                _back.Push(previous);
                _forward.Clear();
            }

            CurrentPath = result.Value.Path;
            Classification = result.Value.Classification;
            WarningText = result.Value.Warning ?? string.Empty;
            IsSearchResult = false;
            SearchText = string.Empty;
            _all = result.Value.Entries.Select(e => new FileEntryViewModel(e)).ToList();
            RebuildBreadcrumbs();
            ApplyView();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing a device folder failed.");
            ErrorText = "Could not read this folder: " + ex.Message;
        }
        finally
        {
            if (_navigation == cts)
            {
                IsLoading = false;
            }

            NotifyCommands();
        }
    }

    public Task OpenAsync(FileEntryViewModel entry) =>
        entry.Entry.CanOpen ? NavigateAsync(entry.FullPath) : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => NavigateAsync(CurrentPath, recordHistory: false);

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var target = _back.Pop();
        _forward.Push(CurrentPath);
        await NavigateAsync(target, recordHistory: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var target = _forward.Pop();
        _back.Push(CurrentPath);
        await NavigateAsync(target, recordHistory: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        var parent = RemotePath.GetParent(CurrentPath);
        if (parent is not null)
        {
            await NavigateAsync(parent).ConfigureAwait(true);
        }
    }

    // ----- search -----

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_serial is null || string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        var serial = _serial;
        IsLoading = true;
        ErrorText = string.Empty;
        try
        {
            var result = await _files.SearchAsync(serial, CurrentPath, SearchText, CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = result.ErrorMessage;
                return;
            }

            _all = result.Value.Entries.Select(e => new FileEntryViewModel(e, RemotePath.GetParent(e.FullPath))).ToList();
            IsSearchResult = true;
            WarningText = (result.Value.Truncated ? "Showing the first 500 matches. Narrow the search to see more. " : string.Empty)
                + (result.Value.UnreadableFolders > 0 ? $"{result.Value.UnreadableFolders} folder(s) could not be searched because Android denied access." : string.Empty);
            ApplyView();
            StatusText = $"{_all.Count} match(es) for \"{SearchText}\" in {CurrentPath}";
        }
        finally
        {
            IsLoading = false;
            NotifyCommands();
        }
    }

    [RelayCommand]
    private Task ClearSearchAsync() => NavigateAsync(CurrentPath, recordHistory: false);

    // ----- sort / filter -----

    [RelayCommand]
    private void SortBy(string key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }
    }

    private void ApplyView()
    {
        IEnumerable<FileEntryViewModel> items = _all;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            items = items.Where(i => i.Name.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        var ordered = items.OrderByDescending(i => i.IsDirectory);
        ordered = SortKey switch
        {
            "Size" => SortDescending ? ordered.ThenByDescending(i => i.Entry.Size ?? -1) : ordered.ThenBy(i => i.Entry.Size ?? -1),
            "Modified" => SortDescending ? ordered.ThenByDescending(i => i.Entry.Modified) : ordered.ThenBy(i => i.Entry.Modified),
            _ => SortDescending ? ordered.ThenByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase) : ordered.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        var list = ordered.ToList();
        Entries.Clear();
        foreach (var item in list)
        {
            Entries.Add(item);
        }

        if (!IsSearchResult)
        {
            StatusText = list.Count == 0
                ? (_all.Count == 0 ? "This folder is empty." : "No items match the filter.")
                : $"{list.Count} item(s)" + (list.Count != _all.Count ? $" of {_all.Count}" : string.Empty);
        }

        NotifyCommands();
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        foreach (var (name, path) in RemotePath.Breadcrumbs(CurrentPath))
        {
            Breadcrumbs.Add(new BreadcrumbItem(name, path));
        }
    }

    // ----- create / rename / delete -----

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task NewFolderAsync()
    {
        var name = PromptAsync is null ? null : await PromptAsync("New folder", "Folder name", "New folder").ConfigureAwait(true);
        if (_serial is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunOperationAsync("Create folder", () => _files.CreateFolderAsync(_serial!, CurrentPath, name.Trim(), CancellationToken.None)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var target = _selection.Single();
        var name = PromptAsync is null ? null : await PromptAsync("Rename", "New name", target.Name).ConfigureAwait(true);
        if (_serial is null || string.IsNullOrWhiteSpace(name) || name == target.Name)
        {
            return;
        }

        await RunOperationAsync("Rename", () => _files.RenameAsync(_serial!, target.FullPath, name.Trim(), CancellationToken.None)).ConfigureAwait(true);
    }

    private bool CanRename() => CanModify && _selection.Count == 1;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        if (_serial is null || _selection.Count == 0)
        {
            return;
        }

        var paths = _selection.Select(s => s.FullPath).ToList();
        var what = paths.Count == 1 ? $"\"{_selection[0].Name}\"" : $"{paths.Count} items";
        var confirmed = ConfirmAsync is not null
            && await ConfirmAsync("Delete " + what + "?", "Android has no recycle bin for files deleted this way. " + what + " will be removed from the device permanently, including everything inside folders.", "Delete").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync("Delete", () => _files.DeleteAsync(_serial!, paths, CancellationToken.None)).ConfigureAwait(true);
    }

    private bool CanActOnSelection() => CanModify && _selection.Count > 0;

    private async Task RunOperationAsync<T>(string title, Func<Task<MyAndroidPhone.Core.Results.OperationResult<T>>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action().ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _notifications.Post(NotificationSeverity.Error, title + " failed", result.ErrorMessage);
            }
            else if (result.Value is string message && message.Length > 0 && message.EndsWith('.'))
            {
                _notifications.Post(NotificationSeverity.Success, title, message);
            }
        }
        finally
        {
            IsBusy = false;
        }

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    // ----- clipboard -----

    [RelayCommand(CanExecute = nameof(CanActOnSelectionRead))]
    private void Copy() => SetClipboard(move: false);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Cut() => SetClipboard(move: true);

    private bool CanActOnSelectionRead() => HasDevice && _selection.Count > 0;

    private void SetClipboard(bool move)
    {
        _clipboard = _selection.Select(s => s.FullPath).ToList();
        _clipboardMove = move;
        _clipboardSerial = _serial;
        ClipboardCount = _clipboard.Count;
        _notifications.Post(NotificationSeverity.Information, move ? "Ready to move" : "Ready to copy", $"{_clipboard.Count} item(s). Open the destination folder and choose Paste.");
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (_serial is null || _clipboard.Count == 0 || _clipboardSerial != _serial)
        {
            return;
        }

        var names = _clipboard.Select(RemotePath.GetName).ToList();
        var existing = await _files.FindExistingAsync(_serial, CurrentPath, names, CancellationToken.None).ConfigureAwait(true);
        var policy = ConflictPolicy.KeepBoth;
        if (existing.Succeeded && existing.Value.Count > 0)
        {
            var choice = ChooseConflictAsync is null ? null : await ChooseConflictAsync(CurrentPath, existing.Value).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            policy = choice.Value;
        }

        var sources = _clipboard.ToList();
        var move = _clipboardMove;
        await RunOperationAsync(move ? "Move" : "Copy", () => _files.PasteAsync(_serial!, sources, CurrentPath, move, policy, CancellationToken.None)).ConfigureAwait(true);
        if (move)
        {
            _clipboard.Clear();
            ClipboardCount = 0;
        }
    }

    // ----- transfers -----

    [RelayCommand(CanExecute = nameof(CanActOnSelectionRead))]
    private async Task DownloadAsync()
    {
        if (_serial is null || _selection.Count == 0)
        {
            return;
        }

        var folder = await _pickers.PickFolderAsync().ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        var names = _selection.Select(s => LocalPathRules.SanitizeName(s.Name)).ToList();
        var clashes = names.Where(n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n))).ToList();
        var policy = ConflictPolicy.KeepBoth;
        if (clashes.Count > 0)
        {
            var choice = ChooseConflictAsync is null ? null : await ChooseConflictAsync(folder, clashes).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            policy = choice.Value;
        }

        _transfers.Enqueue(new TransferRequest(_serial, _deviceName, TransferDirection.Download, _selection.Select(s => s.FullPath).ToList(), folder, policy));
        _notifications.Post(NotificationSeverity.Information, "Download queued", "Progress is shown in Transfer Center.");
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task UploadFilesAsync()
    {
        var picked = await _pickers.PickFilesAsync().ConfigureAwait(true);
        if (picked.Count > 0)
        {
            await UploadAsync(picked).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task UploadFolderAsync()
    {
        var folder = await _pickers.PickFolderAsync().ConfigureAwait(true);
        if (folder is not null)
        {
            await UploadAsync(new[] { folder }).ConfigureAwait(true);
        }
    }

    /// <summary>Starts an upload of local files/folders into the current folder (also used for drag and drop).</summary>
    public async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_serial is null || localPaths.Count == 0)
        {
            return;
        }

        if (!CanModify)
        {
            _notifications.Post(NotificationSeverity.Warning, "Cannot copy here", Classification is { WritesBlocked: true } c ? c.Title + ": " + c.Explanation : "Open a folder on the device first.");
            return;
        }

        var names = localPaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).Where(n => n.Length > 0).ToList();
        var policy = ConflictPolicy.KeepBoth;
        var existing = await _files.FindExistingAsync(_serial, CurrentPath, names, CancellationToken.None).ConfigureAwait(true);
        if (existing.Succeeded && existing.Value.Count > 0)
        {
            var choice = ChooseConflictAsync is null ? null : await ChooseConflictAsync(CurrentPath, existing.Value).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            policy = choice.Value;
        }

        _transfers.Enqueue(new TransferRequest(_serial, _deviceName, TransferDirection.Upload, localPaths.ToList(), CurrentPath, policy));
        _notifications.Post(NotificationSeverity.Information, "Upload queued", $"{localPaths.Count} item(s) to {CurrentPath}. Progress is shown in Transfer Center.");
    }

    /// <summary>
    /// Used when the selection is dragged out to Windows Explorer: downloads to a temporary folder and returns the local
    /// paths of the top-level items. Failures are reported and the failed items are simply absent.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadForDragAsync(IReadOnlyList<FileEntryViewModel> items, CancellationToken cancellationToken)
    {
        if (_serial is null || items.Count == 0)
        {
            return Array.Empty<string>();
        }

        var folder = Path.Combine(Path.GetTempPath(), "MyAndroidPhone", "drag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var snapshot = await _transfers.RunAsync(
            new TransferRequest(_serial, _deviceName, TransferDirection.Download, items.Select(i => i.FullPath).ToList(), folder, ConflictPolicy.KeepBoth),
            cancellationToken).ConfigureAwait(false);
        if (snapshot.FilesFailed > 0)
        {
            _ui.Post(() => _notifications.Post(NotificationSeverity.Warning, "Some items could not be copied", snapshot.Failures.FirstOrDefault()?.Message ?? "See Transfer Center."));
        }

        return items.Select(i => Path.Combine(folder, LocalPathRules.SanitizeName(i.Name))).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    public IReadOnlyList<FileEntryViewModel> Selection => _selection;

    private static void CleanOldDragFolders()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "MyAndroidPhone", "drag");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1)))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporary files only; leftovers are harmless.
        }
    }

    // ----- device tracking -----

    private void OnDevicesChanged(object? sender, EventArgs e) => _ui.Post(() => _ = SyncDeviceAsync(forceReload: false));

    private void OnTransfersChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed || _serial is null)
        {
            return;
        }

        foreach (var t in _transfers.Transfers)
        {
            if (t.IsFinished && t.Request.Direction == TransferDirection.Upload && t.Request.DeviceSerial == _serial
                && t.Request.DestinationDirectory == CurrentPath && _refreshedAfter.Add(t.Id))
            {
                _ = NavigateAsync(CurrentPath, recordHistory: false);
            }
        }
    });

    private async Task SyncDeviceAsync(bool forceReload)
    {
        // The desktop's instance decides which phone this window shows, not Device Center's selection.
        var device = _active.Current is { } instance
            ? _devices.Devices.FirstOrDefault(d => d.Serial == instance.DeviceSerial)
            : _devices.SelectedDevice;
        var usable = device is { IsOnline: true } ? device : null;
        var newSerial = usable?.Serial;
        if (newSerial == _serial && !forceReload)
        {
            return;
        }

        _navigation?.Cancel();
        _serial = newSerial;
        _deviceName = usable?.DisplayName ?? string.Empty;
        DeviceTitle = usable is null ? string.Empty : usable.DisplayName;
        OnPropertyChanged(nameof(HasDevice));
        OnPropertyChanged(nameof(NoDevice));
        _back.Clear();
        _forward.Clear();
        Locations.Clear();
        _all = new List<FileEntryViewModel>();
        Entries.Clear();
        Breadcrumbs.Clear();
        ErrorText = string.Empty;
        WarningText = string.Empty;
        IsSearchResult = false;
        ClipboardCount = _clipboardSerial == newSerial ? _clipboard.Count : 0;

        if (usable is null)
        {
            StatusText = device is null
                ? "Connect a device in Device Center to browse its files."
                : $"{device.DisplayName} is not ready ({device.State}). Open Device Center for help.";
            NotifyCommands();
            return;
        }

        var serial = usable.Serial;
        var locations = await _files.GetLocationsAsync(serial, CancellationToken.None).ConfigureAwait(true);
        if (serial != _serial)
        {
            return;
        }

        foreach (var location in locations)
        {
            Locations.Add(new LocationItem(location));
        }

        var start = locations.Any(l => l.Path == DefaultPath) ? DefaultPath : "/";
        await NavigateAsync(start, recordHistory: false).ConfigureAwait(true);
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanModify));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        NewFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        UploadFilesCommand.NotifyCanExecuteChanged();
        UploadFolderCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _devices.Changed -= OnDevicesChanged;
        _transfers.Changed -= OnTransfersChanged;
        _navigation?.Cancel();
    }
}
