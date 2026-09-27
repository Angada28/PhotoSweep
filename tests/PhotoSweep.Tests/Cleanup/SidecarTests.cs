using System.Text.Json.Nodes;
using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Tests.Scanning;
using static PhotoSweep.Tests.Cleanup.Groups;

namespace PhotoSweep.Tests.Cleanup;

// Google Takeout sidecars ("IMG_1.jpg.json", "IMG_1.jpg.supplemental-metadata.json") moving with their photos, with the
// real ReviewFolder on real files in a temp folder.
public class SidecarTests : IDisposable
{
    private const string BatchName = "2026-09-26 10.00.00";

    private readonly TempPhotoFolder _temp = new();
    private readonly ReviewFolder _review = new(new FixedTime(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero)));

    public void Dispose() => _temp.Dispose();

    private string ReviewDir => Path.Combine(_temp.Root, ScanOptions.ReviewFolderName);

    private string BatchDir => Path.Combine(ReviewDir, BatchName);

    private ScannedFile Add(string relativePath, params byte[] bytes) => Scanned(_temp.AddBytes(relativePath, bytes));

    private string AddSidecar(string relativePath, string text = "{}") => _temp.AddBytes(relativePath, System.Text.Encoding.UTF8.GetBytes(text));

    private CleanupResult MoveCopy(ScannedFile keeper, ScannedFile copy)
    {
        var validation = CleanupPlan.Validate([Of(keeper, copy)], [copy.Path], [_temp.Root]);
        Assert.True(validation.IsValid);
        return _review.MoveToReview(validation.Plan);
    }

    [Fact]
    public void Finds_only_the_two_takeout_names()
    {
        var photo = _temp.AddBytes(@"sub\IMG_1.jpg", [1]);
        var json = AddSidecar(@"sub\IMG_1.jpg.json");
        var supplemental = AddSidecar(@"sub\IMG_1.jpg.supplemental-metadata.json");
        AddSidecar(@"sub\IMG_1.json");         // not "<file>.json": the extension is missing
        AddSidecar(@"sub\IMG_1.jpg(1).json");  // Takeout's numbering quirk: not matched, rather than guessed

        Assert.Equal([json, supplemental], Sidecars.Find(photo));
        Assert.Empty(Sidecars.Find(_temp.AddBytes("IMG_2.jpg", [2])));
    }

    [Fact]
    public void Both_sidecar_names_move_with_their_photo_and_come_back_on_undo()
    {
        var keeper = Add("IMG_1.jpg", 1);
        var copy = Add(@"backup\IMG_1.jpg", 1);
        var json = AddSidecar(@"backup\IMG_1.jpg.json", "json");
        var supplemental = AddSidecar(@"backup\IMG_1.jpg.supplemental-metadata.json", "supplemental");
        var keeperSidecar = AddSidecar("IMG_1.jpg.json", "keeper");

        var result = MoveCopy(keeper, copy);

        Assert.Empty(result.Failures);
        Assert.Empty(result.SidecarFailures);
        Assert.Equal(2, result.SidecarsMoved);
        Assert.False(File.Exists(json));
        Assert.False(File.Exists(supplemental));
        Assert.Equal("json", File.ReadAllText(Path.Combine(BatchDir, @"backup\IMG_1.jpg.json")));
        Assert.Equal("supplemental", File.ReadAllText(Path.Combine(BatchDir, @"backup\IMG_1.jpg.supplemental-metadata.json")));
        Assert.True(File.Exists(keeperSidecar)); // the kept photo's sidecar stays with it
        Assert.Equal((1, 1L), (result.Batch.FileCount, result.Batch.TotalBytes)); // counts photos only

        var manifest = JsonNode.Parse(File.ReadAllText(Assert.Single(result.Batch.ManifestPaths)))!;
        Assert.Equal(
            [(@"backup\IMG_1.jpg", null), (@"backup\IMG_1.jpg.json", @"backup\IMG_1.jpg"), (@"backup\IMG_1.jpg.supplemental-metadata.json", @"backup\IMG_1.jpg")],
            manifest["Files"]!.AsArray().Select(e => ((string)e!["RelativePath"]!, (string?)e["SidecarOf"])));

        var undo = _review.Undo(result.Batch);

        Assert.Empty(undo.Failures);
        Assert.Empty(undo.SidecarFailures);
        Assert.Equal(2, undo.SidecarsRestored);
        Assert.Equal("json", File.ReadAllText(json));
        Assert.Equal("supplemental", File.ReadAllText(supplemental));
        Assert.False(Directory.Exists(ReviewDir));
    }

    [Fact]
    public void A_photo_without_a_sidecar_moves_as_before()
    {
        var result = MoveCopy(Add("a.jpg", 1), Add("b.jpg", 1));

        Assert.Single(result.Moved);
        Assert.Equal(0, result.SidecarsMoved);
        Assert.Empty(result.SidecarFailures);
    }

    [Fact]
    public void A_sidecar_never_moves_when_its_photo_does_not()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var sidecar = AddSidecar("b.jpg.json");

        CleanupResult result;
        using (new FileStream(copy.Path, FileMode.Open, FileAccess.Read, FileShare.None)) // the photo is in use
            result = MoveCopy(keeper, copy);

        Assert.Equal(CleanupFailureReason.InUse, Assert.Single(result.Failures).Reason);
        Assert.Equal(0, result.SidecarsMoved);
        Assert.Empty(result.SidecarFailures);
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public void A_sidecar_that_cannot_move_does_not_block_its_photo()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var locked = AddSidecar("b.jpg.json");
        var fine = AddSidecar("b.jpg.supplemental-metadata.json");

        CleanupResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = MoveCopy(keeper, copy);

        Assert.Empty(result.Failures);
        Assert.Equal(copy.Path, Assert.Single(result.Moved).OriginalPath);
        var failure = Assert.Single(result.SidecarFailures);
        Assert.Equal((locked, CleanupFailureReason.InUse), (failure.Path, failure.Reason));
        Assert.Equal(1, result.SidecarsMoved);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(fine));

        // The failed sidecar isn't in the manifest, so undo doesn't go looking for it.
        var undo = _review.Undo(result.Batch);
        Assert.Empty(undo.Failures);
        Assert.Empty(undo.SidecarFailures);
        Assert.True(File.Exists(copy.Path));
        Assert.True(File.Exists(fine));
    }

    [Fact]
    public void A_photo_put_back_under_a_new_name_takes_its_sidecar_with_it()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        AddSidecar("b.jpg.json", "moved");
        var moved = MoveCopy(keeper, copy);
        _temp.AddBytes("b.jpg", [42]); // a new photo now lives at the old name

        var undo = _review.Undo(moved.Batch);

        Assert.Equal(Path.Combine(_temp.Root, "b (2).jpg"), Assert.Single(undo.Restored).RestoredPath);
        Assert.Equal("moved", File.ReadAllText(Path.Combine(_temp.Root, "b (2).jpg.json")));
        Assert.False(File.Exists(Path.Combine(_temp.Root, "b.jpg.json")));
    }

    [Fact]
    public void Undo_never_overwrites_a_sidecar_that_has_appeared_since()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var sidecar = AddSidecar("b.jpg.json", "moved");
        var moved = MoveCopy(keeper, copy);
        AddSidecar("b.jpg.json", "new"); // something wrote a new sidecar at the old place

        var undo = _review.Undo(moved.Batch);

        Assert.Empty(undo.SidecarFailures);
        Assert.Equal(copy.Path, Assert.Single(undo.Restored).RestoredPath);
        Assert.Equal("new", File.ReadAllText(sidecar));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(_temp.Root, "b.jpg (2).json")));
    }

    [Fact]
    public void A_sidecar_waits_while_its_photo_cannot_be_put_back_then_follows_it()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var sidecar = AddSidecar("b.jpg.json");
        var moved = MoveCopy(keeper, copy);

        UndoResult first;
        using (new FileStream(moved.Moved[0].ReviewPath, FileMode.Open, FileAccess.Read, FileShare.None))
            first = _review.Undo(moved.Batch);

        Assert.Equal(CleanupFailureReason.InUse, Assert.Single(first.Failures).Reason);
        Assert.Equal(0, first.SidecarsRestored);
        Assert.False(File.Exists(sidecar)); // still in the review folder, with its photo

        var second = _review.Undo(Assert.Single(_review.FindBatches([_temp.Root])));

        Assert.Single(second.Restored);
        Assert.Equal(1, second.SidecarsRestored);
        Assert.True(File.Exists(sidecar));
        Assert.False(Directory.Exists(ReviewDir));
    }

    [Fact]
    public void A_sidecar_that_could_not_follow_its_photo_is_put_back_by_the_next_undo()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var sidecar = AddSidecar("b.jpg.json");
        var moved = MoveCopy(keeper, copy);
        var reviewSidecar = Path.Combine(BatchDir, "b.jpg.json");

        UndoResult first;
        using (new FileStream(reviewSidecar, FileMode.Open, FileAccess.Read, FileShare.None))
            first = _review.Undo(moved.Batch);

        Assert.Empty(first.Failures);
        Assert.Equal(copy.Path, Assert.Single(first.Restored).RestoredPath);
        Assert.Equal((sidecar, CleanupFailureReason.InUse), (Assert.Single(first.SidecarFailures).Path, first.SidecarFailures[0].Reason));
        var remaining = Assert.Single(_review.FindBatches([_temp.Root]));
        Assert.Equal(0, remaining.FileCount); // no photos left in it, just the sidecar

        var second = _review.Undo(remaining);

        Assert.Equal(1, second.SidecarsRestored);
        Assert.True(File.Exists(sidecar));
        Assert.False(Directory.Exists(ReviewDir));
    }

    [Fact]
    public void A_sidecar_stays_in_the_review_folder_when_its_photo_was_emptied_out_of_it()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var sidecar = AddSidecar("b.jpg.json");
        var moved = MoveCopy(keeper, copy);
        File.Delete(moved.Moved[0].ReviewPath); // the user deleted the photo from the review folder

        var undo = _review.Undo(moved.Batch);

        Assert.Equal(CleanupFailureReason.NotFound, Assert.Single(undo.Failures).Reason);
        Assert.Equal(sidecar, Assert.Single(undo.SidecarFailures).Path);
        Assert.False(File.Exists(sidecar)); // not put back without its photo
        Assert.True(File.Exists(Path.Combine(BatchDir, "b.jpg.json")));
        Assert.Empty(_review.FindBatches([_temp.Root])); // nothing left to undo; the leftover sidecar is the user's to delete
    }

    [Fact]
    public void A_manifest_written_before_sidecars_existed_still_undoes()
    {
        var keeper = Add("a.jpg", 1);
        var copy = Add("b.jpg", 1);
        var moved = MoveCopy(keeper, copy);
        var manifestPath = Assert.Single(moved.Batch.ManifestPaths);
        var json = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        foreach (var entry in json["Files"]!.AsArray())
            entry!.AsObject().Remove("SidecarOf");
        File.WriteAllText(manifestPath, json.ToJsonString());

        var undo = new ReviewFolder().Undo(Assert.Single(new ReviewFolder().FindBatches([_temp.Root])));

        Assert.Empty(undo.Failures);
        Assert.Equal(copy.Path, Assert.Single(undo.Restored).RestoredPath);
    }
}
