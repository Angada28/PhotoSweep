using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Cleanup;

/// <summary>
/// PhotoSweep never deletes photos. Clean-up moves them into <c>&lt;root&gt;/_PhotoSweep Removed/&lt;batch time&gt;/</c>,
/// keeping their subfolders, and records every move in a manifest so the batch can be undone. The user empties the
/// review folder themselves once they're happy.
/// </summary>
/// <remarks>
/// Every check here reads only directory metadata (<see cref="FileInfo"/>: exists, size, last-write time), and every move
/// is a rename (<see cref="NoCopyMove"/>). No file is ever opened, so online-only cloud files are never downloaded.
/// One file failing never stops the others; each failure is reported in the result.
/// </remarks>
public sealed class ReviewFolder(TimeProvider? timeProvider = null)
{
    private const int MaxAlternateNames = 10_000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public CleanupResult MoveToReview(CleanupPlan plan)
    {
        var id = Guid.NewGuid();
        var createdUtc = _time.GetUtcNow().UtcDateTime;
        var roots = plan.Groups.SelectMany(g => g.Remove).Select(m => m.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folderName = UnusedBatchFolderName(roots);
        var manifests = new Dictionary<string, BatchManifest>(StringComparer.OrdinalIgnoreCase);
        var moved = new List<MovedFile>();
        var failures = new List<CleanupFailure>();

        foreach (var group in plan.Groups)
        {
            // Re-checked now, not trusted from the scan: the keeper may have been deleted or edited since.
            if (!group.Keep.Any(IsUnchangedSinceScan))
            {
                var keeper = Path.GetFileName(group.Group.Keeper.File.Path);
                failures.AddRange(group.Remove.Select(m => new CleanupFailure(m.File.Path, CleanupFailureReason.KeptCopyMissing,
                    $"Skipped: none of the copies being kept (e.g. {keeper}) is still on disk unchanged.")));
                continue;
            }

            foreach (var move in group.Remove)
            {
                var manifest = manifests.TryGetValue(move.Root, out var m) ? m : manifests[move.Root] =
                    new BatchManifest(Path.Combine(move.Root, ScanOptions.ReviewFolderName, folderName, BatchManifest.FileName), id, createdUtc, []);

                if (TryMove(move, manifest) is { } failure)
                    failures.Add(failure);
                else
                    moved.Add(new MovedFile(move.File.Path, manifest.ReviewPathOf(manifest.Entries[^1])));
            }
        }

        foreach (var manifest in manifests.Values.Where(m => m.Entries.Count == 0))
            TryRemoveEmptyBatch(manifest);

        return new CleanupResult(ToBatch(id, createdUtc, manifests.Values.Where(m => m.Entries.Count > 0)), moved, failures);
    }

    /// <summary>Every batch still in the review folders of these roots, newest first. Reads the manifests from disk.</summary>
    public IReadOnlyList<CleanupBatch> FindBatches(IReadOnlyList<string> roots)
    {
        var manifests = new List<BatchManifest>();
        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var reviewDirectory = Path.Combine(root, ScanOptions.ReviewFolderName);
            if (!Directory.Exists(reviewDirectory))
                continue;

            foreach (var batchDirectory in Directory.EnumerateDirectories(reviewDirectory))
            {
                if (BatchManifest.TryLoad(Path.Combine(batchDirectory, BatchManifest.FileName)) is { } manifest)
                    manifests.Add(manifest);
            }
        }

        return manifests
            .GroupBy(m => m.Id)
            .Select(g => ToBatch(g.Key, g.First().CreatedUtc, g))
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }

    /// <summary>
    /// Moves every file in the batch back. Never overwrites: if something now lives at the original path, the file is
    /// restored alongside it as "name (2).ext". Each restored file is removed from the manifest straight away, so a
    /// partly failed undo can simply be run again.
    /// </summary>
    public UndoResult Undo(CleanupBatch batch)
    {
        var restored = new List<RestoredFile>();
        var failures = new List<CleanupFailure>();

        foreach (var manifestPath in batch.ManifestPaths)
        {
            if (BatchManifest.TryLoad(manifestPath) is not { } manifest)
            {
                failures.Add(new(manifestPath, CleanupFailureReason.NotFound, "The batch's manifest is missing or unreadable."));
                continue;
            }

            foreach (var entry in manifest.Entries.ToList())
            {
                var original = manifest.OriginalPathOf(entry);
                var reviewPath = manifest.ReviewPathOf(entry);

                if (!File.Exists(reviewPath))
                {
                    // Unchanged original: the move never happened (crash after the write-ahead entry) or an earlier undo
                    // restored it but crashed before updating the manifest. Either way there's nothing to do.
                    if (!Matches(original, entry.SizeBytes, entry.LastWriteUtc))
                        failures.Add(new(original, CleanupFailureReason.NotFound, "No longer in the review folder."));
                    manifest.Entries.Remove(entry);
                    manifest.TrySave();
                    continue;
                }

                try
                {
                    restored.Add(new RestoredFile(original, RestoreWithoutOverwriting(reviewPath, original)));
                    manifest.Entries.Remove(entry);
                    manifest.TrySave(); // if this fails, the next undo sees an unchanged original and drops the entry
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new(original, IoReason(ex), ex.Message)); // entry kept, so undo can retry
                }
            }

            if (manifest.Entries.Count == 0)
                TryRemoveEmptyBatch(manifest);
        }

