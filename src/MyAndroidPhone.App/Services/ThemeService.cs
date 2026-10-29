using Microsoft.UI.Xaml;
using MyAndroidPhone.Core.Settings;

namespace MyAndroidPhone.Desktop.Services;

/// <summary>Applies the persisted theme (dark by default) to the window's root element and follows setting changes.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private FrameworkElement? _root;

    public ThemeService(ISettingsService settings, IUiDispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _settings.Changed += (_, _) => _ui.Post(Apply);
    }

    public void Attach(FrameworkElement root)
    {
        _root = root;
        Apply();
    }

    private void Apply()
    {
        if (_root is null)
        {
            return;
        }

        _root.RequestedTheme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.System => ElementTheme.Default,
            _ => ElementTheme.Dark
        };
    }
}
