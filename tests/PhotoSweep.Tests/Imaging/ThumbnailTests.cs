using PhotoSweep.Core.Imaging;
using PhotoSweep.Tests.Scanning;

namespace PhotoSweep.Tests.Imaging;

public class ThumbnailTests
{
    private static readonly string PhotoDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Photos");

    private static string TestPhoto(string name) => Path.Combine(PhotoDir, name);

    [Fact]
    public async Task Fits_within_the_requested_size_and_keeps_the_aspect_ratio()
    {
        var result = await Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 64); // 256×170

        Assert.Equal(ThumbnailStatus.Ok, result.Status);
        Assert.Equal(64, result.Width);
        Assert.InRange(result.Height, 41, 44);
        Assert.Equal(result.Width * result.Height * 4, result.Bgra!.Length);
    }

    [Fact]
    public async Task Never_enlarges_a_small_photo()
    {
        var result = await Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 1000);

        Assert.Equal((256, 170), (result.Width, result.Height));
    }

    [Fact]
    public async Task Fits_a_wide_box_by_its_height()
    {
        var result = await Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 200, 50); // 256×170

        Assert.Equal(50, result.Height);
        Assert.InRange(result.Width, 74, 76);
    }

    [Fact]
    public async Task Fits_a_box_the_same_way_for_a_photo_stored_sideways()
    {
        // chelsea_exif6 is stored 170×256 and turned upright by its EXIF tag; the box applies to the upright picture.
        var upright = await Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 128, 60);
        var tagged = await Thumbnail.LoadAsync(TestPhoto("chelsea_exif6.jpg"), 128, 60);

        Assert.Equal((upright.Width, upright.Height), (tagged.Width, tagged.Height));
        Assert.Equal(60, tagged.Height);
    }

    [Fact]
    public async Task An_unlimited_box_gives_the_actual_size()
    {
        var result = await Thumbnail.LoadAsync(TestPhoto("chelsea_exif6.jpg"), int.MaxValue, int.MaxValue);

        Assert.Equal((256, 170), (result.Width, result.Height));
    }

    [Fact]
    public async Task Applies_the_EXIF_orientation()
    {
        // Stored sideways (170×256) with Orientation = 6; displayed as the same landscape picture as chelsea.jpg.
        var upright = await Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 128);
        var tagged = await Thumbnail.LoadAsync(TestPhoto("chelsea_exif6.jpg"), 128);

        Assert.Equal((upright.Width, upright.Height), (tagged.Width, tagged.Height));
        Assert.True(tagged.Width > tagged.Height);
        // Same picture the same way up: pixels differ only by JPEG noise, not by a 90° turn.
        Assert.InRange(MeanDifference(upright.Bgra!, tagged.Bgra!), 0, 8);
    }

    [Fact]
    public async Task Does_not_open_an_online_only_file()
    {
        using var folder = new TempPhotoFolder();
        var path = folder.AddPhoto("chelsea.jpg");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        try
        {
            // Held open with no sharing: if Load tried to open the file it would fail and report Unavailable.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = await Thumbnail.LoadAsync(path, 64);
                var actualSize = await Thumbnail.LoadAsync(path, int.MaxValue, int.MaxValue); // the compare window's 1:1

                Assert.Equal(ThumbnailStatus.OnlineOnly, result.Status);
                Assert.Null(result.Bgra);
                Assert.Equal(ThumbnailStatus.OnlineOnly, actualSize.Status);
            }
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task A_missing_file_is_unavailable() =>
        Assert.Equal(ThumbnailStatus.Unavailable, (await Thumbnail.LoadAsync(Path.Combine(PhotoDir, "gone.jpg"), 64)).Status);

    [Fact]
    public async Task A_file_that_is_not_an_image_cannot_be_decoded()
    {
        using var folder = new TempPhotoFolder();
        var path = folder.AddBytes("fake.jpg", "not a picture"u8.ToArray());

        Assert.Equal(ThumbnailStatus.CannotDecode, (await Thumbnail.LoadAsync(path, 64)).Status);
    }

    [Fact]
    public async Task Honours_cancellation() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Thumbnail.LoadAsync(TestPhoto("chelsea.jpg"), 64, new CancellationToken(canceled: true)));

    private static double MeanDifference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
}
