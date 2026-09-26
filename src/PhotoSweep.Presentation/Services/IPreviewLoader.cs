using PhotoSweep.Core.Imaging;

namespace PhotoSweep.Presentation.Services;

/// <summary>A box to fit a preview into, in screen pixels. <see cref="ActualSize"/> means no box: every pixel of the photo.</summary>
public readonly record struct PreviewSize(int Width, int Height)
{
    public static readonly PreviewSize ActualSize = new(0, 0);

    public bool IsActualSize => Width <= 0 || Height <= 0;
}

/// <param name="Image">The decoded picture, for the view to show. <c>object</c> because Presentation can't name WPF's
/// <c>ImageSource</c>; the Desktop loader returns a frozen <c>BitmapSource</c>. Null unless <see cref="Status"/> is Ok.</param>
/// <param name="Width">Pixel width of <paramref name="Image"/>.</param>
/// <param name="Height">Pixel height of <paramref name="Image"/>.</param>
public sealed record PreviewImage(ThumbnailStatus Status, object? Image = null, int Width = 0, int Height = 0);

/// <summary>
/// Decodes bigger previews for the compare window, off the UI thread. Implemented in Desktop (ImageSharp via
/// <see cref="Thumbnail"/>, with a cache); faked in tests so loading, cancelling and prefetching can be checked.
/// </summary>
public interface IPreviewLoader
{
    /// <summary>
    /// The photo fitted into <paramref name="size"/>, upright. Throws <see cref="OperationCanceledException"/> once
    /// <paramref name="ct"/> is cancelled; otherwise reports problems as a status rather than throwing.
    /// </summary>
    Task<PreviewImage> LoadAsync(string path, DateTime lastWriteUtc, PreviewSize size, CancellationToken ct);
}
