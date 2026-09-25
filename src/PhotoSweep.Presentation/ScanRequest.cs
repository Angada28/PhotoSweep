using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation;

/// <summary>
/// Everything the start screen hands to a scan. <see cref="Level"/> is kept out of <see cref="ScanOptions"/> because the
/// scan itself doesn't depend on it; it only matters when grouping (see docs/decisions.md).
/// </summary>
public sealed record ScanRequest(ScanOptions Options, MatchLevel Level);
