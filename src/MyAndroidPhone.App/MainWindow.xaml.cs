using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Legal;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.ViewModels;
using MyAndroidPhone.Desktop.Views;

namespace MyAndroidPhone.Desktop;

/// <summary>Hosts either the first-run wizard or the desktop shell. It contains no business logic.</summary>
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

    public void ShowStartup()
    {
        var setup = _services.GetRequiredService<ISetupStateService>().Current;
        var needsSetup = !setup.Completed || setup.AcceptedNoticesVersion != LegalNotices.Version;
        if (needsSetup)
        {
            ShowWizard();
        }
        else
        {
            ShowShell();
        }
    }

    private void ShowWizard()
    {
        var wizard = _services.GetRequiredService<SetupWizardView>();
        wizard.Finished += (_, _) => ShowShell();
        Presenter.Content = wizard;
    }

    private void ShowShell()
    {
        var shell = _services.GetRequiredService<ShellView>();
        shell.SetupRerunRequested += (_, _) => ShowWizard();
        Presenter.Content = shell;
    }
}
