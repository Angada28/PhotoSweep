using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoSweep.Desktop.Behaviors;

/// <summary>
/// Attached behaviour for the compare window's actual-size mode, set on each pane's <see cref="ScrollViewer"/>:
/// <list type="bullet">
/// <item><b>Zoom:</b> scales the content so that zoom 1 is one image pixel per screen pixel. Bitmaps are 96 DPI, so on a
/// 150% screen one image pixel would otherwise cover 1.5 screen pixels; the scale divides that out.</item>
/// <item><b>Pan:</b> <see cref="PanXProperty"/>/<see cref="PanYProperty"/> are the centre of the view as a fraction of
/// the content, bound two-way to the view-model. Both panes bind the same values, so scrolling or dragging one moves
/// the other to the same spot in its picture.</item>
/// <item><b>Mouse:</b> drag to pan; Ctrl+wheel runs the zoom commands (the view-model keeps zoom within limits).</item>
/// </list>
/// Only view mechanics live here; zoom, pan and their limits are view-model state.
/// </summary>
public static class SyncedZoomPan
{
    // Fractions closer than this count as equal, so two panes echoing each other's rounding settle instead of looping.
    private const double Tolerance = 0.0005;

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.RegisterAttached(
        "Zoom", typeof(double), typeof(SyncedZoomPan), new PropertyMetadata(1.0, OnZoomChanged));

