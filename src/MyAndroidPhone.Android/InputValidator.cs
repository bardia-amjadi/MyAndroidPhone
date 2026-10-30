using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Android;

/// <summary>
/// Strict validation of everything the user types that reaches adb. Commands never go through a shell, but adb
/// itself would treat a leading '-' as an option, so values like that are rejected here.
/// </summary>
public static partial class InputValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex Hostname();

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex PairingCode();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    /// <summary>Accepts "IPv4:port" or "hostname:port" and returns it normalized. IPv6 is not supported yet.</summary>
    public static OperationResult<string> NormalizeEndpoint(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Invalid("Enter the address as IP:port, for example 192.168.1.20:37215.");
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return Invalid("The address must include a port, for example 192.168.1.20:37215.");
        }

        var host = text[..colon];
        var portText = text[(colon + 1)..];
        if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return Invalid("The port must be a number between 1 and 65535.");
        }

        if (!IsValidHost(host))
        {
            return Invalid("The host must be an IPv4 address (like 192.168.1.20) or a simple host name.");
        }

        return OperationResult<string>.Success($"{host}:{port}");
    }

    public static OperationResult<string> ValidatePairingCode(string? code)
    {
        var text = code?.Trim() ?? string.Empty;
        return PairingCode().IsMatch(text)
            ? OperationResult<string>.Success(text)
            : Invalid("The pairing code is the 6 digits shown in the phone's pairing dialog.");
    }

    public static OperationResult<string> ValidateSerial(string? serial)
    {
        var text = serial ?? string.Empty;
        if (text.Length == 0 || text.Length > 128 || text[0] == '-' || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return Invalid("That is not a valid device identifier.");
        }

        return OperationResult<string>.Success(text);
    }

    public static bool IsValidColor(string? value) => value is not null && HexColor().IsMatch(value);

    private static bool IsValidHost(string host)
    {
        var parts = host.Split('.');
        if (parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit)))
        {
            return parts.All(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) <= 255);
        }

        return Hostname().IsMatch(host);
    }

    private static OperationResult<string> Invalid(string message) =>
        OperationResult<string>.Failure(ErrorKind.InvalidInput, message);
}
