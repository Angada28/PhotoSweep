using PhotoSweep.Core.Hashing;
using PhotoSweep.Eval.Synthetic;
using PhotoSweep.Tests.Scanning;
using SixLabors.ImageSharp;
using Xunit.Abstractions;

namespace PhotoSweep.Tests.Evaluation;

// Checks the generator, not the hash: if a variant came out wrong (e.g. EXIF rotation applied twice, or the wrong
// way round), it would stop matching its original. The bound is the same loose sanity bound as
// FingerprintRobustnessTests (10 bits; unrelated photos sit around 32), deliberately not a tuned threshold.
public class VariantGeneratorTests(ITestOutputHelper output)
{
    private const int SameMaxDistance = 10;

    [Theory]
    [InlineData("astronaut.jpg")]
    [InlineData("chelsea.jpg")]
    [InlineData("coffee_exif6.jpg")] // an original that is itself stored sideways
    public void Every_variant_is_written_and_hashes_close_to_its_original(string photo)
    {
        using var folder = new TempPhotoFolder();
        var original = folder.AddPhoto(photo);
        var variantDir = Path.Combine(folder.Outside, "variants");

        var variants = VariantGenerator.Generate(original, variantDir, "0001");

        Assert.Equal(VariantGenerator.AllKinds, variants.Select(v => v.Kind));
        var reference = Fingerprinter.Compute(original);
        foreach (var v in variants)
        {
            Assert.StartsWith(variantDir, v.Path, StringComparison.Ordinal);
            var fp = Fingerprinter.Compute(v.Path);
            var (p, d) = (Hamming.Distance(reference.PHash, fp.PHash), Hamming.Distance(reference.DHash, fp.DHash));
            output.WriteLine($"{photo} {v.Kind}: pHash {p}, dHash {d}");
            Assert.True(p <= SameMaxDistance && d <= SameMaxDistance, $"{v.Kind} is {p}/{d} bits from its original");
        }
    }

    [Fact]
    public void Variants_have_the_intended_size_format_and_orientation()
    {
        using var folder = new TempPhotoFolder();
        var original = folder.AddPhoto("chelsea.jpg");
        var size = Image.Identify(original);
        var variants = VariantGenerator.Generate(original, folder.Outside, "c").ToDictionary(v => v.Kind, v => v.Path);

        Assert.Equal(size.Width / 2, Image.Identify(variants[VariantKind.Half]).Width);
        Assert.Equal(size.Width / 4, Image.Identify(variants[VariantKind.Quarter]).Width);
        Assert.Equal(size.Width / 2, Image.Identify(variants[VariantKind.ResizedAndRecompressed]).Width);
        Assert.Equal("PNG", Image.Identify(variants[VariantKind.Png]).Metadata.DecodedImageFormat!.Name);

        // Stored sideways (width and height swapped) with Orientation 6, so it only looks right once rotated.
        var exif6 = Image.Identify(variants[VariantKind.Exif6]);
        Assert.Equal((size.Height, size.Width), (exif6.Width, exif6.Height));
        Assert.True(exif6.Metadata.ExifProfile!.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var o));
        Assert.Equal((ushort)6, o.Value);

        // Recompressing harder makes a smaller file.
        Assert.True(new FileInfo(variants[VariantKind.Quality30]).Length < new FileInfo(variants[VariantKind.Quality50]).Length);
    }

    [Fact]
    public void A_file_that_does_not_decode_writes_nothing()
    {
        using var folder = new TempPhotoFolder();
        var broken = folder.AddBytes("broken.jpg", [1, 2, 3, 4]);
        var variantDir = Path.Combine(folder.Outside, "variants");

        Assert.ThrowsAny<ImageFormatException>(() => VariantGenerator.Generate(broken, variantDir, "b"));
        Assert.False(Directory.Exists(variantDir));
    }
}
