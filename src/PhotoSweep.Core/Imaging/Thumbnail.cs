using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoSweep.Core.Imaging;

public enum ThumbnailStatus
{
    Ok,

    /// <summary>The file is a cloud placeholder, so it wasn't opened (CLAUDE.md rule 6).</summary>
    OnlineOnly,

    /// <summary>Missing, locked or access denied.</summary>
    Unavailable,

    /// <summary>ImageSharp can't decode it (e.g. HEIC) or it's corrupt.</summary>
    CannotDecode,
}

/// <param name="Bgra">Width × Height pixels, 4 bytes each (B, G, R, A), rows top to bottom. Null unless <see cref="Status"/> is Ok.</param>
public sealed record ThumbnailResult(ThumbnailStatus Status, int Width = 0, int Height = 0, byte[]? Bgra = null);

/// <summary>
/// Small, upright previews for the results screen, decoded with ImageSharp. Returns raw pixels rather than a
/// WPF bitmap so Core stays UI-free; the Desktop project wraps them in a <c>BitmapSource</c>.
/// </summary>
public static class Thumbnail
{
    /// <summary>
    /// Decodes <paramref name="path"/> to fit within <paramref name="maxSize"/>×<paramref name="maxSize"/> pixels,
    /// with the EXIF orientation applied. Never enlarges. Never throws for a bad file; only for cancellation, which
    /// ImageSharp also checks part-way through decoding, so a thumbnail nobody wants any more stops early.
    /// </summary>
    /// <remarks>
    /// The online-only check is repeated here, right before the file is opened, even though the UI checks when a row
    /// is shown: thumbnails wait in a queue, and OneDrive can free up a file in between. This is the last line of
    /// defence, so it sits where the file is actually opened.
    /// </remarks>
    public static async Task<ThumbnailResult> LoadAsync(string path, int maxSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);
        ct.ThrowIfCancellationRequested();

        switch (CloudFileAttributes.Check(path))
        {
            case FileAvailability.OnlineOnly:
                return new ThumbnailResult(ThumbnailStatus.OnlineOnly);
            case FileAvailability.Unavailable:
                return new ThumbnailResult(ThumbnailStatus.Unavailable);
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            // TargetSize lets the JPEG decoder scale down while decoding (it skips most of the work for an 8× smaller
            // image), so a 12 MP photo never exists at full size in memory. Other formats decode, then shrink.
            // But TargetSize also scales UP, so it's only set for images bigger than the box; the header read is cheap.
            var stored = await Image.IdentifyAsync(stream, ct);
            stream.Position = 0;
            var options = stored.Width > maxSize || stored.Height > maxSize
                ? new DecoderOptions { TargetSize = new Size(maxSize, maxSize) }
                : new DecoderOptions();
            using var image = await Image.LoadAsync<Bgra32>(options, stream, ct);
            image.Mutate(x =>
            {
                x.AutoOrient(); // WPF ignores EXIF orientation, so the pixels are turned upright here
                if (image.Width > maxSize || image.Height > maxSize)
                    x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxSize, maxSize) });
            });

            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return new ThumbnailResult(ThumbnailStatus.Ok, image.Width, image.Height, pixels);
        }
        catch (ImageFormatException)
        {
            return new ThumbnailResult(ThumbnailStatus.CannotDecode);
        }
        catch (NotSupportedException)
        {
            return new ThumbnailResult(ThumbnailStatus.CannotDecode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ThumbnailResult(ThumbnailStatus.Unavailable);
        }
    }
}
