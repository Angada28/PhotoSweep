using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhotoSweep.Core.Imaging;

/// <summary>
/// Facts from an image's header that help decide which copy to keep. <see cref="Width"/> and <see cref="Height"/>
/// are as displayed, i.e. with the EXIF orientation applied, so a sideways-stored portrait reports as portrait.
/// </summary>
public sealed record ImageDetails(int Width, int Height)
{
    public string? CameraMake { get; init; }

    public string? CameraModel { get; init; }

    /// <summary>EXIF DateTimeOriginal. Camera-local time with no time zone (EXIF 2.x doesn't record one).</summary>
    public DateTime? DateTaken { get; init; }

    /// <summary>Frames in the file: more than 1 for an animated GIF, PNG or WebP (or a multi-page TIFF).</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>
    /// Animated images are matched only by their bytes, never by appearance: a burst's cover GIF hashes like one of the
    /// burst's stills (its first frame), but it's a different thing to keep.
    /// </summary>
    public bool IsAnimated => FrameCount > 1;

    public long PixelCount => (long)Width * Height;

    /// <summary>Exports and messaging apps usually strip these tags; originals straight from a camera keep them.</summary>
    public bool HasCameraData => CameraMake is not null || CameraModel is not null || DateTaken is not null;

    /// <summary>
    /// Reads the header only (<see cref="Image.Identify(Stream)"/>), without decoding pixels, so it's cheap.
    /// Throws the same ImageSharp exceptions as decoding for unsupported or corrupt files.
    /// </summary>
    public static ImageDetails Read(Stream stream)
    {
        var info = Image.Identify(stream);
        var exif = info.Metadata.ExifProfile;

        var (width, height) = (info.Width, info.Height);
        // Orientations 5–8 are the ones with a 90° turn, so the displayed image has width and height swapped.
        if (exif is not null && exif.TryGetValue(ExifTag.Orientation, out var orientation) && orientation.Value is >= 5 and <= 8)
            (width, height) = (height, width);

        return new ImageDetails(width, height)
        {
            CameraMake = Text(exif, ExifTag.Make),
            CameraModel = Text(exif, ExifTag.Model),
            DateTaken = Date(exif),
            // Identify reads every frame's header but no pixels, so counting frames costs nothing extra.
            FrameCount = Math.Max(1, info.FrameMetadataCollection.Count),
        };
    }

    private static string? Text(ExifProfile? exif, ExifTag<string> tag)
    {
        if (exif is null || !exif.TryGetValue(tag, out var value))
            return null;

        // Cameras often pad these fixed-size fields with spaces or NULs.
        var text = value.Value?.Trim().TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static DateTime? Date(ExifProfile? exif)
    {
        // EXIF dates look like "2019:07:14 16:02:31". Unset ones are often "0000:00:00 00:00:00", which fails to parse.
        return Text(exif, ExifTag.DateTimeOriginal) is { } text
            && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
