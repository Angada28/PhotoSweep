using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoSweep.Core.Imaging;

namespace PhotoSweep.Desktop.Services;

/// <summary>
/// Loads thumbnails off the UI thread, a few at a time, and keeps recent ones in memory.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Settle delay:</b> a request waits <see cref="SettleDelay"/> before queueing. Dragging the scrollbar
/// shows each row for a few milliseconds; those rows are gone before the delay ends and cost nothing. At normal
/// scrolling speed 60 ms isn't noticeable.</item>
/// <item><b>Throttle:</b> a <see cref="SemaphoreSlim"/> limits how many decode at once, so scrolling doesn't start
/// hundreds of decodes that fight over the disk and the CPU. A request waits for a slot with its row's token, so a
/// row scrolled away before its turn is skipped without being decoded, and one scrolled away mid-decode stops it.</item>
/// <item><b>Cache:</b> least-recently-used with a byte budget rather than an entry count, because thumbnails differ in
/// size (a portrait at 2× DPI is much bigger than one at 1×). Keyed by path, last-write time and the box it fits.</item>
/// <item><b>Threads:</b> ImageSharp decodes on the thread pool; the <see cref="BitmapSource"/> is created and frozen
/// there too. Frozen WPF objects are read-only, so the UI thread may use them even though another thread made them.</item>
/// </list>
/// </remarks>
public sealed class ThumbnailCache(long budgetBytes, int maxConcurrentDecodes)
{
    /// <summary>
    /// One for the app. The loader is an attached property (static by nature), so it can't be handed an instance.
    /// 64 MB holds about 400 thumbnails at 160 px and 150% scaling; decodes are capped at 2 to 4 at once.
    /// </summary>
    public static ThumbnailCache Shared { get; } = new(64L * 1024 * 1024, Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(60);

    private readonly SemaphoreSlim _throttle = new(maxConcurrentDecodes);
    private readonly Lock _lock = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _index = [];
    private readonly LinkedList<Entry> _recent = []; // most recently used first
    private long _bytes;

    /// <summary>A square thumbnail; see the other overload.</summary>
    public Task<(BitmapSource? Image, ThumbnailStatus Status)> GetAsync(string path, DateTime lastWriteUtc, int pixelSize, CancellationToken ct) =>
        GetAsync(path, lastWriteUtc, pixelSize, pixelSize, keep: true, ct);

    /// <summary>
    /// The picture fitted into <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/>, or a status saying why
    /// there isn't one. Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled before
    /// it's ready. With <paramref name="keep"/> false it's decoded through the same throttle but not cached (for
    /// actual-size photos, one of which could fill the whole budget).
    /// </summary>
    public async Task<(BitmapSource? Image, ThumbnailStatus Status)> GetAsync(
        string path, DateTime lastWriteUtc, int pixelWidth, int pixelHeight, bool keep, CancellationToken ct)
    {
        ThumbnailStats.Requested();
        var key = new Key(path, lastWriteUtc, pixelWidth, pixelHeight);
        if (TryGet(key) is { } hit)
        {
            ThumbnailStats.CacheHit();
            return (hit.Image, hit.Status);
        }

        try
        {
            await Task.Delay(SettleDelay, ct);
            await _throttle.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ThumbnailStats.Skipped();
            throw;
        }

        try
        {
            if (TryGet(key) is { } loadedMeanwhile) // the same photo, requested again while this one waited
            {
                ThumbnailStats.CacheHit();
                return (loadedMeanwhile.Image, loadedMeanwhile.Status);
            }

            Entry entry;
            try
            {
                entry = await Task.Run(() => DecodeAsync(key, ct), ct);
            }
            catch (OperationCanceledException)
            {
                ThumbnailStats.Skipped();
                throw;
            }

            ThumbnailStats.Decoded();
            // Online-only and missing aren't cached: that can change at any moment, so it's checked every time.
            if (keep && (entry.Status is ThumbnailStatus.Ok or ThumbnailStatus.CannotDecode))
                Add(entry);
            return (entry.Image, entry.Status);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static async Task<Entry> DecodeAsync(Key key, CancellationToken ct)
    {
        var result = await Thumbnail.LoadAsync(key.Path, key.Width, key.Height, ct);
        if (result.Status != ThumbnailStatus.Ok)
            return new Entry(key, null, result.Status, Bytes: 256); // a small nominal cost, so failures are evicted too

        var bitmap = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, result.Bgra, result.Width * 4);
        bitmap.Freeze();
        return new Entry(key, bitmap, ThumbnailStatus.Ok, result.Bgra!.Length);
    }

    private Entry? TryGet(Key key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var node))
                return null;

            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value;
        }
    }

    private void Add(Entry entry)
    {
        lock (_lock)
        {
            if (_index.ContainsKey(entry.Key))
                return;

            _index[entry.Key] = _recent.AddFirst(entry);
            _bytes += entry.Bytes;
            while (_bytes > budgetBytes && _recent.Last is { } oldest && oldest != _recent.First)
            {
                _recent.RemoveLast();
                _index.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
            }
        }
    }

    private readonly record struct Key(string Path, DateTime LastWriteUtc, int Width, int Height);

    private sealed record Entry(Key Key, BitmapSource? Image, ThumbnailStatus Status, long Bytes);
}
