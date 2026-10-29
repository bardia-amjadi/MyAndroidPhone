using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Desktop.ViewModels;

/// <summary>Application settings. Every change is written to disk immediately; failures are reported, not hidden.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IToolService _tools;
    private readonly IFolderLauncher _launcher;
    private readonly INotificationService _notifications;
    private bool _loading;

    public SettingsViewModel(
        ISettingsService settings,
        IToolService tools,
        IFolderLauncher launcher,
        INotificationService notifications,
        AppPaths paths)
    {
        _settings = settings;
        _tools = tools;
        _launcher = launcher;
        _notifications = notifications;
        DataFolder = paths.Root;
        LogsFolder = paths.LogsDirectory;
        Load();
    }

    public string DataFolder { get; }

    public string LogsFolder { get; }

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private bool _showDockLabels;

    [ObservableProperty]
    private int _logVerbosityIndex;

    [ObservableProperty]
    private double _logFileSizeMegabytes;

    [ObservableProperty]
    private double _logRetainedFiles;

    partial void OnThemeIndexChanged(int value)
    {
        if (value >= 0)
        {
            Persist(s => s.Theme = (AppTheme)value);
        }
    }

    partial void OnShowDockLabelsChanged(bool value) => Persist(s => s.ShowDockLabels = value);

    partial void OnLogVerbosityIndexChanged(int value)
    {
        if (value >= 0)
        {
            Persist(s => s.LogVerbosity = (LogVerbosity)value);
        }
    }

    partial void OnLogFileSizeMegabytesChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Persist(s => s.LogFileSizeMegabytes = (int)Math.Clamp(value, 1, 100));
        }
    }

    partial void OnLogRetainedFilesChanged(double value)
    {
        if (!double.IsNaN(value))
        {
            Persist(s => s.LogRetainedFiles = (int)Math.Clamp(value, 1, 30));
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        try
        {
            await _settings.ResetAsync();
            Load();
            await _tools.RefreshAsync();
            _notifications.Post(NotificationSeverity.Success, "Settings reset", "Application settings were restored to their defaults. Tools were detected again.");
        }
        catch (Exception ex)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not reset settings", ex.Message);
        }
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => OpenAsync(DataFolder);

    [RelayCommand]
    private Task OpenLogsFolderAsync() => OpenAsync(LogsFolder);

    private async Task OpenAsync(string folder)
    {
        if (!await _launcher.OpenFolderAsync(folder))
        {
            _notifications.Post(NotificationSeverity.Warning, "Folder not available", folder);
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var current = _settings.Current;
            ThemeIndex = (int)current.Theme;
            ShowDockLabels = current.ShowDockLabels;
            LogVerbosityIndex = (int)current.LogVerbosity;
            LogFileSizeMegabytes = current.LogFileSizeMegabytes;
            LogRetainedFiles = current.LogRetainedFiles;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Persist(Action<AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        _ = PersistAsync(change);
    }

    private async Task PersistAsync(Action<AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change);
        }
        catch (Exception ex)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not save settings", ex.Message);
        }
    }
}
