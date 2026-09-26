using PhotoSweep.Eval;
using PhotoSweep.Eval.Synthetic;
using PhotoSweep.Tests.Scanning;

namespace PhotoSweep.Tests.Evaluation;

/// <summary>The evaluation tool only reads the photo folder, never writes into it, and never opens online-only files.</summary>
public class EvalSafetyTests
{
    [Theory]
    [InlineData(@"C:\Photos", @"C:\Photos", true)]
    [InlineData(@"C:\Photos\eval", @"C:\Photos", true)]
    [InlineData(@"C:\photos\EVAL\x", @"C:\Photos", true)] // Windows paths are case-insensitive
    [InlineData(@"C:\Photos\..\eval", @"C:\Photos", false)]
    [InlineData(@"C:\PhotosEval", @"C:\Photos", false)] // a shared prefix isn't nesting
    [InlineData(@"C:\Photos\..eval", @"C:\Photos", true)] // a folder whose name starts with two dots is still inside
    [InlineData(@"D:\Photos", @"C:\Photos", false)]
    public void Detects_an_output_folder_inside_the_photo_folder(string path, string folder, bool inside) =>
        Assert.Equal(inside, OutputFolder.IsSameOrInside(path, folder));

    [Fact]
    public void Refuses_to_write_into_the_photo_folder()
    {
        using var folder = new TempPhotoFolder();
        var inside = Path.Combine(folder.Root, "eval");

        Assert.Throws<UsageException>(() => OutputFolder.Prepare(folder.Root, inside));
        Assert.False(Directory.Exists(inside));

        var outside = OutputFolder.Prepare(folder.Root, Path.Combine(folder.Outside, "eval"));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Sampling_skips_online_only_files_without_opening_them()
    {
        using var folder = new TempPhotoFolder();
        folder.AddPhoto("astronaut.jpg");
        folder.AddPhoto("chelsea.jpg");
        var cloud = folder.AddPhoto("coffee.jpg");
        folder.AddPhoto("coffee_exif6.jpg", @"sub\x.gif"); // GIFs aren't sampled
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);

        var order = PhotoSampler.Order(PhotoSampler.List(folder.Root), seed: 1);

        Assert.Equal(["astronaut.jpg", "chelsea.jpg"], order.Files.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.Equal(1, order.OnlineOnlySkipped);
    }

    [Fact]
    public void Sampling_order_depends_only_on_the_seed()
    {
        using var folder = new TempPhotoFolder();
        foreach (var name in new[] { "astronaut.jpg", "chelsea.jpg", "coffee.jpg", "astronaut.png", "chelsea.png", "coffee.png" })
            folder.AddPhoto(name);

        var first = PhotoSampler.Order(PhotoSampler.List(folder.Root), 3).Files.Select(f => f.Path);
        var again = PhotoSampler.Order(PhotoSampler.List(folder.Root).Reverse(), 3).Files.Select(f => f.Path);
        var otherSeeds = Enumerable.Range(4, 5).Select(s => PhotoSampler.Order(PhotoSampler.List(folder.Root), s).Files.Select(f => f.Path).ToList());

        Assert.Equal(first, again);
        Assert.Contains(otherSeeds, o => !o.SequenceEqual(first));
    }

    [Fact]
    public void Variant_generator_refuses_an_online_only_original()
    {
        using var folder = new TempPhotoFolder();
        var cloud = folder.AddPhoto("coffee.jpg");
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        var output = Path.Combine(folder.Outside, "variants");

        Assert.Throws<IOException>(() => VariantGenerator.Generate(cloud, output, "x"));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Command_line_reads_positionals_and_options()
    {
        var cl = CommandLine.Parse(["Synthetic", @"C:\Photos", "--out", @"D:\eval", "--count", "50"]);

        Assert.Equal("synthetic", cl.Command);
        Assert.Equal(@"C:\Photos", cl.Positional(0, "folder"));
        Assert.Equal(@"D:\eval", cl.RequiredOption("out"));
        Assert.Equal(50, cl.Int("count", 300));
        Assert.Equal(7, cl.Int("seed", 7));
        Assert.Throws<UsageException>(() => cl.Positional(1, "extra"));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["synthetic", "--out"]));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["synthetic", "--count", "many"]).Int("count", 1));
    }
}
