using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace MyAndroidPhone.Desktop.Helpers;

public static class ColorHelper
{
    public static Windows.UI.Color Parse(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        return Windows.UI.Color.FromArgb(255, 59, 130, 246);
    }

    public static SolidColorBrush Brush(string? hex) => new(Parse(hex));
}
