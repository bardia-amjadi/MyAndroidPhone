using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

/// <summary>Read-only presentation of one tool plus its user actions. Rebuilt whenever detection changes.</summary>
public sealed partial class ToolItemViewModel
{
    private readonly ToolActions _actions;
    private readonly ToolInstallation _installation;

    public ToolItemViewModel(ToolInstallation installation, ToolActions actions)
    {
        _installation = installation;
        _actions = actions;
    }

    public ToolId Id => _installation.Definition.Id;

    public string Name => _installation.Definition.DisplayName;

    public string Description => _installation.Definition.Description;

    public string SourceText => $"Source: {_installation.Definition.SourceUrl}";

    public string LicenseText => $"License: {_installation.Definition.LicenseName}";

    public string DependsOnText => _installation.Definition.Dependencies.Count == 0
        ? "Depends on: nothing"
        : "Depends on: " + string.Join(", ", _installation.Definition.Dependencies.Select(d => ToolCatalog.Get(d).ShortName));

    public string OperationsText => "Used for: " + string.Join(", ", _installation.Definition.SupportedOperations);

    public string VersionText => _installation.Version is null ? "Version: not available" : $"Version: {_installation.Version}";

    public string LocationText => _installation.ExecutablePath is null
        ? "Location: not found"
        : $"Location: {_installation.ExecutablePath}  ({DescribeSource(_installation.Source)})";

    public string Message => _installation.Message ?? string.Empty;

    public bool HasMessage => !string.IsNullOrEmpty(_installation.Message);

    public bool IsChecking => _installation.Status == ToolStatus.NotChecked;

    public bool IsFound => _installation.Status == ToolStatus.Found;

    public bool IsInvalid => _installation.Status == ToolStatus.Invalid;

    public bool IsMissing => _installation.Status == ToolStatus.NotFound;

    public bool HasPath => _installation.ExecutablePath is not null;

    public bool HasConfiguredPath => _installation.Source == ToolLocationSource.Configured;

    public string DownloadPageUrl => _installation.Definition.DownloadPageUrl;

    public Uri DownloadPageUri => new(_installation.Definition.DownloadPageUrl);

    [RelayCommand]
    private Task LocateAsync() => _actions.LocateAsync(Id);

    [RelayCommand]
    private Task UseAutomaticAsync() => _actions.UseAutomaticDetectionAsync(Id);

    [RelayCommand]
    private Task OpenFolderAsync() => _actions.OpenFolderAsync(_installation.ExecutablePath);

    private static string DescribeSource(ToolLocationSource source) => source switch
    {
        ToolLocationSource.Configured => "selected by you",
        ToolLocationSource.ManagedFolder => "managed folder",
        ToolLocationSource.AndroidSdkEnvironment => "ANDROID_HOME / ANDROID_SDK_ROOT",
        ToolLocationSource.PathEnvironment => "found on PATH",
        ToolLocationSource.CommonLocation => "common install location",
        _ => "unknown"
    };
}

public sealed partial class ToolManagerViewModel : ObservableObject, IDisposable
{
    private readonly IToolService _tools;
    private readonly ToolActions _actions;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService _notifications;

    public ToolManagerViewModel(IToolService tools, ToolActions actions, IUiDispatcher ui, INotificationService notifications)
    {
        _tools = tools;
        _actions = actions;
        _ui = ui;
        _notifications = notifications;
        _tools.Changed += OnToolsChanged;
        Rebuild();
    }

    public ObservableCollection<ToolItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isRefreshing;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await _tools.RefreshAsync();
        }
        catch (Exception ex)
        {
            _notifications.Post(NotificationSeverity.Error, "Tool detection failed", ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void Dispose() => _tools.Changed -= OnToolsChanged;

    private void OnToolsChanged(object? sender, EventArgs e) => _ui.Post(Rebuild);

    private void Rebuild()
    {
        Items.Clear();
        foreach (var installation in _tools.Installations)
        {
            Items.Add(new ToolItemViewModel(installation, _actions));
        }
    }
}
