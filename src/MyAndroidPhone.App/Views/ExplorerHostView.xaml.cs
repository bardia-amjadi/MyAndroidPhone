using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Android;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>
/// File Explorer's front: the root "/" with the two important folders. .android is the phone (the phone browser) and
/// .system is the instance's own space on this PC.
/// </summary>
public sealed partial class ExplorerHostView : UserControl
{
    private readonly FileExplorerView _android;
    private readonly SystemFilesView _system;
    private readonly DesktopCommands _commands;
    private readonly ActiveInstance _active;
    private bool _systemLoaded;

    public ExplorerHostView(FileExplorerView android, SystemFilesView system, DesktopCommands commands, ActiveInstance active)
    {
        _android = android;
        _system = system;
        _commands = commands;
        _active = active;
        InitializeComponent();

        AndroidHost.Children.Add(android);
        SystemHost.Children.Add(system);
        AndroidText.Text = _active.Current is { } instance ? $"The files on {instance.Name}." : "The files on your phone.";

        android.HomeRequested += (_, _) => ShowRoot();
        system.ViewModel.HomeRequested += (_, _) => ShowRoot();

        Loaded += async (_, _) =>
        {
            _commands.ExplorerFoldersQueued += OnFoldersQueued;
            await DrainAsync();
        };
        Unloaded += (_, _) => _commands.ExplorerFoldersQueued -= OnFoldersQueued;
    }

    private void Show(UIElement pane)
    {
        RootPane.Visibility = ReferenceEquals(pane, RootPane) ? Visibility.Visible : Visibility.Collapsed;
        AndroidHost.Visibility = ReferenceEquals(pane, AndroidHost) ? Visibility.Visible : Visibility.Collapsed;
        SystemHost.Visibility = ReferenceEquals(pane, SystemHost) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowRoot() => Show(RootPane);

    private void OnOpenAndroid(object sender, RoutedEventArgs e) => Show(AndroidHost);

    private async void OnOpenSystem(object sender, RoutedEventArgs e) => await ShowSystemAsync(null);

    private async Task ShowSystemAsync(string? virtualPath)
    {
        Show(SystemHost);
        if (virtualPath is not null)
        {
            await _system.ViewModel.NavigateToPathAsync(virtualPath);
            _systemLoaded = true;
        }
        else if (!_systemLoaded)
        {
            await _system.ViewModel.InitializeAsync();
            _systemLoaded = true;
        }
        else
        {
            await _system.ViewModel.NavigateAsync(_system.ViewModel.Current);
        }
    }

    private async void OnFoldersQueued(object? sender, EventArgs e) => await DrainAsync();

    private async Task DrainAsync()
    {
        while (_commands.TakeExplorerFolder() is { } folder)
        {
            var location = VirtualPath.Resolve(VirtualLocation.Root, folder);
            switch (location?.Area)
            {
                case VirtualArea.System:
                    await ShowSystemAsync(location.Display);
                    break;
                case VirtualArea.Android:
                    Show(AndroidHost);
                    await _android.NavigateWhenReadyAsync(location.Path);
                    break;
                default:
                    ShowRoot();
                    break;
            }
        }
    }
}
