using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Tests.Scanning;

public class PhotoScannerTests : IDisposable
{
    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private ScanOptions Options(bool recursive = true) => new() { Folders = [_temp.Root], Recursive = recursive };

    private Task<ScanResult> Scan(ScanOptions? options = null, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        new PhotoScanner(ScanCache.Load(_temp.CachePath)).ScanAsync(options ?? Options(), progress, ct);

    private static string[] Names(ScanResult result) => result.Files.Select(f => Path.GetFileName(f.Path)).Order().ToArray();

    [Fact]
    public async Task Exact_copies_share_a_sha256_and_edits_do_not()
    {
        _temp.AddPhoto("astronaut.jpg");
        _temp.AddPhoto("astronaut.jpg", @"backup\astronaut copy.jpg");
        _temp.AddPhoto("astronaut_q50.jpg");

        var result = await Scan();

        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal(ScanStatus.Ok, f.Status));
        var bySha = result.Files.GroupBy(f => f.Sha256).Select(g => g.Count()).Order().ToArray();
        Assert.Equal([1, 2], bySha);
    }

    [Fact]
    public async Task Non_recursive_scan_ignores_subfolders()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg", @"sub\chelsea.jpg");

        Assert.Equal(["coffee.jpg"], Names(await Scan(Options(recursive: false))));
        Assert.Equal(["chelsea.jpg", "coffee.jpg"], Names(await Scan(Options(recursive: true))));
    }

    [Fact]
    public async Task Hidden_and_system_files_and_folders_are_skipped()
    {
        _temp.AddPhoto("coffee.jpg");
        File.SetAttributes(_temp.AddPhoto("chelsea.jpg"), FileAttributes.Hidden);
        File.SetAttributes(_temp.AddPhoto("astronaut.jpg"), FileAttributes.System);
        _temp.AddPhoto("coffee_q50.jpg", @".hidden\coffee_q50.jpg");
        File.SetAttributes(Path.Combine(_temp.Root, ".hidden"), FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(["coffee.jpg"], Names(await Scan()));
    }

    [Fact]
    public async Task Review_folder_is_skipped_at_any_depth_and_any_case()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg", $@"{ScanOptions.ReviewFolderName}\chelsea.jpg");
        _temp.AddPhoto("astronaut.jpg", @"2024\_photosweep removed\astronaut.jpg");

        Assert.Equal(["coffee.jpg"], Names(await Scan()));
    }

    [Fact]
    public async Task Files_with_other_extensions_are_ignored()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddBytes("notes.txt", "hello"u8.ToArray());
        _temp.AddBytes("clip.mp4", [1, 2, 3]);

        Assert.Equal(["coffee.jpg"], Names(await Scan()));
    }

    [Fact]
    public async Task Corrupt_and_unsupported_files_are_recorded_and_the_scan_finishes()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddBytes("broken.jpg", [0xFF, 0xD8, 0xFF, 0x00, 0x01]);
        _temp.AddBytes("phone.heic", "ftypheic"u8.ToArray());

        var result = await Scan();

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(["broken.jpg", "phone.heic"], result.Errors.Select(f => Path.GetFileName(f.Path)));
        Assert.All(result.Errors, f => Assert.Equal(ScanStatus.DecodeFailed, f.Status));
    }

    [Fact]
    public async Task Overlapping_folders_do_not_produce_duplicate_entries()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("chelsea.jpg", @"2020\chelsea.jpg");

        var options = new ScanOptions { Folders = [_temp.Root, Path.Combine(_temp.Root, "2020"), _temp.Root + @"\"] };
        var result = await Scan(options);

        Assert.Equal(["chelsea.jpg", "coffee.jpg"], Names(result));
    }

    [Fact]
    public async Task Missing_folder_is_an_error_and_other_folders_are_still_scanned()
    {
        _temp.AddPhoto("coffee.jpg");
        var missing = Path.Combine(_temp.Outside, "unplugged-usb");

        var result = await Scan(new ScanOptions { Folders = [missing, _temp.Root] });

        var error = Assert.Single(result.Errors);
        Assert.Equal(missing, error.Path);
        Assert.Equal(ScanStatus.Unreadable, error.Status);
        Assert.Contains(result.Files, f => f.Status == ScanStatus.Ok);
    }

    [Fact]
    public async Task Online_only_files_are_skipped_and_counted_by_default()
    {
        _temp.AddPhoto("coffee.jpg");
        var cloud = _temp.AddPhoto("chelsea.jpg");
        File.SetAttributes(cloud, FileAttributes.Offline);
        var progress = new ProgressLog();

        var result = await Scan(progress: progress);

        var skipped = Assert.Single(result.OnlineOnlySkipped);
        Assert.Equal(cloud, skipped.Path);
        Assert.Equal(ScanStatus.OnlineOnlySkipped, skipped.Status);
        Assert.Null(skipped.Sha256); // never opened
        Assert.Equal(new FileInfo(cloud).Length, result.OnlineOnlyBytes);
        Assert.Empty(result.Errors);

        var last = progress.Last;
        Assert.Equal(1, last.OnlineOnlySkipped);
        Assert.Equal(result.OnlineOnlyBytes, last.OnlineOnlyBytes);

        Assert.Equal(1, ScanCache.Load(_temp.CachePath).Count); // skipped files aren't cached, so opting in later re-reads them
    }

    [Fact]
    public async Task Online_only_files_are_scanned_when_the_user_opts_in()
    {
        File.SetAttributes(_temp.AddPhoto("chelsea.jpg"), FileAttributes.Offline);

        var result = await Scan(Options() with { IncludeOnlineOnlyFiles = true });

        Assert.Equal(ScanStatus.Ok, Assert.Single(result.Files).Status);
        Assert.Empty(result.OnlineOnlySkipped);
    }

    [Fact]
    public async Task Online_only_file_already_in_the_cache_is_served_from_it_without_opening()
    {
        var path = _temp.AddPhoto("chelsea.jpg");
        var first = await Scan();

        // Like OneDrive "free up space": same size and timestamp, but now online-only.
        File.SetAttributes(path, FileAttributes.Offline);
        var second = await Scan();

        Assert.Equal(1, second.CacheHits);
        Assert.Equal(ScanStatus.Ok, Assert.Single(second.Files).Status);
        Assert.Equal(first.Files[0].Sha256, second.Files[0].Sha256);
    }

    [Fact]
    public async Task Final_progress_report_shows_everything_processed()
    {
        foreach (var name in new[] { "astronaut.jpg", "chelsea.jpg", "coffee.jpg", "coffee.png" })
            _temp.AddPhoto(name);
        var progress = new ProgressLog();

        await Scan(progress: progress);

        Assert.Equal(new ScanProgress(4, 4, 0, 0, 0, 0, EnumerationComplete: true), progress.Last);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_any_work()
    {
        _temp.AddPhoto("coffee.jpg");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(ct: new CancellationToken(canceled: true)));

        Assert.False(File.Exists(_temp.CachePath));
    }

    [Fact]
    public async Task Cancelling_mid_scan_throws_and_keeps_the_finished_work_in_the_cache()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "Photos"), "*.*g"))
            _temp.AddPhoto(Path.GetFileName(file));
        var total = Directory.GetFiles(_temp.Root).Length;

        using var cts = new CancellationTokenSource();
        var progress = new ProgressLog(onReport: _ => cts.Cancel()); // cancel as soon as the first file is done
        var options = Options() with { MaxDegreeOfParallelism = 1 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(options, progress, cts.Token));

        var cached = ScanCache.Load(_temp.CachePath).Count;
        Assert.InRange(cached, 1, total - 1);
    }

    /// <summary>
    /// Synchronous IProgress for tests. <see cref="Progress{T}"/> would post reports to the thread pool,
    /// so they could arrive after ScanAsync returns.
    /// </summary>
    private sealed class ProgressLog(Action<ScanProgress>? onReport = null) : IProgress<ScanProgress>
    {
        private readonly List<ScanProgress> _reports = [];

        public ScanProgress Last
        {
            get { lock (_reports) return _reports[^1]; }
        }

        public void Report(ScanProgress value)
        {
            lock (_reports) _reports.Add(value);
            onReport?.Invoke(value);
        }
    }
}
