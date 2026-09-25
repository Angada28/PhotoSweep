using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoSweep.Core.Imaging;

/// <summary>Decodes a photo (any format ImageSharp supports) into a small, upright, 8-bit greyscale image.</summary>
public static class GrayscaleLoader
{
    /// <summary>
    /// Longest side after decoding. Hashes only need 32×32, so there is no point decoding a 24 MP photo in full.
    /// With <see cref="DecoderOptions.TargetSize"/> ImageSharp's JPEG decoder scales during decoding, which is
    /// much cheaper than decoding full size and resizing afterwards.
    /// </summary>
    public const int DecodeSize = 256;

    private static readonly DecoderOptions Options = new() { TargetSize = new Size(DecodeSize, DecodeSize) };

    /// <summary>Caller owns (and must dispose) the returned image.</summary>
    public static Image<L8> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <inheritdoc cref="Load(string)"/>
    public static Image<L8> Load(Stream stream)
    {
        var image = Image.Load<L8>(Options, stream);

        // Rotate/flip according to the EXIF Orientation tag, so a portrait phone photo stored sideways
        // hashes the same as an upright copy. Must happen before any resizing to non-square sizes.
        image.Mutate(x => x.AutoOrient());
        return image;
    }
}