        return new UndoResult(restored, failures);
    }

    /// <summary>Returns null on success, or why the file wasn't moved.</summary>
    private static CleanupFailure? TryMove(PlannedMove move, BatchManifest manifest)
    {
        var info = new FileInfo(move.File.Path);
        if (!info.Exists)
            return new(move.File.Path, CleanupFailureReason.NotFound, "The file no longer exists.");
        if (info.Length != move.File.SizeBytes || info.LastWriteTimeUtc != move.File.LastWriteUtc)
            return new(move.File.Path, CleanupFailureReason.ChangedSinceScan, "The file has changed since the scan; scan again to review it.");

        // Write-ahead: the manifest records the move before it happens. If it can't be written, the file stays put.
        var entry = new ManifestEntry(move.RelativePath, move.File.SizeBytes, move.File.LastWriteUtc);
        manifest.Entries.Add(entry);
        try
        {
            manifest.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            manifest.Entries.Remove(entry);
            return new(move.File.Path, CleanupFailureReason.IoError, $"Couldn't write the manifest, so the file wasn't moved: {ex.Message}");
        }

        try
        {
            var destination = manifest.ReviewPathOf(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            NoCopyMove.Move(move.File.Path, destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            manifest.Entries.Remove(entry);
            manifest.TrySave(); // if this fails, the stale entry is harmless: undo sees the original unchanged and drops it
            return new(move.File.Path, IoReason(ex), ex.Message);
        }
    }

    /// <summary>
    /// Tries the original path, then "name (2).ext", "name (3).ext"... The move itself refuses to overwrite, so a file
    /// that appears between the existence check and the move is still safe: that name is skipped too.
    /// </summary>
    private static string RestoreWithoutOverwriting(string reviewPath, string original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var directory = Path.GetDirectoryName(original)!;
        var name = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);

        for (var i = 1; i <= MaxAlternateNames; i++)
        {
            var candidate = i == 1 ? original : Path.Combine(directory, $"{name} ({i}){extension}");
            if (Path.Exists(candidate))
                continue;

            try
            {
                NoCopyMove.Move(reviewPath, candidate);
                return candidate;
            }
            catch (IOException ex) when (NoCopyMove.IsAlreadyExists(ex))
            {
            }
        }

        throw new IOException($"No free name to restore {original} to.");
    }

    /// <summary>"In use" is split out because it's the one I/O failure worth retrying as-is.</summary>
    private static CleanupFailureReason IoReason(Exception ex) =>
        NoCopyMove.IsInUse(ex) ? CleanupFailureReason.InUse : CleanupFailureReason.IoError;

    private static bool IsUnchangedSinceScan(ScannedFile file) => Matches(file.Path, file.SizeBytes, file.LastWriteUtc);

    private static bool Matches(string path, long size, DateTime lastWriteUtc)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size && info.LastWriteTimeUtc == lastWriteUtc;
    }

    /// <summary>Local time, as the user sees it in Explorer. Suffixed if a batch already has that name in any of the roots.</summary>
    private string UnusedBatchFolderName(List<string> roots)
    {
        var stamp = _time.GetLocalNow().ToString("yyyy-MM-dd HH.mm.ss");
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? stamp : $"{stamp} ({i})";
            if (!roots.Any(root => Path.Exists(Path.Combine(root, ScanOptions.ReviewFolderName, name))))
                return name;
        }
    }

    private static CleanupBatch ToBatch(Guid id, DateTime createdUtc, IEnumerable<BatchManifest> manifests)
    {
        var list = manifests.ToList();
        return new CleanupBatch(id, createdUtc, list.Select(m => m.ManifestPath).ToList(),
            list.Sum(m => m.Entries.Count), list.Sum(m => m.Entries.Sum(e => e.SizeBytes)));
    }

    /// <summary>
    /// Removes the manifest (our own file, and only once it lists nothing) and then any folders left empty. Every
    /// <see cref="Directory.Delete(string)"/> is non-recursive, so Windows refuses if a folder still holds anything,
    /// e.g. a file the user dropped in. Leaving an empty folder behind is harmless.
    /// </summary>
    private static void TryRemoveEmptyBatch(BatchManifest manifest)
    {
        try
        {
            File.Delete(manifest.ManifestPath);
            var subfolders = Directory.Exists(manifest.BatchDirectory)
                ? Directory.EnumerateDirectories(manifest.BatchDirectory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList()
                : [];
            foreach (var folder in subfolders.Append(manifest.BatchDirectory).Append(manifest.ReviewDirectory))
                TryDeleteEmptyFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
