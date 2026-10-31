using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MyAndroidPhone.Desktop.Views;

public enum FrameState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>A pointer-sensitive strip on a window edge or corner that shows the matching resize cursor.</summary>
internal sealed class ResizeHandle : Border
{
    public ResizeHandle(InputSystemCursorShape shape, bool left, bool top, bool right, bool bottom)
    {
        ProtectedCursor = InputSystemCursor.Create(shape);
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public bool Left { get; }

    public bool Top { get; }

    public bool Right { get; }

    public bool Bottom { get; }
}

/// <summary>
/// A floating window on the desktop canvas: drag by the title bar, resize from any edge or corner, minimize to the Dock,
/// maximize to fill the desktop (double-click the title bar) and close. Placement is stored in Canvas coordinates.
/// </summary>
public sealed partial class ModuleFrame : UserControl
{
    private const double EdgeSize = 6;
    private const double CornerSize = 14;

    private readonly List<ResizeHandle> _handles = new();
    private Rect _restoreBounds;
    private FrameState _stateBeforeMinimize = FrameState.Normal;

    private ResizeHandle? _resizing;
    private Point _resizeStartPointer;
    private Rect _resizeStartBounds;

    public ModuleFrame(string title, string featureId, UIElement content)
    {
        InitializeComponent();
        FeatureId = featureId;
        Title = title;
        TitleText.Text = title;
        MinWidth = 380;
        MinHeight = 260;
        AutomationProperties.SetName(CloseButton, $"Close {title}");
        AutomationProperties.SetName(MinimizeButton, $"Minimize {title}");
        AutomationProperties.SetName(MaximizeButton, $"Maximize {title}");
        Presenter.Content = content;
        TitleBar.DoubleTapped += (_, _) => MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);

        Lights.PointerEntered += (_, _) => SetGlyphs(true);
        Lights.PointerExited += (_, _) => SetGlyphs(false);

        AddHandle(InputSystemCursorShape.SizeWestEast, true, false, false, false, HorizontalAlignment.Left, VerticalAlignment.Stretch, EdgeSize, double.NaN, new Thickness(0, CornerSize, 0, CornerSize));
        AddHandle(InputSystemCursorShape.SizeWestEast, false, false, true, false, HorizontalAlignment.Right, VerticalAlignment.Stretch, EdgeSize, double.NaN, new Thickness(0, CornerSize, 0, CornerSize));
        AddHandle(InputSystemCursorShape.SizeNorthSouth, false, true, false, false, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, EdgeSize, new Thickness(CornerSize, 0, CornerSize, 0));
        AddHandle(InputSystemCursorShape.SizeNorthSouth, false, false, false, true, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, EdgeSize, new Thickness(CornerSize, 0, CornerSize, 0));
        AddHandle(InputSystemCursorShape.SizeNorthwestSoutheast, true, true, false, false, HorizontalAlignment.Left, VerticalAlignment.Top, CornerSize, CornerSize, new Thickness(0));
        AddHandle(InputSystemCursorShape.SizeNortheastSouthwest, false, true, true, false, HorizontalAlignment.Right, VerticalAlignment.Top, CornerSize, CornerSize, new Thickness(0));
        AddHandle(InputSystemCursorShape.SizeNortheastSouthwest, true, false, false, true, HorizontalAlignment.Left, VerticalAlignment.Bottom, CornerSize, CornerSize, new Thickness(0));
        AddHandle(InputSystemCursorShape.SizeNorthwestSoutheast, false, false, true, true, HorizontalAlignment.Right, VerticalAlignment.Bottom, CornerSize, CornerSize, new Thickness(0));
    }

    public string FeatureId { get; }

    public string Title { get; }

    public FrameState State { get; private set; } = FrameState.Normal;

    /// <summary>The draggable title bar; the shell attaches pointer handlers to it.</summary>
    public UIElement DragHandle => TitleBar;

    public event EventHandler? CloseRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MaximizeToggleRequested;

    /// <summary>Removes the hosted module so it is unloaded (and its view model disposed).</summary>
    public void Detach() => Presenter.Content = null;

    // ----- state changes (the shell passes the desktop size) -----

