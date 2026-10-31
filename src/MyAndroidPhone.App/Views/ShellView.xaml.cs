using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.Foundation;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>
/// The desktop of one instance: menu bar, Dock and floating windows that can be moved, resized, minimized, maximized and
/// closed. The code-behind only handles window chrome; what a module does lives in its view model.
/// </summary>
public sealed partial class ShellView : UserControl
{
    private readonly ModuleViewFactory _modules;
    private readonly IToolService _tools;
    private readonly INotificationService _notifications;
    private readonly ThemeService _theme;
    private readonly Dictionary<string, ModuleFrame> _openWindows = new();
    private int _cascade;

    private ModuleFrame? _dragging;
    private ModuleFrame? _active;
    private Point _dragOffset;

    public ShellView(
        ShellViewModel viewModel,
        ModuleViewFactory modules,
        IToolService tools,
        INotificationService notifications,
        ThemeService theme)
    {
        ViewModel = viewModel;
        _modules = modules;
        _tools = tools;
        _notifications = notifications;
        _theme = theme;
        InitializeComponent();

        ViewModel.OpenFeatureRequested += (_, id) => OpenModule(id);
        ViewModel.CloseInstanceRequested += (_, _) => SwitchInstanceRequested?.Invoke(this, EventArgs.Empty);
        BuildModulesMenu();
        RebuildWindowMenu();

        _notifications.Posted += OnNotificationPosted;
        foreach (var pending in _notifications.DrainPending())
        {
            ShowNotification(pending);
        }

        Unloaded += (_, _) =>
        {
            _notifications.Posted -= OnNotificationPosted;
            _theme.UseOverride(null);
            foreach (var id in _openWindows.Keys.ToList())
            {
                CloseWindow(id);
            }

            ViewModel.Dispose();
        };
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>The user wants the start screen.</summary>
    public event EventHandler? SwitchInstanceRequested;

    /// <summary>Binds this desktop to an instance: its theme, accent wallpaper and phone.</summary>
    public void Open(AndroidInstance instance)
    {
        _theme.UseOverride(instance.Theme);
        var accent = ColorHelper.Parse(instance.AccentColor);
        Wallpaper.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(120, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(30, accent.R, accent.G, accent.B), Offset = 1 }
            }
        };
        ViewModel.Initialize(instance);
    }

    // ----- menus -----

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

    private void RebuildWindowMenu()
    {
        WindowMenu.Items.Clear();

        var minimize = new MenuFlyoutItem { Text = "Minimize", IsEnabled = _active is { State: not FrameState.Minimized } };
        minimize.Click += (_, _) => MinimizeWindow(_active);
        var zoom = new MenuFlyoutItem { Text = _active?.State == FrameState.Maximized ? "Restore" : "Maximize", IsEnabled = _active is not null };
        zoom.Click += (_, _) => ToggleMaximize(_active);
        var close = new MenuFlyoutItem { Text = "Close window", IsEnabled = _active is not null };
        close.Click += (_, _) =>
        {
            if (_active is not null)
            {
                CloseWindow(_active.FeatureId);
            }
        };
        var closeAll = new MenuFlyoutItem { Text = "Close all windows", IsEnabled = _openWindows.Count > 0 };
        closeAll.Click += OnCloseAll;
        var restoreAll = new MenuFlyoutItem { Text = "Show all minimized windows", IsEnabled = _openWindows.Values.Any(w => w.State == FrameState.Minimized) };
        restoreAll.Click += (_, _) =>
        {
            foreach (var window in _openWindows.Values.Where(w => w.State == FrameState.Minimized).ToList())
            {
                window.Unminimize();
                BringToFront(window);
            }

            UpdateOpenState();
        };

        WindowMenu.Items.Add(minimize);
        WindowMenu.Items.Add(zoom);
        WindowMenu.Items.Add(close);
        WindowMenu.Items.Add(new MenuFlyoutSeparator());
        WindowMenu.Items.Add(restoreAll);
        WindowMenu.Items.Add(closeAll);

        if (_openWindows.Count > 0)
        {
            WindowMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var window in _openWindows.Values)
            {
                var entry = new MenuFlyoutItem { Text = window.Title + (window.State == FrameState.Minimized ? " (minimized)" : string.Empty) };
                var id = window.FeatureId;
                entry.Click += (_, _) => OpenModule(id);
                WindowMenu.Items.Add(entry);
            }
        }
    }

    // ----- windows -----

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
            existing.Unminimize();
            BringToFront(existing);
            UpdateOpenState();
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
        window.MinimizeRequested += (_, _) => MinimizeWindow(window);
        window.MaximizeToggleRequested += (_, _) => ToggleMaximize(window);
        var handle = window.DragHandle;
        handle.PointerPressed += (_, e) => BeginDrag(window, handle, e);
        handle.PointerMoved += (_, e) => Drag(e);
        handle.PointerReleased += (_, e) => EndDrag(handle, e);
        handle.PointerCanceled += (_, e) => EndDrag(handle, e);
        window.PointerPressed += (_, _) => BringToFront(window);
        return window;
    }

    private Size DesktopSize => new(Workspace.ActualWidth, Workspace.ActualHeight);

    private void CloseWindow(string id)
    {
        if (_openWindows.Remove(id, out var window))
        {
            Workspace.Children.Remove(window);
            window.Detach();
            if (_active == window)
            {
                _active = null;
                ActivateTopmost();
            }
        }

        UpdateOpenState();
    }

    private void MinimizeWindow(ModuleFrame? window)
    {
        if (window is null)
        {
            return;
        }

        window.Minimize();
        if (_active == window)
        {
            _active = null;
            ActivateTopmost();
        }

        UpdateOpenState();
    }

    private void ToggleMaximize(ModuleFrame? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.State == FrameState.Maximized)
        {
            window.Restore(DesktopSize);
        }
        else
        {
            window.Maximize(DesktopSize);
        }

        BringToFront(window);
        RebuildWindowMenu();
    }

    private void BringToFront(ModuleFrame window)
    {
        var top = Workspace.Children.Max(c => Canvas.GetZIndex(c));
        Canvas.SetZIndex(window, top + 1);
        _active = window;
    }

    private void ActivateTopmost()
    {
        var next = _openWindows.Values
            .Where(w => w.State != FrameState.Minimized)
            .OrderByDescending(w => Canvas.GetZIndex(w))
            .FirstOrDefault();
        _active = next;
    }

    private void UpdateOpenState()
    {
        EmptyHint.Visibility = _openWindows.Values.Any(w => w.State != FrameState.Minimized) ? Visibility.Collapsed : Visibility.Visible;
        ViewModel.SetOpenWindows(
            _openWindows.Keys.ToHashSet(),
            _openWindows.Where(p => p.Value.State == FrameState.Minimized).Select(p => p.Key).ToHashSet());
        RebuildWindowMenu();
    }

    private void BeginDrag(ModuleFrame window, UIElement header, PointerRoutedEventArgs e)
    {
        BringToFront(window);
        if (window.State == FrameState.Maximized)
        {
            return;
        }

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
        // Keep windows reachable when the main window shrinks; maximized windows follow the desktop size.
        foreach (var child in Workspace.Children.OfType<ModuleFrame>())
        {
            if (child.State == FrameState.Maximized)
            {
                child.FitMaximized(e.NewSize);
                continue;
            }

            child.MaxWidth = Math.Max(300, e.NewSize.Width);
            child.MaxHeight = Math.Max(240, e.NewSize.Height);
            Canvas.SetLeft(child, Math.Clamp(Canvas.GetLeft(child), 0, Math.Max(0, e.NewSize.Width - Math.Min(child.Width, child.MaxWidth))));
            Canvas.SetTop(child, Math.Clamp(Canvas.GetTop(child), 0, Math.Max(0, e.NewSize.Height - Math.Min(child.Height, child.MaxHeight))));
        }
    }

    // ----- notifications -----

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
        Canvas.SetZIndex(NotificationHost, 100);
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

    // ----- menu handlers -----

    private void OnSwitchInstance(object sender, RoutedEventArgs e) => ViewModel.RequestCloseInstance();

    private void OnReconnect(object sender, RoutedEventArgs e) => ViewModel.ReconnectCommand.Execute(null);

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private void OnCloseAll(object sender, RoutedEventArgs e)
    {
        foreach (var id in _openWindows.Keys.ToList())
        {
            CloseWindow(id);
        }
    }

    private void OnThemeDark(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Dark);

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Light);

    private void OnThemeSystem(object sender, RoutedEventArgs e) => SetTheme(AppTheme.System);

    private void SetTheme(AppTheme theme)
    {
        _theme.UseOverride(theme);
        _ = ViewModel.SetThemeAsync(theme);
    }
}
