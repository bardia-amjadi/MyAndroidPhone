using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class TransferCenterView : UserControl
{
    public TransferCenterView(TransferCenterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public TransferCenterViewModel ViewModel { get; }

    private static TransferItemViewModel? Item(object sender) => (sender as FrameworkElement)?.Tag as TransferItemViewModel;

    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelCommand.Execute(Item(sender));

    private void OnRetry(object sender, RoutedEventArgs e) => ViewModel.RetryCommand.Execute(Item(sender));

    private void OnOpenFolder(object sender, RoutedEventArgs e) => ViewModel.OpenFolderCommand.Execute(Item(sender));
}
