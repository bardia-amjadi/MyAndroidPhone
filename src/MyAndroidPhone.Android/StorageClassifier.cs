using System.Text.RegularExpressions;
using MyAndroidPhone.Core.Files;

namespace MyAndroidPhone.Android;

/// <summary>
/// Explains what kind of place a path is. The app never roots a device and never claims access it does not have, so
/// protected and system areas are labelled and writes there are refused up front.
/// </summary>
public static partial class StorageClassifier
{
    [GeneratedRegex(@"^/storage/[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}(/|$)")]
    private static partial Regex RemovableVolume();

    private static readonly string[] SharedRoots = { "/sdcard", "/storage/emulated", "/storage/self", "/mnt/sdcard", "/mnt/user", "/mnt/runtime" };

    private static readonly string[] SystemRoots =
    {
        "/system", "/system_ext", "/vendor", "/product", "/odm", "/apex", "/proc", "/sys", "/dev", "/bin", "/etc", "/lib", "/sbin", "/init",
        "/cache", "/config", "/metadata", "/mnt/vendor"
    };

    public static StorageClassification Classify(string normalizedPath)
    {
        if (RemotePath.IsSameOrInside(normalizedPath, "/data/local/tmp"))
        {
            return new(StorageAccess.ShellWorkspace, "Shell workspace",
                "A scratch folder that the adb shell can read and write. Files here are not visible to most apps and may be cleared by the system.", false);
        }

        if (RemotePath.IsSameOrInside(normalizedPath, "/data"))
        {
            return new(StorageAccess.ProtectedAppData, "Protected app and system data",
                "Android keeps this private. Without root, the adb shell cannot read it (only debuggable apps allow run-as). This app does not enable root or bypass Android security. " +
                "To get an app's data, use the app's own export or backup feature, or copy files from its shared-storage folders.", true);
        }

        if (SystemRoots.Any(r => RemotePath.IsSameOrInside(normalizedPath, r)))
        {
            return new(StorageAccess.System, "System files (read-only)",
                "The operating system's own files. They can usually be listed and copied to the computer, but this app does not modify them.", true);
        }

        var shared = SharedRoots.Any(r => RemotePath.IsSameOrInside(normalizedPath, r)) || RemovableVolume().IsMatch(normalizedPath);
        if (shared)
        {
            var appSpecific = normalizedPath.Contains("/Android/data", StringComparison.Ordinal) || normalizedPath.Contains("/Android/obb", StringComparison.Ordinal);
            if (appSpecific)
            {
                return new(StorageAccess.AppSpecific, "App-specific storage",
                    "Folders that belong to individual apps. Apps cannot see each other's folders on Android 11 and later, but the adb shell usually can. Changing these files can break the app that owns them.", false);
            }

            return new(StorageAccess.SharedStorage, "Shared storage",
                "Your photos, downloads, documents and other files. Normally accessible.", false);
        }

        return new(StorageAccess.Other, "Device file system",
            "Part of Android's file system outside shared storage. Many folders here are restricted and listing may be refused.", normalizedPath == "/");
    }
}
