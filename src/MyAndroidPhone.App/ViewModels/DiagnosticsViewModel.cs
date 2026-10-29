using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Logging;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Infrastructure.Diagnostics;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsReportBuilder _builder;
    private readonly IRecentLogSource _recentLog;
    private readonly IFilePickerService _picker;
    private readonly IFolderLauncher _launcher;
    private readonly INotificationService _notifications;
    private readonly AppPaths _paths;

    public DiagnosticsViewModel(
        DiagnosticsReportBuilder builder,
        IRecentLogSource recentLog,
        IFilePickerService picker,
        IFolderLauncher launcher,
        INotificationService notifications,
        AppPaths paths)
    {
        _builder = builder;
        _recentLog = recentLog;
        _picker = picker;
        _launcher = launcher;
        _notifications = notifications;
        _paths = paths;
        Refresh();
    }

    [ObservableProperty]
    private string _reportText = string.Empty;

    [ObservableProperty]
    private string _recentLogText = string.Empty;

    [RelayCommand]
    private void Refresh()
    {
        ReportText = _builder.Build();
        var lines = _recentLog.GetRecentLines();
        RecentLogText = lines.Count == 0 ? "No log entries yet in this session." : string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            Refresh();
            var suggested = $"MyAndroidPhone-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var path = await _picker.PickSaveFileAsync(suggested, "Text report", ".txt");
            if (path is null)
            {
                return;
            }

            await File.WriteAllTextAsync(path, ReportText);
            _notifications.Post(NotificationSeverity.Success, "Diagnostics report saved", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Post(NotificationSeverity.Error, "Could not save the report", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenLogsFolderAsync()
    {
        if (!await _launcher.OpenFolderAsync(_paths.LogsDirectory))
        {
            _notifications.Post(NotificationSeverity.Warning, "Folder not available", _paths.LogsDirectory);
        }
    }
}
