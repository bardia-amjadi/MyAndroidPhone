using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class DeviceCenterView : UserControl
{
    public DeviceCenterView(DeviceCenterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public DeviceCenterViewModel ViewModel { get; }
}
