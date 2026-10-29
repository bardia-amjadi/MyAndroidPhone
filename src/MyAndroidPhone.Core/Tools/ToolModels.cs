namespace MyAndroidPhone.Core.Tools;

public enum ToolId
{
    Adb,
    Scrcpy
}

public enum ToolStatus
{
    /// <summary>Detection has not run yet.</summary>
    NotChecked,
    NotFound,
    /// <summary>A file was found or configured but did not behave like the expected tool.</summary>
    Invalid,
    Found
}

public enum ToolLocationSource
{
    None,
    Configured,
    ManagedFolder,
    AndroidSdkEnvironment,
    PathEnvironment,
    CommonLocation
}

public sealed record ToolDefinition(
    ToolId Id,
    string DisplayName,
    string ShortName,
    string Description,
    string ExecutableBaseName,
    IReadOnlyList<string> VersionArguments,
    string SourceUrl,
    string DownloadPageUrl,
    string LicenseName,
    string LicenseUrl,
    IReadOnlyList<ToolId> Dependencies,
    IReadOnlyList<string> SupportedOperations);

public sealed record ToolInstallation(
    ToolDefinition Definition,
    ToolStatus Status,
    string? ExecutablePath,
    string? Version,
    ToolLocationSource Source,
    string? Message)
{
    public bool IsUsable => Status == ToolStatus.Found;
}

/// <summary>The built-in tool catalog. Add a tool here only when a concrete implemented feature needs it.</summary>
public static class ToolCatalog
{
    public static ToolDefinition Adb { get; } = new(
        ToolId.Adb,
        "Android Debug Bridge (adb)",
        "ADB",
        "Command-line bridge used by every device feature (discovery, files, apps, wireless pairing). Ships in Android SDK Platform-Tools.",
        "adb",
        new[] { "version" },
        "https://developer.android.com/tools/releases/platform-tools",
        "https://developer.android.com/tools/releases/platform-tools",
        "Android Software Development Kit License Agreement",
        "https://developer.android.com/studio/terms",
        Array.Empty<ToolId>(),
        new[] { "Device discovery", "File transfer", "App management", "Wireless pairing" });

    public static ToolDefinition Scrcpy { get; } = new(
        ToolId.Scrcpy,
        "scrcpy",
        "scrcpy",
        "Open-source screen mirroring and control for Android devices. Only needed for the Run module.",
        "scrcpy",
        new[] { "--version" },
        "https://github.com/Genymobile/scrcpy",
        "https://github.com/Genymobile/scrcpy/releases",
        "Apache License 2.0",
        "https://github.com/Genymobile/scrcpy/blob/master/LICENSE",
        new[] { ToolId.Adb },
        new[] { "Screen mirroring", "Input control", "Screen recording" });

    public static IReadOnlyList<ToolDefinition> All { get; } = new[] { Adb, Scrcpy };

    public static ToolDefinition Get(ToolId id) => All.First(t => t.Id == id);
}
