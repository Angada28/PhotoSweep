using PhotoSweep.Core.Scanning;

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
}
