using System.Text.Json;
using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhotoSweep.Tests.Scanning;

// Each scan loads the cache fresh from disk, like a new app session, so these also test the JSON round trip.
public class ScanCacheTests : IDisposable
{
    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private Task<ScanResult> Scan(bool recursive = true) =>
        new PhotoScanner(ScanCache.Load(_temp.CachePath)).ScanAsync(new ScanOptions { Folders = [_temp.Root], Recursive = recursive });

    [Fact]
    public async Task Unchanged_files_are_not_read_again()
    {
        var path = _temp.AddPhoto("astronaut.jpg");
        _temp.AddPhoto("coffee.jpg");
        var first = await Scan();
        var originalSha = first.Files.Single(f => f.Path == path).Sha256;

        // Overwrite the bytes but keep size and timestamp. If the scanner re-read the file the SHA would change,
        // so getting the OLD hash back proves the result came from the cache.
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);
        File.SetLastWriteTimeUtc(path, time);

        var second = await Scan();

        Assert.Equal(0, first.CacheHits);
        Assert.Equal(2, second.CacheHits);
        Assert.Equal(originalSha, second.Files.Single(f => f.Path == path).Sha256);
        Assert.Equal(first.Files.Select(f => f.Fingerprint), second.Files.Select(f => f.Fingerprint));
        Assert.Equal(first.Files.Select(f => f.Details), second.Files.Select(f => f.Details));
        Assert.All(second.Files, f => Assert.NotNull(f.Details));
    }

    [Fact]
    public async Task Camera_details_survive_the_cache()
    {
        var path = Path.Combine(_temp.Root, "camera.jpg");
        using (var image = Image.Load(TempPhotoFolder.ReadTestPhoto("coffee.jpg")))
        {
            var exif = image.Metadata.ExifProfile ??= new ExifProfile();
            exif.SetValue(ExifTag.Model, "Pixel 8");
            exif.SetValue(ExifTag.DateTimeOriginal, "2024:01:02 03:04:05");
            image.SaveAsJpeg(path);
        }

        var first = Assert.Single((await Scan()).Files).Details;
        var second = await Scan();

        Assert.Equal(1, second.CacheHits);
        Assert.Equal(first, Assert.Single(second.Files).Details);
        Assert.Equal("Pixel 8", first!.CameraModel);
    }

    // Version-1 entries have no image details. Reusing them would give files with a fingerprint but no
    // resolution, and the keeper ranking would silently get worse, so they must be re-read instead.
    [Fact]
    public async Task Version_1_cache_is_ignored_even_when_its_entries_match()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        var info = new FileInfo(path);
        var entry = new Dictionary<string, object>
        {
            ["Size"] = info.Length,
            ["LastWriteUtcTicks"] = info.LastWriteTimeUtc.Ticks,
            ["Status"] = "Ok",
            ["Sha256"] = "OLD",
            ["PHash"] = 1,
            ["DHash"] = 2,
        };
        File.WriteAllText(_temp.CachePath, JsonSerializer.Serialize(
            new { Version = 1, Entries = new Dictionary<string, object> { [path] = entry } }));

        var result = await Scan();

        Assert.Equal(0, result.CacheHits);
        Assert.NotEqual("OLD", Assert.Single(result.Files).Sha256);
        Assert.NotNull(result.Files[0].Details);
    }

    [Fact]
    public async Task Changed_timestamp_means_a_cache_miss_for_that_file_only()
    {
        var path = _temp.AddPhoto("astronaut.jpg");
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg");
        await Scan();

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        var second = await Scan();

        Assert.Equal(2, second.CacheHits);
    }

    [Fact]
    public async Task Decode_failures_are_cached()
    {
        _temp.AddBytes("phone.heic", "ftypheic"u8.ToArray());
        await Scan();

        var second = await Scan();

        Assert.Equal(1, second.CacheHits);
        Assert.Equal(ScanStatus.DecodeFailed, Assert.Single(second.Files).Status);
    }

    [Fact]
    public async Task Unreadable_files_are_not_cached_so_they_are_retried()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(ScanStatus.Unreadable, Assert.Single((await Scan()).Files).Status);

        var second = await Scan();

        Assert.Equal(0, second.CacheHits);
        Assert.Equal(ScanStatus.Ok, Assert.Single(second.Files).Status);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{ "Version": 999, "Entries": {} }""")]
    [InlineData("null")]
    public async Task Corrupt_or_old_cache_file_is_ignored(string contents)
    {
        _temp.AddPhoto("coffee.jpg");
        File.WriteAllText(_temp.CachePath, contents);

        Assert.Equal(0, ScanCache.Load(_temp.CachePath).Count);
        var result = await Scan();

        Assert.Equal(ScanStatus.Ok, Assert.Single(result.Files).Status);
        Assert.Equal(1, ScanCache.Load(_temp.CachePath).Count); // and it was rewritten in the current format
    }

    [Fact]
    public async Task Files_gone_from_a_scanned_folder_are_pruned()
    {
        var moved = _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg");
        await Scan();
        Assert.Equal(2, ScanCache.Load(_temp.CachePath).Count);

        File.Move(moved, Path.Combine(_temp.Outside, "coffee.jpg"));
        await Scan();

        Assert.Equal(1, ScanCache.Load(_temp.CachePath).Count);
    }

    [Fact]
    public async Task Non_recursive_scan_does_not_prune_subfolder_entries()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg", @"sub\chelsea.jpg");
        await Scan(recursive: true);

        await Scan(recursive: false);

        Assert.Equal(2, ScanCache.Load(_temp.CachePath).Count);
    }

    [Fact]
    public async Task Entries_for_other_folders_are_kept()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg", @"other\chelsea.jpg");
        await Scan();

        // Scan only "other"; coffee.jpg isn't inside it, so its entry must survive.
        await new PhotoScanner(ScanCache.Load(_temp.CachePath))
            .ScanAsync(new ScanOptions { Folders = [Path.Combine(_temp.Root, "other")] });

        Assert.Equal(2, ScanCache.Load(_temp.CachePath).Count);
    }

    // Version 2 had no frame count. JPEG entries are still right (a JPEG can't be animated), so they're kept; GIF, PNG
    // and WebP entries might hide an animation, so those files are read again.
    [Fact]
    public async Task Version_2_cache_keeps_entries_for_formats_that_cant_be_animated()
    {
        var jpg = _temp.AddPhoto("coffee.jpg");
        var gif = TempPhotoFolder.AddAnimatedGif(_temp.Root, "burst.gif", frames: 3);
        await Scan();

        // Turn the saved cache into what version 2 wrote: same entries, no FrameCount, and poison the SHA-256s so a
        // cache hit is visible.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_temp.CachePath))!;
        json["Version"] = 2;
        foreach (var (_, entry) in json["Entries"]!.AsObject())
        {
            entry!.AsObject().Remove("FrameCount");
            entry["Sha256"] = "OLD";
        }

        File.WriteAllText(_temp.CachePath, json.ToJsonString());

        var result = await Scan();

        Assert.Equal(1, result.CacheHits);
        Assert.Equal("OLD", result.Files.Single(f => f.Path == jpg).Sha256);
        var reRead = result.Files.Single(f => f.Path == gif);
        Assert.NotEqual("OLD", reRead.Sha256);
        Assert.Equal(3, reRead.Details!.FrameCount);
    }

    [Fact]
    public async Task Frame_count_survives_the_cache()
    {
        TempPhotoFolder.AddAnimatedGif(_temp.Root, "burst.gif", frames: 4);
        await Scan();

        var second = await Scan();

        Assert.Equal(1, second.CacheHits);
        Assert.True(Assert.Single(second.Files).Details!.IsAnimated);
        Assert.Equal(4, second.Files[0].Details!.FrameCount);
    }
}
