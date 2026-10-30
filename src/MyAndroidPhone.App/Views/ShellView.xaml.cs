using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.Foundation;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>
/// The desktop workspace: menu bar, status area, floating module windows and the Dock. The code-behind only handles
/// window chrome (placement, dragging, closing); what a module does lives in its view model.
/// </summary>
public sealed partial class ShellView : UserControl
{
    private readonly ModuleViewFactory _modules;
    private readonly IToolService _tools;
    private readonly INotificationService _notifications;
    private readonly Dictionary<string, ModuleFrame> _openWindows = new();
    private int _cascade;

    private ModuleFrame? _dragging;
    private Point _dragOffset;

    public ShellView(
        ShellViewModel viewModel,
        ModuleViewFactory modules,
        IToolService tools,
        INotificationService notifications)
    {
        ViewModel = viewModel;
        _modules = modules;
        _tools = tools;
        _notifications = notifications;
        InitializeComponent();

        ViewModel.OpenFeatureRequested += (_, id) => OpenModule(id);
        ViewModel.SetupRerunRequested += (_, _) => SetupRerunRequested?.Invoke(this, EventArgs.Empty);
        BuildModulesMenu();

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

    public ShellViewModel ViewModel { get; }

    public event EventHandler? SetupRerunRequested;

    private void BuildModulesMenu()
    {
        foreach (var feature in ViewModel.Features)
        {
            var item = new MenuFlyoutItem { Text = feature.DisplayName };
            var id = feature.Id;
            item.Click += (_, _) => OpenModule(id);
            ToolsMenu.Items.Add(item);
        }
    }

    private void OpenModule(string id)
    {
        var feature = ViewModel.Features.FirstOrDefault(f => f.Id == id);
        if (feature is null || !_modules.CanCreate(id))
        {
            _notifications.Post(NotificationSeverity.Warning, "Not available", "This module is not implemented in this version.");
            return;
        }

        // A feature that needs a missing tool explains it instead of failing later.
        var missing = feature.RequiredTools
            .Select(_tools.Get)
            .Where(t => !t.IsUsable)
            .Select(t => t.Definition.ShortName)
            .ToList();
        if (missing.Count > 0)
        {
            _notifications.Post(NotificationSeverity.Warning, $"{feature.DisplayName} needs {string.Join(", ", missing)}",
                "Open Tool Manager to locate the missing tool.");
            return;
        }

        if (_openWindows.TryGetValue(id, out var existing))
        {
            BringToFront(existing);
            return;
        }

        var window = CreateWindow(feature.DisplayName, id, _modules.Create(id), _modules.PreferredSize(id));
        _openWindows[id] = window;
        Workspace.Children.Add(window);
        BringToFront(window);
        UpdateOpenState();
    }

    private ModuleFrame CreateWindow(string title, string id, FrameworkElement content, Size preferred)
    {
        var bounds = new Size(Math.Max(Workspace.ActualWidth, 400), Math.Max(Workspace.ActualHeight, 300));
        var width = Math.Min(preferred.Width, bounds.Width - 16);
        var height = Math.Min(preferred.Height, bounds.Height - 16);

        var window = new ModuleFrame(title, id, content) { Width = width, Height = height };

        _cascade = (_cascade + 1) % 8;
        Canvas.SetLeft(window, Math.Max(0, Math.Min(24 + _cascade * 28, bounds.Width - width)));
        Canvas.SetTop(window, Math.Max(0, Math.Min(16 + _cascade * 28, bounds.Height - height)));

        window.CloseRequested += (_, _) => CloseWindow(id);
        var handle = window.DragHandle;
        handle.PointerPressed += (_, e) => BeginDrag(window, handle, e);
        handle.PointerMoved += (_, e) => Drag(e);
        handle.PointerReleased += (_, e) => EndDrag(handle, e);
        handle.PointerCanceled += (_, e) => EndDrag(handle, e);
        window.PointerPressed += (_, _) => BringToFront(window);
        return window;
    }

    private void CloseWindow(string id)
    {
        if (_openWindows.Remove(id, out var window))
        {
            Workspace.Children.Remove(window);
            window.Detach();
        }

        UpdateOpenState();
    }

    private void BringToFront(ModuleFrame window)
    {
        var top = Workspace.Children.Max(c => Canvas.GetZIndex(c));
        Canvas.SetZIndex(window, top + 1);
    }

    private void UpdateOpenState()
    {
        EmptyHint.Visibility = _openWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.SetOpenWindows(_openWindows.Keys.ToHashSet());
    }

    private void BeginDrag(ModuleFrame window, UIElement header, PointerRoutedEventArgs e)
    {
        BringToFront(window);
        _dragging = window;
        var point = e.GetCurrentPoint(Workspace).Position;
        _dragOffset = new Point(point.X - Canvas.GetLeft(window), point.Y - Canvas.GetTop(window));
        header.CapturePointer(e.Pointer);
    }

    private void Drag(PointerRoutedEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Workspace).Position;
        var maxLeft = Math.Max(0, Workspace.ActualWidth - _dragging.ActualWidth);
        var maxTop = Math.Max(0, Workspace.ActualHeight - _dragging.ActualHeight);
        Canvas.SetLeft(_dragging, Math.Clamp(point.X - _dragOffset.X, 0, maxLeft));
        Canvas.SetTop(_dragging, Math.Clamp(point.Y - _dragOffset.Y, 0, maxTop));
    }

    private void EndDrag(UIElement header, PointerRoutedEventArgs e)
    {
        _dragging = null;
        header.ReleasePointerCapture(e.Pointer);
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep module windows reachable when the main window shrinks.
        foreach (var child in Workspace.Children.OfType<ModuleFrame>())
        {
            child.MaxWidth = Math.Max(300, e.NewSize.Width);
            child.MaxHeight = Math.Max(240, e.NewSize.Height);
            Canvas.SetLeft(child, Math.Clamp(Canvas.GetLeft(child), 0, Math.Max(0, e.NewSize.Width - Math.Min(child.Width, child.MaxWidth))));
            Canvas.SetTop(child, Math.Clamp(Canvas.GetTop(child), 0, Math.Max(0, e.NewSize.Height - Math.Min(child.Height, child.MaxHeight))));
        }
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
        while (NotificationHost.Children.Count > 4)
        {
            NotificationHost.Children.RemoveAt(0);
        }

        // Errors stay until dismissed; everything else fades out on its own.
        if (notification.Severity != NotificationSeverity.Error)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(8);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => NotificationHost.Children.Remove(bar);
            timer.Start();
        }
    }

    private void OnOpenDeviceCenter(object sender, RoutedEventArgs e) => ViewModel.RequestOpen(ModuleIds.DeviceCenter);

    private void OnRerunSetup(object sender, RoutedEventArgs e) => ViewModel.RequestSetupRerun();

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private void OnCloseAll(object sender, RoutedEventArgs e)
    {
        foreach (var id in _openWindows.Keys.ToList())
        {
            CloseWindow(id);
        }
    }

    private void OnThemeDark(object sender, RoutedEventArgs e) => _ = ViewModel.SetThemeAsync(AppTheme.Dark);

    private void OnThemeLight(object sender, RoutedEventArgs e) => _ = ViewModel.SetThemeAsync(AppTheme.Light);

    private void OnThemeSystem(object sender, RoutedEventArgs e) => _ = ViewModel.SetThemeAsync(AppTheme.System);
}
