namespace PhotoSweep.Core.Cleanup;

public enum CleanupFailureReason
{
    /// <summary>None of the group's kept copies is still on disk unchanged, so removing the rest could lose the photo.</summary>
    KeptCopyMissing,

    /// <summary>The file's size or last-write time no longer matches the scan; it may have been edited.</summary>
    ChangedSinceScan,

    /// <summary>The file isn't where it should be (deleted, or emptied out of the review folder).</summary>
    NotFound,

    /// <summary>Another app has the file open, so Windows refused to move it. Worth retrying once that app lets go.</summary>
    InUse,

    /// <summary>The move itself failed for another reason: access denied, different volume, manifest not writable.</summary>
    IoError,
}

/// <summary>One file that was skipped or couldn't be moved. The other files carry on regardless.</summary>
public sealed record CleanupFailure(string Path, CleanupFailureReason Reason, string Message);

public sealed record MovedFile(string OriginalPath, string ReviewPath);

public sealed record RestoredFile(string OriginalPath, string RestoredPath)
{
    /// <summary>True when something else now lives at the original path, so the file came back as "name (2).jpg".</summary>
    public bool AlternateName => !string.Equals(OriginalPath, RestoredPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One clean-up, which can be undone as a unit. A handle, not a copy of the data: the manifests on disk are the source
/// of truth, and <see cref="ReviewFolder.Undo"/> re-reads them. There is one manifest per scanned root the clean-up
/// touched, all sharing <see cref="Id"/>.
/// </summary>
public sealed record CleanupBatch(Guid Id, DateTime CreatedUtc, IReadOnlyList<string> ManifestPaths, int FileCount, long TotalBytes);

/// <param name="Moved">Photos moved. Their Takeout sidecars aren't listed here; see <see cref="SidecarsMoved"/>.</param>
/// <param name="Failures">Photos that weren't moved.</param>
public sealed record CleanupResult(CleanupBatch Batch, IReadOnlyList<MovedFile> Moved, IReadOnlyList<CleanupFailure> Failures)
{
    /// <summary>Takeout metadata sidecars moved along with their photos (<see cref="Sidecars"/>).</summary>
    public int SidecarsMoved { get; init; }

    /// <summary>
    /// Sidecars that stayed behind although their photo moved. Kept apart from <see cref="Failures"/> because the photo
    /// itself did move: a sidecar never holds its photo back.
    /// </summary>
    public IReadOnlyList<CleanupFailure> SidecarFailures { get; init; } = [];
}

/// <param name="Restored">Photos put back. Their sidecars aren't listed here; see <see cref="SidecarsRestored"/>.</param>
/// <param name="Failures">Photos that couldn't be put back.</param>
public sealed record UndoResult(IReadOnlyList<RestoredFile> Restored, IReadOnlyList<CleanupFailure> Failures)
{
    public int SidecarsRestored { get; init; }

    /// <summary>Sidecars that couldn't be put back. Their photos are reported (restored or not) as usual.</summary>
    public IReadOnlyList<CleanupFailure> SidecarFailures { get; init; } = [];
}