    public void Maximize(Size area)
    {
        if (State == FrameState.Maximized)
        {
            FitMaximized(area);
            return;
        }

        _restoreBounds = new Rect(Canvas.GetLeft(this), Canvas.GetTop(this), ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        State = FrameState.Maximized;
        Surface.CornerRadius = new CornerRadius(0);
        SetHandlesVisible(false);
        MaximizeButton.SetValue(ToolTipService.ToolTipProperty, "Restore");
        AutomationProperties.SetName(MaximizeButton, $"Restore {Title}");
        FitMaximized(area);
    }

    public void FitMaximized(Size area)
    {
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Canvas.SetLeft(this, 0);
        Canvas.SetTop(this, 0);
        Width = Math.Max(MinWidth, area.Width);
        Height = Math.Max(MinHeight, area.Height);
    }

    public void Restore(Size area)
    {
        if (State != FrameState.Maximized)
        {
            return;
        }

        State = FrameState.Normal;
        Surface.CornerRadius = new CornerRadius(12);
        SetHandlesVisible(true);
        MaximizeButton.SetValue(ToolTipService.ToolTipProperty, "Maximize");
        AutomationProperties.SetName(MaximizeButton, $"Maximize {Title}");
        var width = Math.Clamp(_restoreBounds.Width, MinWidth, Math.Max(MinWidth, area.Width));
        var height = Math.Clamp(_restoreBounds.Height, MinHeight, Math.Max(MinHeight, area.Height));
        Width = width;
        Height = height;
        Canvas.SetLeft(this, Math.Clamp(_restoreBounds.X, 0, Math.Max(0, area.Width - width)));
        Canvas.SetTop(this, Math.Clamp(_restoreBounds.Y, 0, Math.Max(0, area.Height - height)));
    }

    public void Minimize()
    {
        if (State == FrameState.Minimized)
        {
            return;
        }

        _stateBeforeMinimize = State;
        State = FrameState.Minimized;
        Visibility = Visibility.Collapsed;
    }

    public void Unminimize()
    {
        if (State != FrameState.Minimized)
        {
            return;
        }

        State = _stateBeforeMinimize;
        Visibility = Visibility.Visible;
    }

    // ----- resizing -----

    private void AddHandle(InputSystemCursorShape shape, bool left, bool top, bool right, bool bottom,
        HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height, Thickness margin)
    {
        var handle = new ResizeHandle(shape, left, top, right, bottom)
        {
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin
        };
        if (!double.IsNaN(width))
        {
            handle.Width = width;
        }

        if (!double.IsNaN(height))
        {
            handle.Height = height;
        }

        handle.PointerPressed += OnResizePressed;
        handle.PointerMoved += OnResizeMoved;
        handle.PointerReleased += OnResizeReleased;
        handle.PointerCanceled += OnResizeReleased;
        _handles.Add(handle);
        Root.Children.Add(handle);
    }

    private void SetHandlesVisible(bool visible)
    {
        foreach (var handle in _handles)
        {
            handle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnResizePressed(object sender, PointerRoutedEventArgs e)
    {
        if (Parent is not Canvas canvas || sender is not ResizeHandle handle)
        {
            return;
        }

        _resizing = handle;
        _resizeStartPointer = e.GetCurrentPoint(canvas).Position;
        _resizeStartBounds = new Rect(Canvas.GetLeft(this), Canvas.GetTop(this), ActualWidth, ActualHeight);
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing is null || Parent is not Canvas canvas)
        {
            return;
        }

        var point = e.GetCurrentPoint(canvas).Position;
        var dx = point.X - _resizeStartPointer.X;
        var dy = point.Y - _resizeStartPointer.Y;
        var start = _resizeStartBounds;
        var canvasWidth = canvas.ActualWidth;
        var canvasHeight = canvas.ActualHeight;

        var left = start.X;
        var top = start.Y;
        var width = start.Width;
        var height = start.Height;

        if (_resizing.Right)
        {
            width = Math.Clamp(start.Width + dx, MinWidth, Math.Max(MinWidth, canvasWidth - start.X));
        }

        if (_resizing.Bottom)
        {
            height = Math.Clamp(start.Height + dy, MinHeight, Math.Max(MinHeight, canvasHeight - start.Y));
        }

        if (_resizing.Left)
        {
            // The right edge stays put; the left edge cannot pass the canvas edge or shrink below the minimum width.
            var right = start.X + start.Width;
            var newLeft = Math.Clamp(start.X + dx, 0, right - MinWidth);
            left = newLeft;
            width = right - newLeft;
        }

        if (_resizing.Top)
        {
            var bottom = start.Y + start.Height;
            var newTop = Math.Clamp(start.Y + dy, 0, bottom - MinHeight);
            top = newTop;
            height = bottom - newTop;
        }

        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Width = width;
        Height = height;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        e.Handled = true;
    }

    private void OnResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing is not null)
        {
            _resizing.ReleasePointerCapture(e.Pointer);
            _resizing = null;
        }
    }

    // ----- chrome -----

    private void SetGlyphs(bool visible)
    {
        var opacity = visible ? 1.0 : 0.0;
        CloseGlyph.Opacity = opacity;
        MinimizeGlyph.Opacity = opacity;
        MaximizeGlyph.Opacity = opacity;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnMaximizeClicked(object sender, RoutedEventArgs e) => MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
}
