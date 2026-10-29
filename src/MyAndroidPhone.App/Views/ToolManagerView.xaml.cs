using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class ToolManagerView : UserControl
{
    public ToolManagerView(ToolManagerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public ToolManagerViewModel ViewModel { get; }
}
