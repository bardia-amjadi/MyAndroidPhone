using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>Browser for the .system folder (logs, downloads, screenshots, notes).</summary>
public sealed partial class SystemFilesView : UserControl
{
    public SystemFilesView(SystemFilesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PromptAsync = PromptAsync;
        ViewModel.ConfirmAsync = ConfirmAsync;

        var f2 = new KeyboardAccelerator { Key = VirtualKey.F2 };
        f2.Invoked += (_, a) => { ViewModel.RenameCommand.Execute(null); a.Handled = true; };
        var del = new KeyboardAccelerator { Key = VirtualKey.Delete };
        del.Invoked += (_, a) => { ViewModel.DeleteCommand.Execute(null); a.Handled = true; };
        var f5 = new KeyboardAccelerator { Key = VirtualKey.F5 };
        f5.Invoked += (_, a) => { ViewModel.RefreshCommand.Execute(null); a.Handled = true; };
        FileList.KeyboardAccelerators.Add(f2);
        FileList.KeyboardAccelerators.Add(del);
        FileList.KeyboardAccelerators.Add(f5);
    }

    public SystemFilesViewModel ViewModel { get; }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntryViewModel entry)
        {
            await ViewModel.OpenAsync(entry);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(FileList.SelectedItems.OfType<FileEntryViewModel>());

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Copy into this folder";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        await ViewModel.ImportAsync(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList());
    }

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
}
