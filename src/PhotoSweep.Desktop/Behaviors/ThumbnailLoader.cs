using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Desktop.Services;
using PhotoSweep.Presentation;

namespace PhotoSweep.Desktop.Behaviors;

/// <summary>
/// Attached behaviour: <c>&lt;Image behaviors:ThumbnailLoader.Photo="{Binding}" behaviors:ThumbnailLoader.Size="160" /&gt;</c>
/// fills the image with the photo's thumbnail, loaded in the background by <see cref="ThumbnailCache"/>.
/// </summary>
/// <remarks>
/// The list recycles rows: when one scrolls off screen WPF unloads it and later reuses it for another photo. So:
/// <list type="bullet">
/// <item>Loading starts when the image is on screen (Loaded, or its photo changes while loaded) and is cancelled
/// when it leaves (Unloaded, or its photo changes). A load waiting for a decode slot is then skipped.</item>
/// <item>A result that arrives after the image moved on to another photo is dropped, never shown on the wrong row.</item>
/// <item>Each time, the view-model re-checks whether the file is online-only (<see cref="PhotoViewModel.RefreshAvailability"/>).
/// If it is, nothing is loaded and the tile shows the cloud placeholder.</item>
/// </list>
/// Deciding what to show lives in the view-model (tested); this class only drives WPF's image and lifetime events.
/// </remarks>
public static class ThumbnailLoader
{
    public static readonly DependencyProperty PhotoProperty = DependencyProperty.RegisterAttached(
        "Photo", typeof(PhotoViewModel), typeof(ThumbnailLoader), new PropertyMetadata(null, OnPhotoChanged));

    /// <summary>The displayed size in device-independent pixels; the decode is this × the monitor's scaling.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(ThumbnailLoader), new PropertyMetadata(160.0));

    // Per image: the running load's cancellation, and which photo the current Source belongs to.
    private static readonly DependencyProperty LoadProperty = DependencyProperty.RegisterAttached(
        "Load", typeof(CancellationTokenSource), typeof(ThumbnailLoader));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(PhotoViewModel), typeof(ThumbnailLoader));

    public static PhotoViewModel? GetPhoto(DependencyObject d) => (PhotoViewModel?)d.GetValue(PhotoProperty);

    public static void SetPhoto(DependencyObject d, PhotoViewModel? value) => d.SetValue(PhotoProperty, value);

    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);

    public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);

    private static void OnPhotoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        // Unhook first so the handlers are never attached twice.
        image.Loaded -= OnLoaded;
        image.Unloaded -= OnUnloaded;
        image.Loaded += OnLoaded;
        image.Unloaded += OnUnloaded;

        Show(image);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Show((Image)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Cancel((Image)sender);

    private static void Show(Image image)
    {
        Cancel(image);
        var photo = GetPhoto(image);
        if (!ReferenceEquals(image.GetValue(ShownProperty), photo))
        {
            image.Source = null; // never show the previous photo's thumbnail on a recycled row
            image.ClearValue(ShownProperty);
        }

        if (photo is null || !image.IsLoaded)
            return; // loaded when it comes on screen

        if (!photo.RefreshAvailability())
        {
            image.Source = null; // online-only or gone: the tile shows a placeholder and the file is never opened
            image.ClearValue(ShownProperty);
            return;
        }

        if (image.Source is not null)
        {
            photo.PreviewLoaded(ThumbnailStatus.Ok); // still showing this photo's thumbnail
            return;
        }

        var cts = new CancellationTokenSource();
        image.SetValue(LoadProperty, cts);
        _ = LoadAsync(image, photo, cts);
    }

    private static void Cancel(Image image)
    {
        if (image.GetValue(LoadProperty) is CancellationTokenSource cts)
        {
            cts.Cancel();
            image.ClearValue(LoadProperty);
        }
    }

    /// <summary>Runs on the UI thread; only the decode inside <see cref="ThumbnailCache"/> goes to the thread pool.</summary>
    private static async Task LoadAsync(Image image, PhotoViewModel photo, CancellationTokenSource cts)
    {
        try
        {
            var pixels = (int)Math.Ceiling(GetSize(image) * VisualTreeHelper.GetDpi(image).DpiScaleX);
            var (bitmap, status) = await ThumbnailCache.Shared.GetAsync(photo.File.Path, photo.File.LastWriteUtc, pixels, cts.Token);

            if (cts.IsCancellationRequested || !ReferenceEquals(GetPhoto(image), photo))
            {
                ThumbnailStats.TooLate(); // the row was scrolled away or recycled meanwhile: drop the result
                return;
            }

            image.Source = bitmap;
            image.SetValue(ShownProperty, bitmap is null ? null : photo);
            photo.PreviewLoaded(status);
        }
        catch (OperationCanceledException)
        {
            // Skipped before decoding because the row left the screen.
        }
        catch (Exception)
        {
            // E.g. out of memory for a huge image: a placeholder, not a crash (this task is never awaited).
            if (!cts.IsCancellationRequested && ReferenceEquals(GetPhoto(image), photo))
                photo.PreviewLoaded(ThumbnailStatus.CannotDecode);
        }
        finally
        {
            if (ReferenceEquals(image.GetValue(LoadProperty), cts))
                image.ClearValue(LoadProperty);
            cts.Dispose();
        }
    }
}
