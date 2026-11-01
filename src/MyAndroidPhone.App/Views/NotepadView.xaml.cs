using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Android;
using MyAndroidPhone.Core.Workspace;
using MyAndroidPhone.Desktop.Services;
using Windows.System;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>
/// Notepad: tabs for text files from the phone (.android, read-only), from the instance's own space (.system) and from
/// this PC. Phone files can be saved as a copy on the PC. Reading and saving go through the same rules as the terminal.
/// </summary>
public sealed partial class NotepadView : UserControl
{
    private const int MaxOpenBytes = 2 * 1024 * 1024;
    private const double MinFont = 9;
    private const double MaxFont = 40;

    private readonly VirtualFileSystem _vfs;
    private readonly IInstanceWorkspace _workspace;
    private readonly ActiveInstance _active;
    private readonly DesktopCommands _commands;
    private readonly IFilePickerService _pickers;
    private readonly INotificationService _notifications;
    private readonly ILogger<NotepadView> _logger;
    private double _fontSize = 13;
    private int _untitled;

    private sealed class Document
    {
        public required TabViewItem Tab { get; init; }

        public required TextBox Editor { get; init; }

        public string Title { get; set; } = "Untitled";

        /// <summary>/.system/... or /.android/... or null for a new or PC file.</summary>
        public string? VirtualPath { get; set; }

        /// <summary>Where Save writes to (a .system file or a PC file). Null when Save must ask.</summary>
        public string? LocalPath { get; set; }

        public bool IsReadOnly { get; set; }

        public bool IsDirty { get; set; }

        public bool Loading { get; set; }

        public string Origin { get; set; } = "New file";
    }

    private readonly List<Document> _documents = new();

    public NotepadView(
        VirtualFileSystem vfs,
        IInstanceWorkspace workspace,
        ActiveInstance active,
        DesktopCommands commands,
        IFilePickerService pickers,
        INotificationService notifications,
        ILogger<NotepadView> logger)
    {
        _vfs = vfs;
        _workspace = workspace;
        _active = active;
        _commands = commands;
        _pickers = pickers;
        _notifications = notifications;
        _logger = logger;
        InitializeComponent();

        AddShortcut(VirtualKey.S, () => OnSave(this, new RoutedEventArgs()));
        AddShortcut(VirtualKey.N, () => NewDocument());
        AddShortcut(VirtualKey.O, () => OnOpenPath(this, new RoutedEventArgs()));
        AddShortcut(VirtualKey.W, () => _ = CloseAsync(Current));

        Loaded += async (_, _) =>
        {
            _commands.NotepadFilesQueued += OnFilesQueued;
            await DrainQueueAsync();
            if (_documents.Count == 0)
            {
                NewDocument();
            }
        };
        Unloaded += (_, _) => _commands.NotepadFilesQueued -= OnFilesQueued;
    }

    private void AddShortcut(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private Document? Current => _documents.FirstOrDefault(d => ReferenceEquals(d.Tab, Tabs.SelectedItem));

    // ----- opening -----

    private async void OnFilesQueued(object? sender, EventArgs e) => await DrainQueueAsync();

    private async Task DrainQueueAsync()
    {
        while (_commands.TakeNotepadFile() is { } path)
        {
            await OpenVirtualAsync(path);
        }
    }

    private Document NewDocument(string? title = null)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = _fontSize,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            IsSpellCheckEnabled = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);

