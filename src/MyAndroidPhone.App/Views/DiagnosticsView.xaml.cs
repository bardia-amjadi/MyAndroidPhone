using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView(DiagnosticsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public DiagnosticsViewModel ViewModel { get; }
}
