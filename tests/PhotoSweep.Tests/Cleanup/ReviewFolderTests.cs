using System.Text.Json;
using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Tests.Scanning;
using static PhotoSweep.Tests.Cleanup.Groups;

namespace PhotoSweep.Tests.Cleanup;

// Real files in a temp folder. "Scanning" is simulated by Groups.Scanned, which records size and last-write time as
// they are at that moment; the file can then be changed or deleted before the clean-up runs.
public class ReviewFolderTests : IDisposable
{
    private const string BatchName = "2026-09-25 14.03.07";

    private readonly TempPhotoFolder _temp = new();
    private readonly ReviewFolder _review = new(new FixedTime(new DateTimeOffset(2026, 9, 25, 14, 3, 7, TimeSpan.Zero)));

    public void Dispose() => _temp.Dispose();

    private string ReviewDir => Path.Combine(_temp.Root, ScanOptions.ReviewFolderName);

    private string BatchDir => Path.Combine(ReviewDir, BatchName);

    private ScannedFile Add(string relativePath, params byte[] bytes) => Scanned(_temp.AddBytes(relativePath, bytes));

    private CleanupPlan Plan(IReadOnlyList<PhotoGroup> groups, params string[] remove)
    {
        var validation = CleanupPlan.Validate(groups, remove, [_temp.Root, _temp.Outside]);
        Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
        return validation.Plan;
    }

    private static string[] ManifestEntries(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.GetProperty("Files").EnumerateArray().Select(e => e.GetProperty("RelativePath").GetString()!).ToArray();
    }

    [Fact]
    public void Moves_keep_subfolders_and_the_manifest_lists_every_move()
    {
        var keeper = Add("a.jpg", 1);
        var nested = Add(@"sub\deep\a.jpg", 1);
        var copy = Add("a (1).jpg", 1);

        var result = _review.MoveToReview(Plan([Of(keeper, nested, copy)], nested.Path, copy.Path));

        Assert.Empty(result.Failures);
        Assert.Equal([Path.Combine(BatchDir, @"sub\deep\a.jpg"), Path.Combine(BatchDir, "a (1).jpg")], result.Moved.Select(m => m.ReviewPath));
        Assert.All(result.Moved, m => Assert.True(File.Exists(m.ReviewPath)));
        Assert.False(File.Exists(nested.Path));
        Assert.False(File.Exists(copy.Path));
        Assert.True(File.Exists(keeper.Path));

        var manifest = Assert.Single(result.Batch.ManifestPaths);
        Assert.Equal(Path.Combine(BatchDir, "manifest.json"), manifest);
        Assert.Equal([@"sub\deep\a.jpg", "a (1).jpg"], ManifestEntries(manifest));
        Assert.Equal((2, 2L), (result.Batch.FileCount, result.Batch.TotalBytes));
    }

    [Fact]
    public void One_failed_move_does_not_stop_the_rest_and_is_not_in_the_manifest()
    {
        var keeper = Add("a.jpg", 1);
        var locked = Add("locked.jpg", 1);
        var gone = Add("gone.jpg", 1);
        var fine = Add("fine.jpg", 1);
        File.Delete(gone.Path); // deleted after the scan

        CleanupResult result;
        using (new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.None)) // another app has it open
            result = _review.MoveToReview(Plan([Of(keeper, locked, gone, fine)], locked.Path, gone.Path, fine.Path));

