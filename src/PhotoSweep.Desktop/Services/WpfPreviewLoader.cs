using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>
/// Compare-window previews: ImageSharp via its own <see cref="ThumbnailCache"/>, separate from the results page's so
/// big previews don't push out the thumbnails. Fit-size previews (and prefetches) are cached; actual-size ones aren't.
/// </summary>
public sealed class WpfPreviewLoader : IPreviewLoader
{
    // About 20 previews at 1600×1000; two decodes at once is enough for two panes plus a few prefetches.
    private readonly ThumbnailCache _cache = new(256L * 1024 * 1024, maxConcurrentDecodes: 2);

    public async Task<PreviewImage> LoadAsync(string path, DateTime lastWriteUtc, PreviewSize size, CancellationToken ct)
    {
        var (width, height) = size.IsActualSize ? (int.MaxValue, int.MaxValue) : (size.Width, size.Height);
        var (image, status) = await _cache.GetAsync(path, lastWriteUtc, width, height, keep: !size.IsActualSize, ct);
        return new PreviewImage(status, image, image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
    }
}
