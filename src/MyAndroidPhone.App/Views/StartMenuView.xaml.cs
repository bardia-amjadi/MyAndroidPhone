using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.System;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>The Device Center menu that opens above the Dock, like a start menu.</summary>
public sealed partial class StartMenuView : UserControl
{
    public StartMenuView(StartMenuViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        IsTabStop = true;
        KeyDown += OnPanelKeyDown;
    }

    public StartMenuViewModel ViewModel { get; }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Toggle()
    {
        if (IsOpen)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        Visibility = Visibility.Visible;
        ViewModel.OnOpened();
        Focus(FocusState.Programmatic);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        ViewModel.OnClosed();
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => Hide();

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private void OnLauncherClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LauncherItem item)
        {
            Hide();
            item.Launch();
        }
    }
}