        Assert.Equal([fine.Path], result.Moved.Select(m => m.OriginalPath));
        Assert.Equal(
            [(locked.Path, CleanupFailureReason.InUse), (gone.Path, CleanupFailureReason.NotFound)],
            result.Failures.Select(f => (f.Path, f.Reason)));
        Assert.True(File.Exists(locked.Path));
        Assert.Equal(["fine.jpg"], ManifestEntries(Assert.Single(result.Batch.ManifestPaths)));
    }

    [Fact]
    public void A_file_is_not_moved_if_its_manifest_entry_cannot_be_written_first()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        _temp.AddBytes(ScanOptions.ReviewFolderName, [0]); // a file where the review folder should go

        var result = _review.MoveToReview(Plan([Of(keeper, copy)], copy.Path));

        var failure = Assert.Single(result.Failures);
        Assert.Equal(CleanupFailureReason.IoError, failure.Reason);
        Assert.StartsWith("Couldn't write the manifest", failure.Message);
        Assert.True(File.Exists(copy.Path));
        Assert.Empty(result.Moved);
        Assert.Empty(result.Batch.ManifestPaths);
        Assert.True(File.Exists(Path.Combine(_temp.Root, ScanOptions.ReviewFolderName))); // the blocking file is untouched
    }

    [Fact]
    public void Online_only_file_is_moved_and_restored_without_being_read()
    {
        var keeper = Add("a.jpg", 1);
        var cloudPath = _temp.AddBytes(@"OneDrive\a.jpg", [1]);
        File.SetAttributes(cloudPath, FileAttributes.Offline);
        var cloud = Scanned(cloudPath);

        // While this handle is open Windows refuses every other open for reading, but still allows a rename. So the
        // moves below can only succeed if nothing reads the file; a copy fallback would throw a sharing violation.
        using var guard = new FileStream(cloudPath, FileMode.Open, FileAccess.Read, FileShare.Delete);
        Assert.Throws<IOException>(() => File.OpenRead(cloudPath).Dispose());

        var result = _review.MoveToReview(Plan([Of(keeper, cloud)], cloud.Path));

        Assert.Empty(result.Failures);
        var reviewPath = Assert.Single(result.Moved).ReviewPath;
        Assert.True(File.GetAttributes(reviewPath).HasFlag(FileAttributes.Offline));

        var undo = _review.Undo(result.Batch);

        Assert.Empty(undo.Failures);
        Assert.True(File.GetAttributes(cloudPath).HasFlag(FileAttributes.Offline));
        Assert.Throws<IOException>(() => File.OpenRead(cloudPath).Dispose()); // still the same guarded file
    }

    [Fact]
    public void Group_is_skipped_if_its_keeper_was_deleted_after_the_scan()
    {
        var a = Add("a.jpg", 1);
        var aCopy = Add("a copy.jpg", 1);
        var b = Add("b.jpg", 2);
        var bCopy = Add("b copy.jpg", 2);
        var plan = Plan([Of(a, aCopy), Of(b, bCopy)], aCopy.Path, bCopy.Path);
        File.Delete(a.Path);

        var result = _review.MoveToReview(plan);

        var failure = Assert.Single(result.Failures);
        Assert.Equal((aCopy.Path, CleanupFailureReason.KeptCopyMissing), (failure.Path, failure.Reason));
        Assert.True(File.Exists(aCopy.Path)); // the last copy stays where it is
        Assert.Equal([bCopy.Path], result.Moved.Select(m => m.OriginalPath)); // other groups carry on
    }

    [Fact]
    public void An_edited_keeper_does_not_count_as_a_kept_copy_but_another_unchanged_one_does()
    {
        var a = Add("a.jpg", 1);
        var aCopy = Add("a copy.jpg", 1);
        var b = Add("b.jpg", 2);
        var bKept = Add(@"backup\b.jpg", 2);
        var bCopy = Add("b copy.jpg", 2);
        var plan = Plan([Of(a, aCopy), Of(b, bKept, bCopy)], aCopy.Path, bCopy.Path);
        File.AppendAllBytes(a.Path, [9]);
        File.Delete(b.Path);

        var result = _review.MoveToReview(plan);

        Assert.Equal(CleanupFailureReason.KeptCopyMissing, Assert.Single(result.Failures).Reason);
        Assert.Equal([bCopy.Path], result.Moved.Select(m => m.OriginalPath)); // backup\b.jpg is still there
    }

    [Fact]
    public void Selected_files_that_changed_after_the_scan_are_skipped()
    {
        var keeper = Add("a.jpg", 1);
        var edited = Add("edited.jpg", 1);
        var touched = Add("touched.jpg", 1);
        var fine = Add("fine.jpg", 1);
        var plan = Plan([Of(keeper, edited, touched, fine)], edited.Path, touched.Path, fine.Path);
        File.AppendAllBytes(edited.Path, [9]);                                        // size changed
        File.SetLastWriteTimeUtc(touched.Path, touched.LastWriteUtc.AddMinutes(1)); // only the timestamp changed

        var result = _review.MoveToReview(plan);

        Assert.All(result.Failures, f => Assert.Equal(CleanupFailureReason.ChangedSinceScan, f.Reason));
        Assert.Equal([edited.Path, touched.Path], result.Failures.Select(f => f.Path));
        Assert.True(File.Exists(edited.Path));
        Assert.True(File.Exists(touched.Path));
        Assert.Equal([fine.Path], result.Moved.Select(m => m.OriginalPath));
    }

    [Fact]
    public void Two_clean_ups_in_the_same_second_get_separate_folders()
    {
        var a = Add("a.jpg", 1);
        var aCopy = Add("a copy.jpg", 1);
        var b = Add("b.jpg", 2);
        var bCopy = Add("b copy.jpg", 2);

        var first = _review.MoveToReview(Plan([Of(a, aCopy)], aCopy.Path));
        var second = _review.MoveToReview(Plan([Of(b, bCopy)], bCopy.Path));

        Assert.Equal([BatchName, BatchName + " (2)"], Directory.GetDirectories(ReviewDir).Select(Path.GetFileName).Order());
        Assert.NotEqual(first.Batch.Id, second.Batch.Id);
    }

    [Fact]
    public void Undo_restores_every_file_and_removes_the_empty_review_folder()
    {
        var keeper = Add("a.jpg", 1);
        var nested = Add(@"sub\deep\a.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        var moved = _review.MoveToReview(Plan([Of(keeper, nested, copy)], nested.Path, copy.Path));

        var undo = _review.Undo(moved.Batch);

        Assert.Empty(undo.Failures);
        Assert.Equal([nested.Path, copy.Path], undo.Restored.Select(r => r.RestoredPath));
        Assert.All(undo.Restored, r => Assert.False(r.AlternateName));
        Assert.True(File.Exists(nested.Path));
        Assert.True(File.Exists(copy.Path));
        Assert.False(Directory.Exists(ReviewDir));
        Assert.Empty(_review.FindBatches([_temp.Root]));
    }

    [Fact]
    public void Undo_never_overwrites_a_file_that_has_appeared_at_the_original_path()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        var moved = _review.MoveToReview(Plan([Of(keeper, copy)], copy.Path));
        _temp.AddBytes("a copy.jpg", [42]);     // a new file where the moved one was
        _temp.AddBytes("a copy (2).jpg", [43]); // and the first alternate name is taken too

        var undo = _review.Undo(moved.Batch);

        var restored = Assert.Single(undo.Restored);
        Assert.True(restored.AlternateName);
        Assert.Equal(Path.Combine(_temp.Root, "a copy (3).jpg"), restored.RestoredPath);
        Assert.Equal([1], File.ReadAllBytes(restored.RestoredPath));
        Assert.Equal([42], File.ReadAllBytes(copy.Path));
        Assert.Equal([43], File.ReadAllBytes(Path.Combine(_temp.Root, "a copy (2).jpg")));
    }

    [Fact]
    public void Undo_quietly_drops_an_entry_whose_move_never_happened()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        var moved = _review.MoveToReview(Plan([Of(keeper, copy)], copy.Path));
        // A crash after the write-ahead entry but before the move looks like this: listed, but still at the original path.
        File.Move(moved.Moved[0].ReviewPath, copy.Path);

        var undo = _review.Undo(moved.Batch);

        Assert.Empty(undo.Failures);
        Assert.Empty(undo.Restored);
        Assert.True(File.Exists(copy.Path));
        Assert.False(File.Exists(Path.Combine(_temp.Root, "a copy (2).jpg")));
        Assert.False(Directory.Exists(ReviewDir));
    }

    [Fact]
    public void Undo_reports_files_emptied_out_of_the_review_folder_and_restores_the_rest()
    {
        var keeper = Add("a.jpg", 1);
        var emptied = Add("emptied.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        var moved = _review.MoveToReview(Plan([Of(keeper, emptied, copy)], emptied.Path, copy.Path));
        File.Delete(moved.Moved[0].ReviewPath); // the user emptied it from the review folder

        var undo = _review.Undo(moved.Batch);

        var failure = Assert.Single(undo.Failures);
        Assert.Equal((emptied.Path, CleanupFailureReason.NotFound), (failure.Path, failure.Reason));
        Assert.Equal([copy.Path], undo.Restored.Select(r => r.RestoredPath));
        Assert.Empty(_review.FindBatches([_temp.Root])); // nothing left that undo could ever bring back
    }

    [Fact]
    public void A_partly_failed_undo_can_be_run_again()
    {
        var keeper = Add("a.jpg", 1);
        var locked = Add("locked.jpg", 1);
        var copy = Add("a copy.jpg", 1);
        var moved = _review.MoveToReview(Plan([Of(keeper, locked, copy)], locked.Path, copy.Path));

        UndoResult first;
        using (new FileStream(moved.Moved[0].ReviewPath, FileMode.Open, FileAccess.Read, FileShare.None))
            first = _review.Undo(moved.Batch);

        Assert.Equal((locked.Path, CleanupFailureReason.InUse), (Assert.Single(first.Failures).Path, first.Failures[0].Reason));
        Assert.Equal([copy.Path], first.Restored.Select(r => r.RestoredPath));
        var remaining = Assert.Single(_review.FindBatches([_temp.Root]));
        Assert.Equal(1, remaining.FileCount);

        var second = _review.Undo(remaining);

        Assert.Empty(second.Failures);
        Assert.Equal([locked.Path], second.Restored.Select(r => r.RestoredPath));
        Assert.False(Directory.Exists(ReviewDir));
    }

    [Fact]
    public void A_clean_up_across_two_roots_is_found_and_undone_as_one_batch()
    {
        var keeper = Add("a.jpg", 1);
        var here = Add(@"sub\a.jpg", 1);
        var otherPath = Path.Combine(_temp.Outside, "a.jpg");
        File.WriteAllBytes(otherPath, [1]);
        var other = Scanned(otherPath);
        var moved = _review.MoveToReview(Plan([Of(keeper, here, other)], here.Path, other.Path));

        Assert.Equal(2, moved.Batch.ManifestPaths.Count); // one per root, each in that root's own review folder
        Assert.True(File.Exists(Path.Combine(_temp.Outside, ScanOptions.ReviewFolderName, BatchName, "a.jpg")));

        var found = Assert.Single(new ReviewFolder().FindBatches([_temp.Root, _temp.Outside])); // fresh instance: read from disk
        Assert.Equal(moved.Batch.Id, found.Id);
        Assert.Equal(2, found.FileCount);

        var undo = new ReviewFolder().Undo(found);

        Assert.Empty(undo.Failures);
        Assert.True(File.Exists(here.Path));
        Assert.True(File.Exists(otherPath));
    }

    [Fact]
    public async Task Removed_files_disappear_from_the_next_scan_and_come_back_after_undo()
    {
        _temp.AddPhoto("coffee.jpg");
        _temp.AddPhoto("coffee_q50.jpg");
        _temp.AddPhoto("chelsea.jpg");
        _temp.AddPhoto("chelsea.jpg", @"backup\chelsea.jpg");
        var options = new ScanOptions { Folders = [_temp.Root] };
        Task<ScanResult> Scan() => new PhotoScanner(ScanCache.Load(_temp.CachePath)).ScanAsync(options);

        var groups = DuplicateGrouper.Group(await Scan(), MatchLevel.SamePhoto);
        Assert.Equal(2, groups.Count);
        var nonKeepers = groups.SelectMany(g => g.Members.Skip(1)).Select(m => m.File.Path).ToList();
        var validation = CleanupPlan.Validate(groups, nonKeepers, options.Folders);
        var moved = _review.MoveToReview(validation.Plan!);

        Assert.Equal(2, moved.Moved.Count);
        Assert.Empty(DuplicateGrouper.Group(await Scan(), MatchLevel.SamePhoto)); // the review folder isn't scanned

        _review.Undo(moved.Batch);

        Assert.Equal(2, DuplicateGrouper.Group(await Scan(), MatchLevel.SamePhoto).Count);
    }
}
