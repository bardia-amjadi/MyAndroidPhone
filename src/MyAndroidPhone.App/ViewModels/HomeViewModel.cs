using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Legal;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed class InstanceItemViewModel
{
    public InstanceItemViewModel(AndroidInstance instance, AndroidDevice? device)
    {
        Instance = instance;
        Device = device;
    }

    public AndroidInstance Instance { get; }

    public AndroidDevice? Device { get; }

    public Guid Id => Instance.Id;

    public string Name => Instance.Name;

    public string Glyph => Instance.IconGlyph;

    public Brush AccentBrush => ColorHelper.Brush(Instance.AccentColor);

    public string Model => Instance.DeviceModel ?? Instance.DeviceSerial;

    public bool IsConnected => Device is { IsOnline: true };

    public string ConnectionText => Instance.ConnectionType switch
    {
        DeviceConnectionType.Usb => "USB",
        DeviceConnectionType.Wireless => "Network",
        DeviceConnectionType.Emulator => "Emulator",
        _ => string.Empty
    };

    public string StatusText => IsConnected ? $"Connected - {ConnectionText}" : $"Not connected - {ConnectionText}";

    public string LastOpenedText => Instance.LastOpened is { } t ? "Opened " + t.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "Never opened";
}

/// <summary>The start screen: every instance that was created, plus the way to create a new one.</summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IInstanceService _instances;
    private readonly IDeviceService _devices;
    private readonly ISetupStateService _setup;
    private readonly IToolService _tools;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService _notifications;

    public HomeViewModel(
        IInstanceService instances,
        IDeviceService devices,
        ISetupStateService setup,
        IToolService tools,
        IUiDispatcher ui,
        INotificationService notifications)
    {
        _instances = instances;
        _devices = devices;
        _setup = setup;
        _tools = tools;
        _ui = ui;
        _notifications = notifications;
        _instances.Changed += OnStateChanged;
        _devices.Changed += OnStateChanged;
        _tools.Changed += OnStateChanged;
        Rebuild();
    }

    public ObservableCollection<InstanceItemViewModel> Items { get; } = new();

    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>The view shows the create wizard; the argument is true when only the license step is needed.</summary>
    public event EventHandler<bool>? WizardRequested;

    /// <summary>An instance should be opened (the license is already accepted).</summary>
    public event EventHandler<AndroidInstance>? OpenRequested;

    /// <summary>The license notice changed: the view shows it first and calls <see cref="OpenRequested"/> after acceptance.</summary>
    public event EventHandler<AndroidInstance>? LicenseRequiredToOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstances))]
    private int _count;

    [ObservableProperty]
    private string _toolSummary = string.Empty;

    public bool HasInstances => Count > 0;

    public bool NoInstances => Count == 0;

    private void OnStateChanged(object? sender, EventArgs e) => _ui.Post(Rebuild);

    private void Rebuild()
    {
        Items.Clear();
        foreach (var instance in _instances.Instances)
        {
            var device = _devices.Devices.FirstOrDefault(d => d.Serial == instance.DeviceSerial);
            Items.Add(new InstanceItemViewModel(instance, device));
        }

        Count = Items.Count;
        OnPropertyChanged(nameof(NoInstances));
        ToolSummary = string.Join("   -   ", _tools.Installations.Select(i => i.Status switch
        {
            ToolStatus.Found => $"{i.Definition.ShortName} {i.Version}",
            ToolStatus.Invalid => $"{i.Definition.ShortName} needs attention",
            ToolStatus.NotFound => $"{i.Definition.ShortName} not found",
            _ => $"{i.Definition.ShortName} checking..."
        }));
    }

    [RelayCommand]
    private void Create() => WizardRequested?.Invoke(this, false);

    [RelayCommand]
    private void Open(InstanceItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_setup.Current.AcceptedNoticesVersion != LegalNotices.Version)
        {
            LicenseRequiredToOpen?.Invoke(this, item.Instance);
            return;
        }

        OpenRequested?.Invoke(this, item.Instance);
    }

    /// <summary>Called by the view after a wizard finished creating an instance or after the license was accepted.</summary>
    public void OpenAfterWizard(AndroidInstance instance) => OpenRequested?.Invoke(this, instance);

    [RelayCommand]
    private async Task RemoveAsync(InstanceItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirmed = ConfirmAsync is not null && await ConfirmAsync(
            $"Remove \"{item.Name}\"?",
            "This removes the saved instance from this app. The phone and its files are not touched, and you can create the instance again any time.",
            "Remove");
        if (!confirmed)
        {
            return;
        }

        await _instances.DeleteAsync(item.Id);
        _notifications.Post(NotificationSeverity.Information, "Instance removed", item.Name);
    }

    public void Dispose()
    {
        _instances.Changed -= OnStateChanged;
        _devices.Changed -= OnStateChanged;
        _tools.Changed -= OnStateChanged;
    }
}
