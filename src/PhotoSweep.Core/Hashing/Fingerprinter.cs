using PhotoSweep.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoSweep.Core.Hashing;

/// <summary>
/// Loads an image and computes its <see cref="ImageFingerprint"/>. Decode errors (unsupported or corrupt files)
/// propagate as ImageSharp exceptions for the caller to handle.
/// </summary>
public static class Fingerprinter
{
    public static ImageFingerprint Compute(string path)
    {
        using var image = GrayscaleLoader.Load(path);
        return Compute(image);
    }

    public static ImageFingerprint Compute(Stream stream)
    {
        using var image = GrayscaleLoader.Load(stream);
        return Compute(image);
    }

    /// <summary>Fingerprints an already-decoded image as-is (no EXIF orientation applied).</summary>
    public static ImageFingerprint Compute(Image<L8> image)
    {
        Span<byte> pHashPixels = stackalloc byte[PerceptualHash.PHashSize * PerceptualHash.PHashSize];
        Span<byte> dHashPixels = stackalloc byte[PerceptualHash.DHashWidth * PerceptualHash.DHashHeight];

        Shrink(image, PerceptualHash.PHashSize, PerceptualHash.PHashSize, pHashPixels);
        Shrink(image, PerceptualHash.DHashWidth, PerceptualHash.DHashHeight, dHashPixels);

        return new ImageFingerprint(PerceptualHash.ComputePHash(pHashPixels), PerceptualHash.ComputeDHash(dHashPixels));
    }

    // Stretch to exactly width×height (aspect ratio is deliberately ignored, as in standard pHash/dHash).
    // Box = area averaging: every source pixel contributes, which smooths away JPEG noise.
    private static void Shrink(Image<L8> image, int width, int height, Span<byte> destination)
    {
        using var small = image.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Box,
        }));

        small.CopyPixelDataTo(destination); // L8 is one byte per pixel, so this is a straight copy
    }
}
