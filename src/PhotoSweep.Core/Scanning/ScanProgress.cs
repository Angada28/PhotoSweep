namespace PhotoSweep.Core.Scanning;

/// <summary>
/// A progress snapshot. Files are analysed while the folder walk is still running, so the total isn't known until
/// <see cref="EnumerationComplete"/> is true; until then a UI should show "processed X of Y found so far".
/// </summary>
public sealed record ScanProgress(
    int Found,
    int Processed,
    int CacheHits,
    int Errors,
    int OnlineOnlySkipped,
    long OnlineOnlyBytes,
    bool EnumerationComplete);
