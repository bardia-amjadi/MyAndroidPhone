using Microsoft.UI.Xaml;
using MyAndroidPhone.Core.Settings;

namespace MyAndroidPhone.Desktop.Services;

/// <summary>Applies the default theme (dark unless changed in Settings) or the open instance's theme to the window's root element.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private FrameworkElement? _root;
    private AppTheme? _override;

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

    /// <summary>An open instance uses its own theme; pass null to go back to the application default.</summary>
    public void UseOverride(AppTheme? theme)
    {
        _override = theme;
        Apply();
    }

    private void Apply()
    {
        if (_root is null)
        {
            return;
        }

        _root.RequestedTheme = (_override ?? _settings.Current.Theme) switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.System => ElementTheme.Default,
            _ => ElementTheme.Dark
        };
    }
}
