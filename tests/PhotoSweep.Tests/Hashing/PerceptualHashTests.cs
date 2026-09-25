using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Tests.Hashing;

// The hash maths on hand-built pixel arrays: no files, no decoding.
public class PerceptualHashTests
{
    private const int W = PerceptualHash.DHashWidth;
    private const int H = PerceptualHash.DHashHeight;
    private const int P = PerceptualHash.PHashSize;

    [Fact]
    public void DHash_of_flat_image_is_zero()
    {
        var pixels = Enumerable.Repeat((byte)128, W * H).ToArray();

        Assert.Equal(0UL, PerceptualHash.ComputeDHash(pixels));
    }

    [Fact]
    public void DHash_of_left_to_right_brightening_gradient_has_every_bit_set()
    {
        var pixels = new byte[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            pixels[y * W + x] = (byte)(x * 20);

        Assert.Equal(ulong.MaxValue, PerceptualHash.ComputeDHash(pixels));
    }

    [Fact]
    public void DHash_of_mirrored_gradient_is_the_opposite()
    {
        var pixels = new byte[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            pixels[y * W + x] = (byte)(255 - x * 20);

        Assert.Equal(0UL, PerceptualHash.ComputeDHash(pixels));
    }

    [Fact]
    public void PHash_is_deterministic()
    {
        var pixels = Pattern();

        Assert.Equal(PerceptualHash.ComputePHash(pixels), PerceptualHash.ComputePHash(pixels));
    }

    [Fact]
    public void PHash_ignores_uniform_brightness_shift()
    {
        // Adding a constant only changes the DC coefficient, which stays above the median either way.
        var pixels = Pattern();
        var brighter = pixels.Select(p => (byte)(p + 40)).ToArray();

        Assert.Equal(PerceptualHash.ComputePHash(pixels), PerceptualHash.ComputePHash(brighter));
    }

    [Fact]
    public void PHash_of_inverted_image_is_far_away()
    {
        // Inverting negates every AC coefficient, so the above/below-median bits (almost) all flip.
        var pixels = Pattern();
        var inverted = pixels.Select(p => (byte)(255 - p)).ToArray();

        var distance = Hamming.Distance(PerceptualHash.ComputePHash(pixels), PerceptualHash.ComputePHash(inverted));

        Assert.True(distance >= 40, $"distance was {distance}");
    }

    [Fact]
    public void Wrong_input_size_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => PerceptualHash.ComputePHash(new byte[10]));
        Assert.Throws<ArgumentException>(() => PerceptualHash.ComputeDHash(new byte[10]));
    }

    // A deterministic 32×32 image with structure at several frequencies, values kept in 0..200.
    private static byte[] Pattern()
    {
        var pixels = new byte[P * P];
        for (var y = 0; y < P; y++)
        for (var x = 0; x < P; x++)
            pixels[y * P + x] = (byte)(100 + 50 * Math.Sin(x * 0.3) + 40 * Math.Cos(y * 0.5 + x * 0.1));
        return pixels;
    }
}
