using System.Windows;
using System.Windows.Media;
using PhotoSweep.Presentation;

namespace PhotoSweep.Desktop.Behaviors;

/// <summary>
/// Attached behaviour: <c>&lt;Grid behaviors:PaneSize.Target="{Binding}" /&gt;</c> tells the compare view-model how big
/// its preview pane is in screen pixels (size × the monitor's scaling), so previews are decoded to fit it.
/// </summary>
public static class PaneSize
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(CompareViewModel), typeof(PaneSize), new PropertyMetadata(null, OnTargetChanged));

    public static CompareViewModel? GetTarget(DependencyObject d) => (CompareViewModel?)d.GetValue(TargetProperty);

    public static void SetTarget(DependencyObject d, CompareViewModel? value) => d.SetValue(TargetProperty, value);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        // Unhook first so the handler is never attached twice.
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Report(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Report((FrameworkElement)sender);

    private static void Report(FrameworkElement element)
    {
        if (GetTarget(element) is not { } target || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(element);
        target.SetPaneSize((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
    }
}
