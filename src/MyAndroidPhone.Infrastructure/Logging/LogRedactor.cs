using System.Text.RegularExpressions;

namespace MyAndroidPhone.Infrastructure.Logging;

/// <summary>Removes personal paths and secret-looking values from log lines and exported reports.</summary>
public static partial class LogRedactor
{
    [GeneratedRegex(@"(?i)\b(pairing[ _-]?code|password|passwd|token|secret|api[ _-]?key)\b(\s*[:=]\s*)\S+")]
    private static partial Regex SecretPairs();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = SecretPairs().Replace(text, "$1$2[redacted]");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && profile.Length > 3)
        {
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
