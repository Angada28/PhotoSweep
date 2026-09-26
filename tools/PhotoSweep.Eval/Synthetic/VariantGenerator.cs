using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoSweep.Eval.Synthetic;

/// <summary>Edits that should NOT change what the photo looks like, so every variant should match its original.</summary>
public enum VariantKind
{
    /// <summary>Resized to 50%, JPEG quality 90.</summary>
    Half,

    /// <summary>Resized to 25%, JPEG quality 90.</summary>
    Quarter,

    /// <summary>Full size, JPEG quality 50.</summary>
    Quality50,

    /// <summary>Full size, JPEG quality 30.</summary>
    Quality30,

    /// <summary>Full size, lossless PNG.</summary>
    Png,

    /// <summary>Pixels stored rotated with EXIF Orientation 6, as a phone saves a portrait shot.</summary>
    Exif6,

    /// <summary>Resized to 50% and saved at JPEG quality 30, e.g. a messaging-app re-save.</summary>
    ResizedAndRecompressed,
}

public sealed record GeneratedVariant(VariantKind Kind, string Path);

/// <summary>Writes the <see cref="VariantKind"/>s of one original, decoding and encoding with ImageSharp only.</summary>
public static class VariantGenerator
{
    public static readonly IReadOnlyList<VariantKind> AllKinds = Enum.GetValues<VariantKind>();

    private static readonly JpegEncoder Q90 = new() { Quality = 90 };
    private static readonly JpegEncoder Q50 = new() { Quality = 50 };
    private static readonly JpegEncoder Q30 = new() { Quality = 30 };

    public static string Label(VariantKind kind) => kind switch
    {
        VariantKind.Half => "resized 50%",
        VariantKind.Quarter => "resized 25%",
        VariantKind.Quality50 => "JPEG q50",
        VariantKind.Quality30 => "JPEG q30",
        VariantKind.Png => "PNG",
        VariantKind.Exif6 => "EXIF-rotated (6)",
        VariantKind.ResizedAndRecompressed => "50% + JPEG q30",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Writes every variant of <paramref name="originalPath"/> into <paramref name="outputFolder"/> as
    /// <c>{stem}_{kind}.jpg/.png</c>. Decode errors propagate as ImageSharp exceptions, before anything is written.
    /// </summary>
    /// <remarks>
    /// Variants start from the original turned upright (EXIF orientation applied, then cleared), because a re-save by
    /// another app shows the photo the way it's displayed. The comparison is against the original file as it is on
    /// disk, hashed the way the app hashes it, which applies the orientation too.
    /// </remarks>
    public static IReadOnlyList<GeneratedVariant> Generate(string originalPath, string outputFolder, string stem)
    {
        // Last check right before opening: the caller's list came from a directory listing that may be stale.
        if (CloudFileAttributes.Check(originalPath) != FileAvailability.Local)
            throw new IOException($"Not stored locally (online-only or gone), so not opened: {originalPath}");

        using var upright = Image.Load<Rgb24>(originalPath);
        upright.Mutate(x => x.AutoOrient());
        upright.Metadata.ExifProfile = null; // AutoOrient resets the tag, but drop the rest too: no stale thumbnail or size tags
        upright.Metadata.XmpProfile = null;
        upright.Metadata.IptcProfile = null;

        Directory.CreateDirectory(outputFolder);
        var written = new List<GeneratedVariant>(AllKinds.Count);
        string PathFor(VariantKind kind, string extension) => Path.Combine(outputFolder, $"{stem}_{kind}{extension}");

        foreach (var kind in AllKinds)
        {
            var path = PathFor(kind, kind == VariantKind.Png ? ".png" : ".jpg");
            switch (kind)
            {
                case VariantKind.Half:
                    SaveResized(upright, 2, path, Q90);
                    break;
                case VariantKind.Quarter:
                    SaveResized(upright, 4, path, Q90);
                    break;
                case VariantKind.Quality50:
                    upright.SaveAsJpeg(path, Q50);
                    break;
                case VariantKind.Quality30:
                    upright.SaveAsJpeg(path, Q30);
                    break;
                case VariantKind.Png:
                    upright.SaveAsPng(path);
                    break;
                case VariantKind.Exif6:
                    // Orientation 6 means "rotate 90° clockwise to display", so the stored pixels are turned the other way.
                    using (var stored = upright.Clone(x => x.Rotate(RotateMode.Rotate270)))
                    {
                        stored.Metadata.ExifProfile = new ExifProfile();
                        stored.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                        stored.SaveAsJpeg(path, Q90);
                    }

                    break;
                case VariantKind.ResizedAndRecompressed:
                    SaveResized(upright, 2, path, Q30);
                    break;
                default:
                    throw new InvalidOperationException($"No generator for {kind}.");
            }

            written.Add(new GeneratedVariant(kind, path));
        }

        return written;
    }

    private static void SaveResized(Image<Rgb24> image, int divisor, string path, JpegEncoder encoder)
    {
        using var small = image.Clone(x => x.Resize(Math.Max(1, image.Width / divisor), 0)); // 0 = keep aspect ratio
        small.SaveAsJpeg(path, encoder);
    }
}