    public static readonly DependencyProperty PanXProperty = DependencyProperty.RegisterAttached(
        "PanX", typeof(double), typeof(SyncedZoomPan),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPanChanged));

    public static readonly DependencyProperty PanYProperty = DependencyProperty.RegisterAttached(
        "PanY", typeof(double), typeof(SyncedZoomPan),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPanChanged));

    public static readonly DependencyProperty ZoomInCommandProperty = DependencyProperty.RegisterAttached(
        "ZoomInCommand", typeof(ICommand), typeof(SyncedZoomPan), new PropertyMetadata(null, OnHookedPropertyChanged));

    public static readonly DependencyProperty ZoomOutCommandProperty = DependencyProperty.RegisterAttached(
        "ZoomOutCommand", typeof(ICommand), typeof(SyncedZoomPan), new PropertyMetadata(null, OnHookedPropertyChanged));

    // While dragging: where the mouse went down and the scroll offsets then.
    private static readonly DependencyProperty DragProperty = DependencyProperty.RegisterAttached(
        "Drag", typeof(DragStart), typeof(SyncedZoomPan));

    public static double GetZoom(DependencyObject d) => (double)d.GetValue(ZoomProperty);

    public static void SetZoom(DependencyObject d, double value) => d.SetValue(ZoomProperty, value);

    public static double GetPanX(DependencyObject d) => (double)d.GetValue(PanXProperty);

    public static void SetPanX(DependencyObject d, double value) => d.SetValue(PanXProperty, value);

    public static double GetPanY(DependencyObject d) => (double)d.GetValue(PanYProperty);

    public static void SetPanY(DependencyObject d, double value) => d.SetValue(PanYProperty, value);

    public static ICommand? GetZoomInCommand(DependencyObject d) => (ICommand?)d.GetValue(ZoomInCommandProperty);

    public static void SetZoomInCommand(DependencyObject d, ICommand? value) => d.SetValue(ZoomInCommandProperty, value);

    public static ICommand? GetZoomOutCommand(DependencyObject d) => (ICommand?)d.GetValue(ZoomOutCommandProperty);

    public static void SetZoomOutCommand(DependencyObject d, ICommand? value) => d.SetValue(ZoomOutCommandProperty, value);

    private static void OnHookedPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Hook(d);

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (Hook(d) is { } viewer)
            ApplyZoom(viewer);
    }

    private static void OnPanChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Moved by the other pane (through the view-model): follow, unless this pane is already there.
        if (Hook(d) is { } viewer && Centre(viewer) is { } now
            && (Math.Abs(now.X - GetPanX(viewer)) > Tolerance || Math.Abs(now.Y - GetPanY(viewer)) > Tolerance))
        {
            ApplyPan(viewer);
        }
    }

    private static ScrollViewer? Hook(DependencyObject d)
    {
        if (d is not ScrollViewer viewer)
            return null;

        // Unhook first so the handlers are never attached twice.
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.ScrollChanged += OnScrollChanged;
        viewer.Loaded -= OnLoaded;
        viewer.Loaded += OnLoaded;
        viewer.PreviewMouseWheel -= OnMouseWheel;
        viewer.PreviewMouseWheel += OnMouseWheel;
        viewer.PreviewMouseLeftButtonDown -= OnMouseDown;
        viewer.PreviewMouseLeftButtonDown += OnMouseDown;
        viewer.PreviewMouseMove -= OnMouseMove;
        viewer.PreviewMouseMove += OnMouseMove;
        viewer.PreviewMouseLeftButtonUp -= OnMouseUp;
        viewer.PreviewMouseLeftButtonUp += OnMouseUp;
        viewer.LostMouseCapture -= OnLostCapture;
        viewer.LostMouseCapture += OnLostCapture;
        return viewer;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => ApplyZoom((ScrollViewer)sender);

    private static void ApplyZoom(ScrollViewer viewer)
    {
        if (viewer.Content is not FrameworkElement content)
            return;

        var dpi = VisualTreeHelper.GetDpi(viewer);
        var zoom = GetZoom(viewer);
        var (x, y) = (zoom / dpi.DpiScaleX, zoom / dpi.DpiScaleY);
        // Only when it changes: a new transform means a new layout pass, and this also runs after every resize.
        if (content.LayoutTransform is not ScaleTransform current || current.ScaleX != x || current.ScaleY != y)
            content.LayoutTransform = new ScaleTransform(x, y);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0 || e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            // Zoomed, resized or a new picture: keep the same spot in the middle. A resize may also mean the window moved to
            // a screen with other scaling (ScrollViewer has no DpiChanged event), so the scale is checked again.
            ApplyZoom(viewer);
            ApplyPan(viewer);
            return;
        }

        if (Centre(viewer) is not { } centre)
            return;

        // Scrolled here; tell the view-model, which moves the other pane. Only along an axis that can scroll, or a
        // picture that fits would reset the other pane to the middle.
        if (viewer.ScrollableWidth > 0 && Math.Abs(centre.X - GetPanX(viewer)) > Tolerance)
            viewer.SetCurrentValue(PanXProperty, centre.X);
        if (viewer.ScrollableHeight > 0 && Math.Abs(centre.Y - GetPanY(viewer)) > Tolerance)
            viewer.SetCurrentValue(PanYProperty, centre.Y);
    }

    /// <summary>The middle of the view as a fraction of the content, or null before there's any content.</summary>
    private static Point? Centre(ScrollViewer viewer) =>
        viewer.ExtentWidth > 0 && viewer.ExtentHeight > 0
            ? new Point(
                (viewer.HorizontalOffset + viewer.ViewportWidth / 2) / viewer.ExtentWidth,
                (viewer.VerticalOffset + viewer.ViewportHeight / 2) / viewer.ExtentHeight)
            : null;

    private static void ApplyPan(ScrollViewer viewer)
    {
        viewer.ScrollToHorizontalOffset(GetPanX(viewer) * viewer.ExtentWidth - viewer.ViewportWidth / 2);
        viewer.ScrollToVerticalOffset(GetPanY(viewer) * viewer.ExtentHeight - viewer.ViewportHeight / 2);
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return; // a plain wheel scrolls as usual

        var viewer = (DependencyObject)sender;
        var command = e.Delta > 0 ? GetZoomInCommand(viewer) : GetZoomOutCommand(viewer);
        if (command?.CanExecute(null) == true)
            command.Execute(null);
        e.Handled = true;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (IsOnScrollBar(e.OriginalSource as DependencyObject))
            return; // let the scrollbars work

        viewer.SetValue(DragProperty, new DragStart(e.GetPosition(viewer), viewer.HorizontalOffset, viewer.VerticalOffset));
        viewer.CaptureMouse();
        viewer.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (viewer.GetValue(DragProperty) is not DragStart start)
            return;

        var moved = e.GetPosition(viewer) - start.Mouse;
        viewer.ScrollToHorizontalOffset(start.X - moved.X);
        viewer.ScrollToVerticalOffset(start.Y - moved.Y);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e) => ((ScrollViewer)sender).ReleaseMouseCapture();

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        viewer.ClearValue(DragProperty);
        viewer.ClearValue(FrameworkElement.CursorProperty);
    }

    private static bool IsOnScrollBar(DependencyObject? element)
    {
        for (var d = element; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ScrollBar)
                return true;
        }

        return false;
    }

    private sealed record DragStart(Point Mouse, double X, double Y);
}
