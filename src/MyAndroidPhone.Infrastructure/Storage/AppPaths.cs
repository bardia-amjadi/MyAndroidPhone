namespace MyAndroidPhone.Infrastructure.Storage;

/// <summary>All locations the application reads or writes. Nothing else touches these paths directly.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyAndroidPhone");
        SettingsFile = Path.Combine(Root, "settings.json");
        SetupStateFile = Path.Combine(Root, "setup-state.json");
        DevicesFile = Path.Combine(Root, "devices.json");
        LogsDirectory = Path.Combine(Root, "logs");
        ToolsDirectory = Path.Combine(Root, "tools");
    }

    public string Root { get; }

    public string SettingsFile { get; }

    public string SetupStateFile { get; }

    public string DevicesFile { get; }

    public string LogsDirectory { get; }

    /// <summary>Folder for tools the application manages itself. Existing user installations are never written here.</summary>
    public string ToolsDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ToolsDirectory);
    }
}
