using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PhotoSweep.Core.Scanning;

/// <summary>
/// Scans folders for photos: one producer walks the folders into a bounded channel while
/// <see cref="Parallel.ForEachAsync{TSource}(IAsyncEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// analyses files from it in parallel.
/// </summary>
/// <remarks>
/// The bounded channel means analysis starts on the first file found, not after the whole walk, and gives
/// backpressure: when the workers fall behind, the walk pauses instead of buffering every path on a large drive.
/// </remarks>
public sealed class PhotoScanner(ScanCache cache, FileAnalyzer? analyzer = null)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileAnalyzer _analyzer = analyzer ?? new FileAnalyzer();

    /// <summary>
    /// Scans <see cref="ScanOptions.Folders"/>. Throws <see cref="OperationCanceledException"/> if cancelled; the
    /// cache is still saved, so the work already done is skipped next time.
    /// </summary>
    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChannelCapacity, 1);
        ct.ThrowIfCancellationRequested();

        var roots = options.Folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new ConcurrentBag<ScannedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // only the producer touches this
        var counters = new Counters();
        var channel = Channel.CreateBounded<FileCandidate>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });

        // Linked so that if the workers stop early, the producer (possibly blocked on a full channel) stops too.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;
        var producer = Task.Run(() => ProduceAsync(roots, options, channel.Writer, seen, results, counters, token), token);
        var completed = false;
        try
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = token };
            await Parallel.ForEachAsync(channel.Reader.ReadAllAsync(token), parallelOptions, async (file, fileToken) =>
            {
                var result = await ProcessAsync(file, options, counters, fileToken);
                results.Add(result);
                counters.Record(result);
                Interlocked.Increment(ref counters.Processed);
                if (progress is not null && counters.TryClaimReport())
                    progress.Report(counters.Snapshot());
            });

            await producer;
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await linked.CancelAsync();
                try { await producer; }
                catch { /* already failing with the workers' exception (usually cancellation); don't mask it */ }
            }
            else
            {
                cache.Prune(roots, options.Recursive, seen); // only a complete walk knows which files are gone
            }

            cache.TrySave();
        }

        progress?.Report(counters.Snapshot());
        return new ScanResult(results, counters.CacheHits);
    }

    private async Task<ScannedFile> ProcessAsync(FileCandidate file, ScanOptions options, Counters counters, CancellationToken ct)
    {
        // Cache first: a lookup uses only the listing's size + time, so a cached online-only file is served without a download.
        if (cache.TryGet(file, out var cached))
        {
            Interlocked.Increment(ref counters.CacheHits);
            return cached;
        }

        if (!options.IncludeOnlineOnlyFiles && CloudFileAttributes.IsOnlineOnly(file.Attributes))
            return ScannedFile.From(file) with { Status = ScanStatus.OnlineOnlySkipped };

        var result = await _analyzer.AnalyzeAsync(file, ct);
        cache.Put(result);
        return result;
    }

    private static async Task ProduceAsync(
        IReadOnlyList<string> roots,
        ScanOptions options,
        ChannelWriter<FileCandidate> writer,
        HashSet<string> seen,
        ConcurrentBag<ScannedFile> results,
        Counters counters,
        CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            foreach (var root in roots)
            {
                try
                {
                    foreach (var file in FileWalker.Enumerate(root, options))
                    {
                        if (!seen.Add(file.Path))
                            continue; // overlapping folders, e.g. C:\Photos and C:\Photos\2020

                        Interlocked.Increment(ref counters.Found);
                        await writer.WriteAsync(file, ct); // waits while the channel is full (backpressure)
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Missing folder, or a drive removed mid-walk: report it and carry on with the other folders.
                    var error = new ScannedFile(root, 0, default) { Status = ScanStatus.Unreadable, Error = $"Can't read folder: {ex.Message}" };
                    results.Add(error);
                    counters.Record(error);
                }
            }

            counters.EnumerationComplete = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            // Always complete the channel, or the workers would wait for more files forever.
            writer.Complete(failure);
        }
    }

    /// <summary>Counters shared by the producer and all workers, updated with Interlocked (no locks).</summary>
    private sealed class Counters
    {
        public int Found;
        public int Processed;
        public int CacheHits;
        public int Errors;
        public int OnlineOnly;
        public long OnlineOnlyBytes;
        public volatile bool EnumerationComplete;
        private long _lastReportTicks = Environment.TickCount64 - (long)ProgressInterval.TotalMilliseconds;

        public void Record(ScannedFile file)
        {
            if (file.IsError)
                Interlocked.Increment(ref Errors);

            if (file.Status == ScanStatus.OnlineOnlySkipped)
            {
                Interlocked.Increment(ref OnlineOnly);
                Interlocked.Add(ref OnlineOnlyBytes, file.SizeBytes);
            }
        }

        /// <summary>
        /// At most one report per interval, so the UI thread isn't flooded with one update per file.
        /// CompareExchange makes sure only one of the racing workers wins each interval.
        /// </summary>
        public bool TryClaimReport()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReportTicks);
            return now - last >= ProgressInterval.TotalMilliseconds
                && Interlocked.CompareExchange(ref _lastReportTicks, now, last) == last;
        }

        public ScanProgress Snapshot() => new(
            Volatile.Read(ref Found),
            Volatile.Read(ref Processed),
            Volatile.Read(ref CacheHits),
            Volatile.Read(ref Errors),
            Volatile.Read(ref OnlineOnly),
            Interlocked.Read(ref OnlineOnlyBytes),
            EnumerationComplete);
    }
}
