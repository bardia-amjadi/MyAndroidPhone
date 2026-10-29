namespace MyAndroidPhone.Core.Legal;

public sealed record ThirdPartyNotice(string Component, string License, string Url, string Note);

/// <summary>
/// Notices shown in the setup wizard. This is a list of references, not a restatement of license terms:
/// third-party tools stay under their own licenses and are never presented as part of this application's license.
/// </summary>
public static class LegalNotices
{
    /// <summary>Bump when the text below changes so users are asked to accept again.</summary>
    public const string Version = "2026-10-29.1";

    public const string ApplicationLicenseStatement =
        "My Android Phone's own license has not been chosen yet. The repository owner must add a LICENSE file " +
        "before this application is distributed. Until then, no license for the application itself is implied by this screen.";

    public const string SecurityNotice =
        "My Android Phone is a local tool. It talks to your Android device through Android Debug Bridge (adb), which only works " +
        "after you enable USB debugging (or Wireless debugging) on the phone and approve this computer on the phone. " +
        "The application does not bypass that approval, does not root devices, and does not send device data or telemetry anywhere.\n\n" +
        "Features run external programs (adb, scrcpy). When you select an executable yourself, the application runs it once " +
        "with a version argument to check it. Only select executables you trust.\n\n" +
        "Deleting files, uninstalling apps or changing device settings (in later versions) always asks for confirmation first.";

    public static IReadOnlyList<ThirdPartyNotice> ThirdParty { get; } = new[]
    {
        new ThirdPartyNotice(
            "Android SDK Platform-Tools (adb)",
            "Android Software Development Kit License Agreement",
            "https://developer.android.com/studio/terms",
            "Not bundled. You provide it, or a future version downloads it from Google after you approve."),
        new ThirdPartyNotice(
            "scrcpy",
            "Apache License 2.0",
            "https://github.com/Genymobile/scrcpy/blob/master/LICENSE",
            "Not bundled. Optional; only needed for live screen mirroring."),
        new ThirdPartyNotice(
            "Windows App SDK / WinUI 3",
            "Microsoft license terms shipped with the NuGet package",
            "https://www.nuget.org/packages/Microsoft.WindowsAppSDK",
            "UI framework. See the package page for the license that applies to the version in use."),
        new ThirdPartyNotice(
            ".NET Community Toolkit (CommunityToolkit.Mvvm)",
            "MIT License",
            "https://github.com/CommunityToolkit/dotnet/blob/main/License.md",
            "MVVM helpers."),
        new ThirdPartyNotice(
            "Microsoft.Extensions.DependencyInjection / Logging",
            "MIT License",
            "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
            "Dependency injection and logging abstractions.")
    };
}
