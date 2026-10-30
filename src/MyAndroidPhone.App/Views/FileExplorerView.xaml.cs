using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>
/// File Explorer view. The code-behind covers what needs UI objects: dialogs (they need a XamlRoot), drag and drop and
/// keyboard shortcuts. Everything else is in <see cref="FileExplorerViewModel"/>.
/// </summary>
public sealed partial class FileExplorerView : UserControl
{
    public FileExplorerView(FileExplorerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.PromptAsync = PromptAsync;
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.ChooseConflictAsync = ChooseConflictAsync;

        AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, () => ViewModel.RefreshCommand.Execute(null));
        AddShortcut(VirtualKey.F2, VirtualKeyModifiers.None, () => ViewModel.RenameCommand.Execute(null));
        AddShortcut(VirtualKey.Delete, VirtualKeyModifiers.None, () => ViewModel.DeleteCommand.Execute(null));
        AddShortcut(VirtualKey.C, VirtualKeyModifiers.Control, () => ViewModel.CopyCommand.Execute(null));
        AddShortcut(VirtualKey.X, VirtualKeyModifiers.Control, () => ViewModel.CutCommand.Execute(null));
        AddShortcut(VirtualKey.V, VirtualKeyModifiers.Control, () => ViewModel.PasteCommand.Execute(null));
        AddShortcut(VirtualKey.Up, VirtualKeyModifiers.Menu, () => ViewModel.GoUpCommand.Execute(null));
        AddShortcut(VirtualKey.Back, VirtualKeyModifiers.None, () => ViewModel.GoBackCommand.Execute(null));

        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public FileExplorerViewModel ViewModel { get; }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Do not steal Backspace/Delete/Ctrl+C from text boxes the user is typing in.
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            {
                return;
            }

            action();
            args.Handled = true;
        };
        FileList.KeyboardAccelerators.Add(accelerator);
    }

    // ----- navigation and selection -----

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
        {
            await ViewModel.NavigateAsync(path);
        }
    }

    private async void OnLocationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocationItem location)
        {
            await ViewModel.NavigateAsync(location.Path);
        }
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntryViewModel entry)
        {
            await ViewModel.OpenAsync(entry);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(FileList.SelectedItems.OfType<FileEntryViewModel>());

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
        {
            ViewModel.SortByCommand.Execute(key);
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ViewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ViewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ----- drag and drop -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.CanModify && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Copy to this folder on the phone";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await ViewModel.UploadAsync(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList());
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Drag to Windows Explorer: the files are only downloaded when Explorer actually asks for them (delayed
    /// rendering), so starting a drag never copies anything by itself.
    /// </summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileEntryViewModel>().ToList();
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var paths = await ViewModel.DownloadForDragAsync(items, CancellationToken.None);
                var storage = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        storage.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    }
                    else
                    {
                        storage.Add(await StorageFile.GetFileFromPathAsync(path));
                    }
                }

                request.SetData(storage);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    // ----- dialogs -----

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Header = label, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
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

    private async Task<ConflictPolicy?> ChooseConflictAsync(string location, IReadOnlyList<string> names)
    {
        var list = string.Join(Environment.NewLine, names.Take(8)) + (names.Count > 8 ? Environment.NewLine + $"... and {names.Count - 8} more" : string.Empty);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Items with the same name already exist",
            Content = new TextBlock { Text = $"In {location}:{Environment.NewLine}{list}", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Keep both",
            SecondaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            return ConflictPolicy.KeepBoth;
        }

        if (result == ContentDialogResult.Secondary)
        {
            return await ConfirmAsync("Replace existing items?", "Items with the same name will be overwritten. This cannot be undone.", "Replace") ? ConflictPolicy.Overwrite : null;
        }

        return null;
    }
}
