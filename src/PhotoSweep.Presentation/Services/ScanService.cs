using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation.Services;

/// <summary>
/// The real engine: <see cref="PhotoScanner"/> with a cache file, then <see cref="DuplicateGrouper"/>. Both run on the
/// thread pool so the UI thread only ever handles progress reports.
/// </summary>
/// <remarks>
/// Lives in Presentation, not Desktop, because nothing here is Windows- or WPF-specific; that also lets tests run it.
/// The cache is loaded once and kept, so a re-scan (e.g. "download and scan them too") reads everything already
/// scanned from memory. The gate lets one scan use it at a time: a cancelled scan saves the cache in its
/// <c>finally</c>, and a new scan mustn't start changing it while that save is still running.
/// </remarks>
public sealed class ScanService(string cachePath) : IScanService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScanCache? _cache; // only touched while holding _gate

    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Task.Run because ScanCache.Load parses JSON synchronously, and PhotoScanner runs synchronously until its
            // first await; neither should run on the UI thread.
            return await Task.Run(() =>
            {
                _cache ??= ScanCache.Load(cachePath);
                return new PhotoScanner(_cache).ScanAsync(options, progress, ct);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="DuplicateGrouper"/> can't be stopped part-way (it takes well under a second even for large
    /// libraries), so cancellation is checked before and after it.
    /// </summary>
    public Task<IReadOnlyList<PhotoGroup>> GroupAsync(ScanResult scan, MatchLevel level, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var groups = DuplicateGrouper.Group(scan, level);
            ct.ThrowIfCancellationRequested();
            return groups;
        }, ct);
}
