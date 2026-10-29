using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class SetupWizardView : UserControl
{
    public SetupWizardView(SetupWizardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.Completed += (_, _) => Finished?.Invoke(this, EventArgs.Empty);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public SetupWizardViewModel ViewModel { get; }

    public event EventHandler? Finished;
}
