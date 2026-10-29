namespace MyAndroidPhone.Core.Platform;

public sealed record CompatibilityReport(
    bool IsSupported,
    string OsDescription,
    Version OsVersion,
    string Architecture,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings);

public interface ISystemCompatibilityService
{
    CompatibilityReport Check();
}
