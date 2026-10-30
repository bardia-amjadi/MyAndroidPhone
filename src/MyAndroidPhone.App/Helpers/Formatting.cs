using System.Globalization;

namespace MyAndroidPhone.Desktop.Helpers;

public static class Formatting
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.Value.ToString(CultureInfo.CurrentCulture)} B"
            : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? string.Empty : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
