using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class CreateInstanceView : UserControl
{
    public CreateInstanceView(CreateInstanceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public CreateInstanceViewModel ViewModel { get; }
}
