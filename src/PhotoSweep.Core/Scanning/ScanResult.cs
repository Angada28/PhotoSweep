namespace PhotoSweep.Core.Scanning;

/// <summary>Everything one scan found, sorted by path so results are deterministic regardless of thread timing.</summary>
public sealed class ScanResult
{
    public ScanResult(IEnumerable<ScannedFile> files, int cacheHits)
    {
        Files = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        CacheHits = cacheHits;
        Errors = Files.Where(f => f.IsError).ToList();
        OnlineOnlySkipped = Files.Where(f => f.Status == ScanStatus.OnlineOnlySkipped).ToList();
        OnlineOnlyBytes = OnlineOnlySkipped.Sum(f => f.SizeBytes);
    }

    public IReadOnlyList<ScannedFile> Files { get; }

    /// <summary>Files whose result came from the cache instead of being read again.</summary>
    public int CacheHits { get; }

    public IReadOnlyList<ScannedFile> Errors { get; }

    /// <summary>Lets the UI offer "N photos (X GB) are online-only; scan them too?".</summary>
    public IReadOnlyList<ScannedFile> OnlineOnlySkipped { get; }

    public long OnlineOnlyBytes { get; }
}
