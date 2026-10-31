using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>The start screen. Hosts the create wizard as a pop-up layer inside the app window.</summary>
public sealed partial class HomeView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly INotificationService _notifications;
    private CreateInstanceView? _wizard;

    public HomeView(HomeViewModel viewModel, IServiceProvider services, INotificationService notifications)
    {
        ViewModel = viewModel;
        _services = services;
        _notifications = notifications;
        InitializeComponent();

        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.WizardRequested += (_, acceptOnly) => ShowWizard(acceptOnly, null);
        ViewModel.LicenseRequiredToOpen += (_, instance) => ShowWizard(true, instance);
        ViewModel.OpenRequested += (_, instance) => InstanceOpenRequested?.Invoke(this, instance);

        _notifications.Posted += OnNotificationPosted;
        foreach (var pending in _notifications.DrainPending())
        {
            ShowNotification(pending);
        }

        Unloaded += (_, _) =>
        {
            _notifications.Posted -= OnNotificationPosted;
            ViewModel.Dispose();
        };
    }

    public HomeViewModel ViewModel { get; }

    /// <summary>The main window switches to this instance's desktop.</summary>
    public event EventHandler<AndroidInstance>? InstanceOpenRequested;

    private void ShowWizard(bool acceptOnly, AndroidInstance? thenOpen)
    {
        CloseWizard();
        var model = _services.GetRequiredService<CreateInstanceViewModel>();
        if (acceptOnly)
        {
            model.BeginAcceptOnly();
        }

        model.Cancelled += (_, _) => CloseWizard();
        model.LicenseAccepted += (_, _) =>
        {
            CloseWizard();
            if (thenOpen is not null)
            {
                ViewModel.OpenAfterWizard(thenOpen);
            }
        };
        model.Created += (_, instance) =>
        {
            CloseWizard();
            ViewModel.OpenAfterWizard(instance);
        };

        _wizard = new CreateInstanceView(model);
        WizardHost.Content = _wizard;
        WizardLayer.Visibility = Visibility.Visible;
        if (!acceptOnly)
        {
            _ = model.RefreshDevicesCommand.ExecuteAsync(null);
        }
    }

    private void CloseWizard()
    {
        WizardLayer.Visibility = Visibility.Collapsed;
        WizardHost.Content = null;
        _wizard = null;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InstanceItemViewModel item)
        {
            ViewModel.OpenCommand.Execute(item);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => ViewModel.OpenCommand.Execute((sender as FrameworkElement)?.Tag as InstanceItemViewModel);

    private void OnRemoveClick(object sender, RoutedEventArgs e) => ViewModel.RemoveCommand.Execute((sender as FrameworkElement)?.Tag as InstanceItemViewModel);

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnNotificationPosted(object? sender, AppNotification notification) =>
        DispatcherQueue.TryEnqueue(() => ShowNotification(notification));

    private void ShowNotification(AppNotification notification)
    {
        var bar = new InfoBar
        {
            IsOpen = true,
            Title = notification.Title,
            Message = notification.Message,
            Severity = notification.Severity switch
            {
                NotificationSeverity.Success => InfoBarSeverity.Success,
                NotificationSeverity.Warning => InfoBarSeverity.Warning,
                NotificationSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            }
        };
        bar.CloseButtonClick += (_, _) => NotificationHost.Children.Remove(bar);
        NotificationHost.Children.Add(bar);
        while (NotificationHost.Children.Count > 3)
        {
            NotificationHost.Children.RemoveAt(0);
        }

        if (notification.Severity != NotificationSeverity.Error)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(6);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => NotificationHost.Children.Remove(bar);
            timer.Start();
        }
    }
}
