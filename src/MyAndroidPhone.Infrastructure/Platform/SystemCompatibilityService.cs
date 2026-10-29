using System.Runtime.InteropServices;
using MyAndroidPhone.Core.Platform;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Infrastructure.Platform;

public sealed class SystemCompatibilityService(AppPaths paths) : ISystemCompatibilityService
{
    /// <summary>
    /// Windows 10 version 1809. This is the minimum OS build documented for the Windows App SDK 1.x line this
    /// project pins; re-verify it against the Microsoft documentation whenever the SDK version changes.
    /// </summary>
    public const int MinimumWindowsBuild = 17763;

    private const long MinimumFreeBytes = 200L * 1024 * 1024;

    public CompatibilityReport Check()
    {
        var issues = new List<string>();
        var warnings = new List<string>();
        var version = Environment.OSVersion.Version;
        var architecture = RuntimeInformation.OSArchitecture;

        string description;
        if (!OperatingSystem.IsWindows())
        {
            description = RuntimeInformation.OSDescription;
            issues.Add($"My Android Phone is a Windows application; this system reports '{description}'.");
        }
        else
        {
            description = version.Build >= 22000
                ? $"Windows 11 (build {version.Build})"
                : $"Windows 10 (build {version.Build})";
            if (version.Build < MinimumWindowsBuild)
            {
                issues.Add($"Windows build {version.Build} is older than the minimum supported build {MinimumWindowsBuild} (Windows 10 version 1809).");
            }
        }

        if (architecture is not (Architecture.X86 or Architecture.X64 or Architecture.Arm64))
        {
            issues.Add($"Processor architecture {architecture} is not supported.");
        }

        CheckDataFolder(warnings, issues);

        return new CompatibilityReport(issues.Count == 0, description, version, architecture.ToString(), issues, warnings);
    }

    private void CheckDataFolder(List<string> warnings, List<string> issues)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var probe = Path.Combine(paths.Root, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"The application data folder is not writable ({ex.Message}).");
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(paths.Root));
            if (!string.IsNullOrEmpty(root))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < MinimumFreeBytes)
                {
                    warnings.Add($"Only {free / (1024 * 1024)} MB free on the drive holding the application data folder.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            warnings.Add("Free disk space could not be determined.");
        }
    }
}
