using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation;

/// <summary>What a finished scan hands to the results page.</summary>
/// <param name="UnreadableFolders">Chosen folders that couldn't be read (e.g. removed); the rest were scanned.</param>
public sealed record ScanOutcome(
    ScanRequest Request,
    ScanResult Scan,
    IReadOnlyList<PhotoGroup> Groups,
    IReadOnlyList<string> UnreadableFolders);
