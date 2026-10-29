namespace MyAndroidPhone.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
    System
}

public enum LogVerbosity
{
    Normal,
    Verbose
}

/// <summary>User preferences persisted as JSON. Contains no secrets and no device data.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public bool ShowDockLabels { get; set; } = true;

    public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

    public int LogFileSizeMegabytes { get; set; } = 5;

    public int LogRetainedFiles { get; set; } = 5;

    /// <summary>Tool id (see <c>ToolId</c>) to the executable path the user selected.</summary>
    public Dictionary<string, string> ConfiguredToolPaths { get; set; } = new();
}