        var tab = new TabViewItem { Content = editor, IconSource = new SymbolIconSource { Symbol = Symbol.Document } };
        var doc = new Document { Tab = tab, Editor = editor, Title = title ?? $"Untitled {++_untitled}" };
        editor.TextChanged += (_, _) =>
        {
            if (!doc.Loading)
            {
                doc.IsDirty = true;
                UpdateHeader(doc);
            }
        };
        editor.SelectionChanged += (_, _) => UpdatePosition(doc);
        UpdateHeader(doc);
        _documents.Add(doc);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        return doc;
    }

    private void UpdateHeader(Document doc)
    {
        doc.Tab.Header = (doc.IsDirty ? "* " : string.Empty) + doc.Title + (doc.IsReadOnly ? " (read-only)" : string.Empty);
        if (ReferenceEquals(doc, Current))
        {
            ShowStatus(doc);
        }
    }

    private void ShowStatus(Document? doc)
    {
        PathText.Text = doc is null ? string.Empty : (doc.VirtualPath ?? doc.LocalPath ?? "Not saved yet") + "   -   " + doc.Origin;
        UpdatePosition(doc);
    }

    private void UpdatePosition(Document? doc)
    {
        if (doc is null || !ReferenceEquals(doc, Current))
        {
            return;
        }

        var text = doc.Editor.Text;
        var caret = Math.Min(doc.Editor.SelectionStart, text.Length);
        var line = 1;
        var lastBreak = -1;
        for (var i = 0; i < caret; i++)
        {
            if (text[i] == '\r')
            {
                line++;
                lastBreak = i;
            }
        }

        PositionText.Text = $"Ln {line}, Col {caret - lastBreak}   -   {text.Length:N0} characters   -   UTF-8";
    }

    public async Task OpenVirtualAsync(string input)
    {
        var location = VirtualPath.Resolve(VirtualLocation.Root, input);
        if (location is null || location.Area == VirtualArea.Root)
        {
            _notifications.Post(NotificationSeverity.Warning, "Cannot open", $"\"{input}\" is not a file path. Use /.system/... or /.android/...");
            return;
        }

        var display = location.Display;
        var existing = _documents.FirstOrDefault(d => string.Equals(d.VirtualPath, display, StringComparison.Ordinal));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing.Tab;
            await ReloadAsync(existing, askIfDirty: true);
            return;
        }

        var result = await _vfs.ReadTextAsync(_active.Current?.DeviceSerial, location, MaxOpenBytes, CancellationToken.None);
        if (!result.Succeeded)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not open the file", result.ErrorMessage);
            return;
        }

        // A lone untouched "Untitled" tab is replaced by the first real file.
        var blank = _documents.Count == 1 && _documents[0] is { VirtualPath: null, LocalPath: null, IsDirty: false } only && only.Editor.Text.Length == 0 ? _documents[0] : null;
        if (blank is not null)
        {
            Close(blank);
        }

        var doc = NewDocument(location.Name);
        doc.VirtualPath = display;
        doc.IsReadOnly = location.Area == VirtualArea.Android || display.StartsWith("/.system/logs", StringComparison.OrdinalIgnoreCase);
        doc.LocalPath = location.Area == VirtualArea.System && !doc.IsReadOnly ? _workspace.ResolveLocal(location.Path) : null;
        doc.Origin = location.Area == VirtualArea.Android ? "On the phone (read-only; use Save a copy to keep it)" : doc.IsReadOnly ? "Log file (read-only)" : "In .system on this PC";
        Fill(doc, result.Value);
        _workspace.Log("notepad", "Opened " + display);
    }

    private void Fill(Document doc, TextContent content)
    {
        doc.Loading = true;
        doc.Editor.Text = content.Text.Replace("\r\n", "\r").Replace('\n', '\r');
        doc.Editor.IsReadOnly = doc.IsReadOnly;
        doc.Loading = false;
        doc.IsDirty = false;
        if (content.Truncated)
        {
            doc.IsReadOnly = true;
            doc.Editor.IsReadOnly = true;
            doc.Origin += $" - only the first {content.Text.Length:N0} characters of {content.TotalBytes:N0} bytes are shown";
        }

        UpdateHeader(doc);
        ShowStatus(doc);
    }

    private async Task ReloadAsync(Document doc, bool askIfDirty)
    {
        if (doc.VirtualPath is null)
        {
            return;
        }

        if (doc.IsDirty && askIfDirty && !await ConfirmAsync("Discard your changes?", "Reloading replaces the text in this tab with the file's current content.", "Reload"))
        {
            return;
        }

        var location = VirtualPath.Resolve(VirtualLocation.Root, doc.VirtualPath);
        if (location is null)
        {
            return;
        }

        var result = await _vfs.ReadTextAsync(_active.Current?.DeviceSerial, location, MaxOpenBytes, CancellationToken.None);
        if (!result.Succeeded)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not read the file", result.ErrorMessage);
            return;
        }

        Fill(doc, result.Value);
        // A log being written grows at the end: show the newest lines.
        if (doc.IsReadOnly)
        {
            doc.Editor.SelectionStart = doc.Editor.Text.Length;
        }
    }

    // ----- toolbar -----

    private void OnNew(object sender, RoutedEventArgs e) => NewDocument();

    private async void OnOpenPath(object sender, RoutedEventArgs e)
    {
        var path = await PromptAsync("Open file", "Path (for example /.system/logs/2026-01-01_10-00-00.log or /.android/sdcard/Download/notes.txt)", "/.system/");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenVirtualAsync(path.Trim());
        }
    }

    private async void OnOpenFromPc(object sender, RoutedEventArgs e)
    {
        var files = await _pickers.PickFilesAsync();
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxOpenBytes)
                {
                    _notifications.Post(NotificationSeverity.Warning, "File too large for Notepad", $"{info.Name} is larger than {MaxOpenBytes / 1024 / 1024} MB.");
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(file);
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    _notifications.Post(NotificationSeverity.Warning, "Not a text file", $"{info.Name} looks like a binary file.");
                    continue;
                }

                var doc = NewDocument(info.Name);
                doc.LocalPath = file;
                doc.Origin = "On this PC";
                Fill(doc, new TextContent(new System.Text.UTF8Encoding(false).GetString(bytes), bytes.Length, false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open a PC file in Notepad.");
                _notifications.Post(NotificationSeverity.Error, "Could not open the file", ex.Message);
            }
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var doc = Current;
        if (doc is null)
        {
            return;
        }

        if (doc.IsReadOnly || (doc.LocalPath is null && doc.VirtualPath is not null))
        {
            await SaveCopyAsync(doc);
            return;
        }

        if (doc.LocalPath is null)
        {
            await SaveCopyAsync(doc);
            return;
        }

        await WriteAsync(doc, doc.LocalPath);
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (Current is { } doc)
        {
            await SaveCopyAsync(doc);
        }
    }

    private async Task SaveCopyAsync(Document doc)
    {
        var name = doc.Title.Contains('.') ? doc.Title : doc.Title + ".txt";
        var path = await _pickers.PickSaveFileAsync(name, "Text file", ".txt");
        if (path is null)
        {
            return;
        }

        if (await WriteAsync(doc, path, keepAsDocument: !doc.IsReadOnly && doc.VirtualPath is null))
        {
            _notifications.Post(NotificationSeverity.Success, "Saved", path);
        }
    }

    private async Task<bool> WriteAsync(Document doc, string path, bool keepAsDocument = true)
    {
        try
        {
            var text = doc.Editor.Text.Replace("\r", Environment.NewLine);
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
            if (keepAsDocument)
            {
                doc.LocalPath = path;
                doc.IsDirty = false;
                if (doc.VirtualPath is null)
                {
                    doc.Title = Path.GetFileName(path);
                    doc.Origin = "On this PC";
                }

                UpdateHeader(doc);
            }

            _workspace.Log("notepad", "Saved " + (doc.VirtualPath ?? path));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving from Notepad failed.");
            _notifications.Post(NotificationSeverity.Error, "Could not save", ex.Message);
            return false;
        }
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (Current is { } doc)
        {
            await ReloadAsync(doc, askIfDirty: true);
        }
    }

    private void OnWrapChanged(object sender, RoutedEventArgs e)
    {
        foreach (var doc in _documents)
        {
            doc.Editor.TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetFont(_fontSize + 1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => SetFont(_fontSize - 1);

    private void SetFont(double size)
    {
        _fontSize = Math.Clamp(size, MinFont, MaxFont);
        foreach (var doc in _documents)
        {
            doc.Editor.FontSize = _fontSize;
        }
    }

    // ----- tabs -----

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var doc = _documents.FirstOrDefault(d => ReferenceEquals(d.Tab, args.Tab));
        await CloseAsync(doc);
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowStatus(Current);

    private async Task CloseAsync(Document? doc)
    {
        if (doc is null)
        {
            return;
        }

        if (doc.IsDirty && !await ConfirmAsync("Close without saving?", $"\"{doc.Title}\" has changes that are not saved.", "Close"))
        {
            return;
        }

        Close(doc);
        if (_documents.Count == 0)
        {
            NewDocument();
        }
    }

    private void Close(Document doc)
    {
        _documents.Remove(doc);
        Tabs.TabItems.Remove(doc.Tab);
    }

    // ----- dialogs -----

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Header = label, MinWidth = 420 };
        box.SelectionStart = initial.Length;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
