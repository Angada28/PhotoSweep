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
/// <para>
/// Google Takeout metadata sidecars (<see cref="Sidecars"/>) travel with their photo: moved right after it, recorded in
/// the same manifest, and put back next to wherever the photo was put back. A sidecar only ever moves once its photo
/// has, and a sidecar that can't move is reported separately without holding its photo back.
/// </para>
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
        var sidecarFailures = new List<CleanupFailure>();
        var sidecarsMoved = 0;

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
                {
                    failures.Add(failure);
                    continue;
                }

                var photo = manifest.Entries[^1];
                moved.Add(new MovedFile(move.File.Path, manifest.ReviewPathOf(photo)));

                // Only now that the photo has moved, so a sidecar never goes without its photo.
                foreach (var sidecar in Sidecars.Find(move.File.Path))
                {
                    if (TryMoveSidecar(sidecar, sidecar[move.File.Path.Length..], photo, manifest) is { } sidecarFailure)
                        sidecarFailures.Add(sidecarFailure);
                    else
                        sidecarsMoved++;
                }
            }
        }

        foreach (var manifest in manifests.Values.Where(m => m.Entries.Count == 0))
            TryRemoveEmptyBatch(manifest);

        return new CleanupResult(ToBatch(id, createdUtc, manifests.Values.Where(m => m.Entries.Count > 0)), moved, failures)
        {
            SidecarsMoved = sidecarsMoved,
            SidecarFailures = sidecarFailures,
        };
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
    /// <remarks>
    /// A photo's sidecars follow it only once it's out of the review folder, and go next to where it actually landed:
    /// "a (2).jpg" gets "a (2).jpg.json", so the pair stays together. A sidecar whose photo can't be put back stays in the
    /// manifest for the next undo; one whose photo is gone from the review folder stays where it is.
    /// </remarks>
    public UndoResult Undo(CleanupBatch batch)
    {
        var restored = new List<RestoredFile>();
        var failures = new List<CleanupFailure>();
        var sidecarFailures = new List<CleanupFailure>();
        var sidecarsRestored = 0;

        foreach (var manifestPath in batch.ManifestPaths)
        {
            if (BatchManifest.TryLoad(manifestPath) is not { } manifest)
            {
                failures.Add(new(manifestPath, CleanupFailureReason.NotFound, "The batch's manifest is missing or unreadable."));
                continue;
            }

            var sidecarsOf = manifest.Entries.Where(e => e.IsSidecar).ToLookup(e => e.SidecarOf!, StringComparer.OrdinalIgnoreCase);
            var photos = manifest.Entries.Where(e => !e.IsSidecar).ToList();

            foreach (var entry in photos)
            {
                var original = manifest.OriginalPathOf(entry);
                var reviewPath = manifest.ReviewPathOf(entry);

                if (!File.Exists(reviewPath))
                {
                    // Unchanged original: the move never happened (crash after the write-ahead entry) or an earlier undo
                    // restored it but crashed before updating the manifest. Either way there's nothing to do.
                    var alreadyBack = Matches(original, entry.SizeBytes, entry.LastWriteUtc);
                    if (!alreadyBack)
                        failures.Add(new(original, CleanupFailureReason.NotFound, "No longer in the review folder."));
                    manifest.Entries.Remove(entry);
                    manifest.TrySave();

                    if (alreadyBack)
                        RestoreSidecars(manifest, sidecarsOf[entry.RelativePath], original);
                    else
                        LeaveSidecars(manifest, sidecarsOf[entry.RelativePath]);
                    continue;
                }

                string restoredPath;
                try
                {
                    restoredPath = RestoreWithoutOverwriting(reviewPath, original);
                    restored.Add(new RestoredFile(original, restoredPath));
                    manifest.Entries.Remove(entry);
                    manifest.TrySave(); // if this fails, the next undo sees an unchanged original and drops the entry
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new(original, IoReason(ex), ex.Message)); // entry kept, so undo can retry; its sidecars wait
                    continue;
                }

                RestoreSidecars(manifest, sidecarsOf[entry.RelativePath], restoredPath);
            }

            // Sidecars whose photo an earlier undo already put back, but which couldn't follow it then.
            var photoPaths = photos.Select(p => p.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var orphans in sidecarsOf.Where(g => !photoPaths.Contains(g.Key)))
                RestoreSidecars(manifest, orphans, Path.Combine(manifest.Root, orphans.Key));

            if (manifest.Entries.Count == 0)
                TryRemoveEmptyBatch(manifest);
        }

        return new UndoResult(restored, failures) { SidecarsRestored = sidecarsRestored, SidecarFailures = sidecarFailures };

        void RestoreSidecars(BatchManifest manifest, IEnumerable<ManifestEntry> sidecars, string photoPath)
        {
            foreach (var entry in sidecars)
            {
                var original = manifest.OriginalPathOf(entry);
                var reviewPath = manifest.ReviewPathOf(entry);
                if (!File.Exists(reviewPath))
                {
                    if (!Matches(original, entry.SizeBytes, entry.LastWriteUtc))
                        sidecarFailures.Add(new(original, CleanupFailureReason.NotFound, "No longer in the review folder."));
                    manifest.Entries.Remove(entry);
                    manifest.TrySave();
                    continue;
                }

                try
                {
                    RestoreWithoutOverwriting(reviewPath, photoPath + entry.RelativePath[entry.SidecarOf!.Length..]);
                    sidecarsRestored++;
                    manifest.Entries.Remove(entry);
                    manifest.TrySave();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sidecarFailures.Add(new(original, IoReason(ex), $"{ex.Message} It's still in the review folder: {reviewPath}"));
                }
            }
        }

        // The photo is gone from the review folder (e.g. the user deleted it there), so its sidecar stays too: putting
        // metadata back into the library without its photo would only leave an orphan there instead.
        void LeaveSidecars(BatchManifest manifest, IEnumerable<ManifestEntry> sidecars)
        {
            foreach (var entry in sidecars)
            {
                var reviewPath = manifest.ReviewPathOf(entry);
                if (File.Exists(reviewPath))
                    sidecarFailures.Add(new(manifest.OriginalPathOf(entry), CleanupFailureReason.NotFound,
                        $"Its photo is no longer in the review folder, so it was left there too: {reviewPath}"));
                manifest.Entries.Remove(entry);
                manifest.TrySave();
            }
        }
    }

    /// <summary>Returns null on success, or why the file wasn't moved.</summary>
    private static CleanupFailure? TryMove(PlannedMove move, BatchManifest manifest)
    {
        var info = new FileInfo(move.File.Path);
        if (!info.Exists)
            return new(move.File.Path, CleanupFailureReason.NotFound, "The file no longer exists.");
        if (info.Length != move.File.SizeBytes || info.LastWriteTimeUtc != move.File.LastWriteUtc)
            return new(move.File.Path, CleanupFailureReason.ChangedSinceScan, "The file has changed since the scan; scan again to review it.");

        return WriteAheadAndMove(move.File.Path, new ManifestEntry(move.RelativePath, move.File.SizeBytes, move.File.LastWriteUtc), manifest);
    }

    /// <summary>
    /// Moves one sidecar of an already-moved photo into the same folder, next to it. Its size and time are taken now
    /// (sidecars aren't scanned), from directory metadata only.
    /// </summary>
    /// <param name="suffix">What the sidecar's name adds to the photo's, e.g. ".json".</param>
    private static CleanupFailure? TryMoveSidecar(string path, string suffix, ManifestEntry photo, BatchManifest manifest)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return new(path, CleanupFailureReason.NotFound, "The sidecar no longer exists.");

        return WriteAheadAndMove(path, new ManifestEntry(photo.RelativePath + suffix, info.Length, info.LastWriteTimeUtc, SidecarOf: photo.RelativePath), manifest);
    }

    /// <summary>Records <paramref name="entry"/> in the manifest, then moves the file. Null on success.</summary>
    private static CleanupFailure? WriteAheadAndMove(string source, ManifestEntry entry, BatchManifest manifest)
    {
        // Write-ahead: the manifest records the move before it happens. If it can't be written, the file stays put.
        manifest.Entries.Add(entry);
        try
        {
            manifest.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            manifest.Entries.Remove(entry);
            return new(source, CleanupFailureReason.IoError, $"Couldn't write the manifest, so the file wasn't moved: {ex.Message}");
        }

        try
        {
            var destination = manifest.ReviewPathOf(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            NoCopyMove.Move(source, destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            manifest.Entries.Remove(entry);
            manifest.TrySave(); // if this fails, the stale entry is harmless: undo sees the original unchanged and drops it
            return new(source, IoReason(ex), ex.Message);
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
        // Photos only: "moved 3 photos (12 MB)" shouldn't count the few KB of metadata that went with them.
        var list = manifests.ToList();
        var photos = list.SelectMany(m => m.Entries).Where(e => !e.IsSidecar).ToList();
        return new CleanupBatch(id, createdUtc, list.Select(m => m.ManifestPath).ToList(), photos.Count, photos.Sum(e => e.SizeBytes));
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
