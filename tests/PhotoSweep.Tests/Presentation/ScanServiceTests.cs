using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;
using PhotoSweep.Tests.Scanning;

namespace PhotoSweep.Tests.Presentation;

/// <summary>The real service against real files and a temp cache file.</summary>
public class ScanServiceTests : IDisposable
{
    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private ScanOptions Options => new() { Folders = [_temp.Root], MaxDegreeOfParallelism = 1 };

    private static readonly IProgress<ScanProgress> NoProgress = new CallbackProgress(_ => { });

    [Fact]
    public async Task Scans_and_groups_real_photos()
    {
        _temp.AddPhoto("astronaut.jpg");
        _temp.AddPhoto("astronaut_q50.jpg");
        _temp.AddPhoto("coffee.jpg");
        var service = new ScanService(_temp.CachePath);

        var scan = await service.ScanAsync(Options, NoProgress, CancellationToken.None);
        var samePhoto = await service.GroupAsync(scan, MatchLevel.SamePhoto, CancellationToken.None);
        var exact = await service.GroupAsync(scan, MatchLevel.Exact, CancellationToken.None);

        Assert.Equal(3, scan.Files.Count);
        var group = Assert.Single(samePhoto);
        Assert.Equal(["astronaut.jpg", "astronaut_q50.jpg"], group.Members.Select(m => Path.GetFileName(m.File.Path)).Order(StringComparer.Ordinal));
        Assert.Empty(exact);
    }

    [Fact]
    public async Task Cancelling_leaves_a_complete_cache_that_the_next_scan_uses()
    {
        foreach (var name in new[] { "astronaut.jpg", "chelsea.jpg", "coffee.jpg", "astronaut_q50.jpg", "chelsea_q50.jpg", "coffee_q50.jpg" })
            _temp.AddPhoto(name);
        var service = new ScanService(_temp.CachePath);
        using var cts = new CancellationTokenSource();

        // The first report comes right after the first file is analysed and cached; cancel there, mid-scan.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ScanAsync(Options, new CallbackProgress(_ => cts.Cancel()), cts.Token));

        Assert.False(File.Exists(_temp.CachePath + ".tmp"));
        var saved = ScanCache.Load(_temp.CachePath).Count; // Load returns an empty cache for a corrupt file
        Assert.InRange(saved, 1, 5);

        var rescan = await service.ScanAsync(Options, NoProgress, CancellationToken.None);

        Assert.Equal(6, rescan.Files.Count);
        Assert.Equal(saved, rescan.CacheHits);
    }

    [Fact]
    public async Task Grouping_honours_cancellation()
    {
        var service = new ScanService(_temp.CachePath);
        var scan = await service.ScanAsync(Options, NoProgress, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GroupAsync(scan, MatchLevel.SamePhoto, new CancellationToken(canceled: true)));
    }

    /// <summary>Calls back on the reporting thread; <see cref="Progress{T}"/> would post to the thread pool instead.</summary>
    private sealed class CallbackProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
