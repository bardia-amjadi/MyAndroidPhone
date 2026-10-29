using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Features;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed partial class DockItemViewModel : ObservableObject
{
    private readonly Action<string> _open;

    public DockItemViewModel(FeatureDescriptor descriptor, Action<string> open, bool showLabel)
    {
        Descriptor = descriptor;
        _open = open;
        _showLabel = showLabel;
    }

    public FeatureDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string IconGlyph => Descriptor.IconGlyph;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _showLabel;

    [RelayCommand]
    private void Open() => _open(Id);
}

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IFeatureRegistry _features;
    private readonly IToolService _tools;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;

    public ShellViewModel(IFeatureRegistry features, IToolService tools, ISettingsService settings, IUiDispatcher ui)
    {
        _features = features;
        _tools = tools;
        _settings = settings;
        _ui = ui;

        foreach (var feature in features.All.Where(f => f.ShowInDock))
        {
            DockItems.Add(new DockItemViewModel(feature, RequestOpen, settings.Current.ShowDockLabels));
        }

        _theme = settings.Current.Theme;
        _toolSummary = BuildToolSummary();
        _tools.Changed += OnStateChanged;
        _settings.Changed += OnStateChanged;
    }

    public ObservableCollection<DockItemViewModel> DockItems { get; } = new();

    public IReadOnlyList<FeatureDescriptor> Features => _features.All;

    public event EventHandler<string>? OpenFeatureRequested;

    public event EventHandler? SetupRerunRequested;

    [ObservableProperty]
    private string _toolSummary;

    [ObservableProperty]
    private AppTheme _theme;

    public void RequestOpen(string featureId) => OpenFeatureRequested?.Invoke(this, featureId);

    public void RequestSetupRerun() => SetupRerunRequested?.Invoke(this, EventArgs.Empty);

    public Task SetThemeAsync(AppTheme theme) => _settings.UpdateAsync(s => s.Theme = theme);

    public void SetOpenWindows(IReadOnlySet<string> openFeatureIds)
    {
        foreach (var item in DockItems)
        {
            item.IsOpen = openFeatureIds.Contains(item.Id);
        }
    }

    public void Dispose()
    {
        _tools.Changed -= OnStateChanged;
        _settings.Changed -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        ToolSummary = BuildToolSummary();
        Theme = _settings.Current.Theme;
        foreach (var item in DockItems)
        {
            item.ShowLabel = _settings.Current.ShowDockLabels;
        }
    });

    private string BuildToolSummary()
    {
        var parts = _tools.Installations.Select(i => i.Status switch
        {
            ToolStatus.Found => $"{i.Definition.ShortName} {i.Version}",
            ToolStatus.Invalid => $"{i.Definition.ShortName} needs attention",
            ToolStatus.NotFound => $"{i.Definition.ShortName} not found",
            _ => $"{i.Definition.ShortName} checking…"
        });
        return string.Join("   ·   ", parts);
    }
}
