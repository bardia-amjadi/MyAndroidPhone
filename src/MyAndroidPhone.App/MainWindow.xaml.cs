using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.Views;

namespace MyAndroidPhone.Desktop;

/// <summary>Hosts either the start screen (list of instances) or the desktop of one instance. It contains no business logic.</summary>
public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        Title = "My Android Phone";
        ExtendsContentIntoTitleBar = false;

        // Mica where the OS supports it; the window falls back to a solid theme background otherwise.
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
            // Unsupported on this OS build: keep the default background.
        }

        _services.GetRequiredService<ThemeService>().Attach(RootGrid);
    }

    public void ShowStartup() => ShowHome();

    private void ShowHome()
    {
        Title = "My Android Phone";
        var home = _services.GetRequiredService<HomeView>();
        home.InstanceOpenRequested += (_, instance) => ShowDesktop(instance);
        Presenter.Content = home;
    }

    private void ShowDesktop(AndroidInstance instance)
    {
        Title = instance.Name + " - My Android Phone";
        var shell = _services.GetRequiredService<ShellView>();
        shell.SwitchInstanceRequested += (_, _) => ShowHome();
        Presenter.Content = shell;
        shell.Open(instance);
    }
}
