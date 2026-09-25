using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation.Services;

/// <summary>
/// The scan engine as the scanning page sees it. Two steps, so the page knows when grouping starts.
/// <see cref="ScanService"/> is the real one; tests use a fake that completes each step on command.
/// </summary>
public interface IScanService
{
    /// <summary>Finds and reads photos. Throws <see cref="OperationCanceledException"/> once the scan has stopped and the cache is saved.</summary>
    Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken ct);

    /// <summary>Groups a finished scan at <paramref name="level"/>.</summary>
    Task<IReadOnlyList<PhotoGroup>> GroupAsync(ScanResult scan, MatchLevel level, CancellationToken ct);
}
