using Microsoft.UI.Xaml;
using MyAndroidPhone.Core.Settings;

namespace MyAndroidPhone.Desktop.Helpers;

/// <summary>Static helpers used from x:Bind function bindings.</summary>
public static class Converters
{
    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToVisibilityInverse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility StepVisibility(int current, int step) => current == step ? Visibility.Visible : Visibility.Collapsed;

    public static string SecondaryHeader(bool searching) => searching ? "Location" : "Permissions";

    public static double ToOpacity(bool value) => value ? 1.0 : 0.0;

    public static bool IsTheme(AppTheme theme, string name) =>
        string.Equals(theme.ToString(), name, StringComparison.OrdinalIgnoreCase);
}
