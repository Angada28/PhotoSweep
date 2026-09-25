using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using PhotoSweep.Tests.Cleanup;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

// Moving to the review folder and undo, driven through the results page with a fake engine. Every test installs the
// inline synchronization context, so a finished fake call has been fully handled by the page on the next line.
public class ResultsCleanupTests
{
    private const long MB = 1024 * 1024;

    private const string A = @"C:\Photos\a.jpg";
    private const string ACopy = @"C:\Photos\a copy.jpg";
    private const string A2 = @"C:\Photos\a (2).jpg";
    private const string B = @"C:\Photos\b.jpg";
    private const string BSmall = @"C:\Photos\b small.jpg";

    private const string UndoNote = "Undo is available until you leave this page. The files stay in the _PhotoSweep Removed folder either way.";

    // Starts with "a copy", "a (2)" and "b small" selected: 3 photos, 4 MB.
    private static readonly PhotoGroup[] TwoGroups =
    [
        Group(Photo(A, 3 * MB), Photo(ACopy, 1 * MB), Photo(A2, 1 * MB)),
        Group(Photo(B, 5 * MB), Photo(BSmall, 2 * MB)),
    ];

    private readonly FakeScanService _scan = new();
    private readonly FakeCleanupService _cleanup = new();
    private readonly FakeShellService _shell = new();
    private readonly FakeFileAvailability _availability = new();
    private int _backs;

    private ResultsViewModel Results(IReadOnlyList<PhotoGroup>? groups = null, string[]? folders = null)
    {
        groups ??= TwoGroups;
        var request = new ScanRequest(new ScanOptions { Folders = folders ?? [@"C:\Photos"] }, MatchLevel.SamePhoto);
        var scan = new ScanResult(groups.SelectMany(g => g.Members).Select(m => m.File), cacheHits: 0);
        return new ResultsViewModel(new ScanOutcome(request, scan, groups, []), _scan, _cleanup, _shell, _availability,
            () => _backs++, new FixedTime(DateTimeOffset.UnixEpoch));
    }

    private static PhotoViewModel PhotoAt(ResultsViewModel results, string path) =>
        results.Groups.SelectMany(g => g.Photos).Single(p => p.File.Path == path);

    private static IEnumerable<string> Shown(ResultsViewModel results) => results.Groups.SelectMany(g => g.Photos).Select(p => p.File.Path);

    private static IEnumerable<string> Selected(ResultsViewModel results) =>
        results.Groups.SelectMany(g => g.Photos).Where(p => p.IsSelected).Select(p => p.File.Path);

    /// <summary>Selects exactly these photos (the keep-one rule still applies).</summary>
    private static void Select(ResultsViewModel results, params string[] paths)
    {
        results.ClearSelectionCommand.Execute(null);
        foreach (var path in paths)
            PhotoAt(results, path).ToggleCommand.Execute(null);
    }

    /// <summary>Clicks Move, then Move again on the confirmation. The fake leaves the move pending.</summary>
    private MoveCall StartMove(ResultsViewModel results)
    {
        results.MoveCommand.Execute(null);
        Assert.True(results.IsConfirming, results.ReportTitle);
        results.ConfirmMoveCommand.Execute(null);
        return _cleanup.LastMove;
    }

    // ---- Confirmation ----

    [Fact]
    public void Move_is_off_until_something_is_selected()
    {
        var results = Results();
        results.ClearSelectionCommand.Execute(null);

        Assert.False(results.MoveCommand.CanExecute(null));

        PhotoAt(results, ACopy).ToggleCommand.Execute(null);

        Assert.True(results.MoveCommand.CanExecute(null));
    }

    [Fact]
    public void Move_asks_first_and_says_how_many_how_big_and_where()
    {
        var results = Results();

        results.MoveCommand.Execute(null);

        Assert.True(results.IsConfirming);
        Assert.Equal("Move 3 photos (4 MB) to the _PhotoSweep Removed folder? Nothing is deleted, and you can undo this.", results.ConfirmText);
        Assert.StartsWith("They will go into a dated folder", results.ConfirmFoldersHeading);
        Assert.Equal([@"C:\Photos\_PhotoSweep Removed"], results.ConfirmFolders);
        Assert.Equal(new[] { ACopy, A2, BSmall }.Order(), Assert.Single(_cleanup.Validations).Order());
        Assert.Empty(_cleanup.Moves); // nothing moves until confirmed
    }

    [Fact]
    public void Photos_in_two_scanned_folders_go_to_each_folders_own_review_folder()
    {
        var results = Results(
            [Group(Photo(A, MB), Photo(@"D:\Camera\a.jpg", MB))],
            folders: [@"C:\Photos", @"D:\Camera"]);
        Select(results, A);
        Assert.Equal(1, results.SelectedCount);

        results.MoveCommand.Execute(null);

        Assert.Equal("Move 1 photo (1 MB) to the _PhotoSweep Removed folder? Nothing is deleted, and you can undo this.", results.ConfirmText);
        Assert.StartsWith("It will go", results.ConfirmFoldersHeading);
        Assert.Equal([@"C:\Photos\_PhotoSweep Removed"], results.ConfirmFolders);

        PhotoAt(results, A).ToggleCommand.Execute(null);
        PhotoAt(results, @"D:\Camera\a.jpg").ToggleCommand.Execute(null);
        results.MoveCommand.Execute(null);

        Assert.Equal([@"D:\Camera\_PhotoSweep Removed"], results.ConfirmFolders);
    }

    [Fact]
    public void Cancel_at_the_confirmation_moves_nothing()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        results.MoveCommand.Execute(null);

        results.CancelMoveCommand.Execute(null);

        Assert.False(results.IsConfirming);
        Assert.False(results.ConfirmMoveCommand.CanExecute(null));
        results.ConfirmMoveCommand.Execute(null); // even if the view ignored CanExecute
        Assert.Empty(_cleanup.Moves);
        Assert.Equal(3, results.SelectedCount);
    }

    [Fact]
    public void Changing_the_selection_closes_the_confirmation()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        results.MoveCommand.Execute(null);

        PhotoAt(results, A2).ToggleCommand.Execute(null);

        Assert.False(results.IsConfirming); // its "3 photos (4 MB)" no longer describes the selection
        results.ConfirmMoveCommand.Execute(null);
        Assert.Empty(_cleanup.Moves);
    }

    [Fact]
    public void Validation_problems_are_listed_and_nothing_is_moved()
    {
        var results = Results();
        _cleanup.Problems = [new CleanupProblem(CleanupProblemKind.RemovesEveryCopy, B, "All 2 copies of b.jpg are selected; keep at least one.")];

        results.MoveCommand.Execute(null);

        Assert.False(results.IsConfirming);
        Assert.True(results.HasReport);
        Assert.Equal("Nothing was moved", results.ReportTitle);
        Assert.Equal([$"{B}: All 2 copies of b.jpg are selected; keep at least one."], Assert.Single(results.ReportSections).Lines);
        Assert.Empty(_cleanup.Moves);

        results.DismissReportCommand.Execute(null);
        Assert.False(results.HasReport);
    }

    // ---- Moving ----

    [Fact]
    public void Moving_takes_photos_out_of_their_groups_and_updates_the_totals()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy, BSmall);

        var move = StartMove(results);
        Assert.Equal([ACopy, BSmall], move.Paths);
        move.Complete();

        Assert.False(results.IsWorking);
        Assert.Equal([A, A2], Shown(results)); // group b had one photo left, so it's gone
        Assert.Equal("1 group with 1 extra copy", results.Summary);
        Assert.Equal(1 * MB, results.ExtraBytes);
        Assert.Equal("No photos selected", results.SelectionText);
        Assert.False(results.HasReport);
        Assert.True(results.HasUndo);
        Assert.Equal("Moved 2 photos (3 MB) to the review folder.", results.UndoText);
        Assert.Equal(UndoNote, results.UndoNote);
        Assert.Contains("delete the _PhotoSweep Removed folder yourself", results.DeleteNote);
        Assert.Equal("", results.EarlierBatchesText);
    }

    [Fact]
    public void Moving_every_extra_copy_leaves_a_page_that_says_none_are_left()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();

        StartMove(results).Complete();

        Assert.True(results.IsEmpty);
        Assert.Equal("No duplicates left.", results.Summary);
        Assert.Equal("No duplicates left at this strictness.", results.EmptyText);
        Assert.Equal("Moved 3 photos (4 MB) to the review folder.", results.UndoText);
    }

    [Fact]
    public void While_moving_the_page_is_locked_and_the_move_cannot_run_twice()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        results.MoveCommand.Execute(null);

        results.ConfirmMoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null); // double-click
        results.MoveCommand.Execute(null);       // or Move again, ignoring CanExecute
        results.ConfirmMoveCommand.Execute(null);

        var move = Assert.Single(_cleanup.Moves);
        Assert.True(results.IsWorking);
        Assert.True(results.IsBusy);
        Assert.Equal("Moving photos to the review folder…", results.BusyText);
        Assert.False(results.CanChangeStrictness);
        Assert.False(results.MoveCommand.CanExecute(null));
        Assert.False(results.ConfirmMoveCommand.CanExecute(null));
        Assert.False(results.BackCommand.CanExecute(null));
        Assert.False(results.SelectSuggestedCommand.CanExecute(null));
        Assert.False(results.ClearSelectionCommand.CanExecute(null));

        move.Complete();

        Assert.False(results.IsBusy);
        Assert.True(results.BackCommand.CanExecute(null));
        Assert.True(results.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void A_photo_open_in_another_app_stays_selected_so_Move_can_be_tried_again()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();

        StartMove(results).Complete((ACopy, CleanupFailureReason.InUse));

        Assert.Equal([A, ACopy], Shown(results));
        Assert.Equal([ACopy], Selected(results));
        Assert.Equal("1 photo couldn't be moved", results.ReportTitle);
        var section = Assert.Single(results.ReportSections);
        Assert.Contains("Open in another app", section.Heading);
        Assert.Contains("still selected", section.Heading);
        Assert.Equal([ACopy], section.Lines);
        Assert.Equal("Moved 2 photos (3 MB) to the review folder.", results.UndoText);

        // The other app lets go; Move again.
        Assert.True(results.MoveCommand.CanExecute(null));
        StartMove(results).Complete();

        Assert.Equal([ACopy], _cleanup.Validations[^1]);
        Assert.True(results.IsEmpty);
        Assert.Equal("Moved 1 photo (1 MB) to the review folder.", results.UndoText);
        Assert.Equal("1 earlier clean-up can be undone after this one.", results.EarlierBatchesText);
    }

    [Fact]
    public void Photos_that_changed_went_missing_or_cannot_be_moved_are_deselected()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(
        [
            Group(Photo(A, 3 * MB), Photo(ACopy, 1 * MB), Photo(A2, 1 * MB), Photo(@"C:\Photos\a (3).jpg", 1 * MB)),
            Group(Photo(B, 5 * MB), Photo(BSmall, 2 * MB)),
        ]);

        StartMove(results).Complete(
            (ACopy, CleanupFailureReason.ChangedSinceScan),
            (A2, CleanupFailureReason.NotFound),
            (BSmall, CleanupFailureReason.KeptCopyMissing),
            (@"C:\Photos\a (3).jpg", CleanupFailureReason.IoError));

        Assert.Equal(6, Shown(results).Count()); // all failed, so all stay...
        Assert.Empty(Selected(results));         // ...but none is left selected to fail the same way again
        Assert.False(results.HasUndo);
        Assert.Equal("4 photos couldn't be moved", results.ReportTitle);
        Assert.Equal(2, results.ReportSections.Count);

        var rescan = results.ReportSections[0];
        Assert.Contains("Scan again to review them", rescan.Heading);
        Assert.Equal(
        [
            $"{ACopy}: it has changed since the scan.",
            $"{A2}: it's no longer there.",
            $"{BSmall}: the copy being kept is missing or has changed, so moving this one could lose the photo.",
        ], rescan.Lines);

        var other = results.ReportSections[1];
        Assert.Contains("deselected", other.Heading);
        Assert.Equal([@"C:\Photos\a (3).jpg: engine message for IoError"], other.Lines);
    }

    [Fact]
    public void An_unexpected_error_while_moving_is_shown_and_the_page_carries_on()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();

        StartMove(results).Fail(new InvalidOperationException("disk on fire"));

        Assert.Contains("disk on fire", results.ErrorMessage);
        Assert.Contains("scan again", results.ErrorMessage);
        Assert.False(results.IsWorking);
        Assert.Equal(5, Shown(results).Count());
        Assert.False(results.HasUndo);
        Assert.True(results.MoveCommand.CanExecute(null));
    }

    [Fact]
    public void Moved_photos_stay_out_after_changing_strictness()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy, BSmall);
        StartMove(results).Complete();

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Exact);
        _scan.LastGrouping.Complete(TwoGroups); // the grouper works from the scan, which still lists the moved photos

        Assert.Equal([A, A2], Shown(results));
        Assert.Equal([A2], Selected(results)); // the policy's suggestion, applied to what's left

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.SamePhoto);
        Assert.Equal([A, A2], Shown(results));
    }

    [Fact]
    public void When_the_keeper_is_moved_the_best_copy_left_stands_in_and_nothing_is_suggested()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, A); // keep both copies, move the suggested keeper

        StartMove(results).Complete();

        var group = results.Groups.Single(g => g.Photos.Any(p => p.File.Path == ACopy));
        Assert.Equal([ACopy, A2], group.Photos.Select(p => p.File.Path));
        Assert.True(group.Photos[0].IsKeeper);
        Assert.Equal("The suggested keeper was moved; this is the best copy left", group.KeeperReason);
        Assert.False(group.HasSuggestions);

        results.SelectSuggestedCommand.Execute(null);

        Assert.Equal([BSmall], Selected(results)); // only the untouched group gets suggestions
    }

    // ---- Undo ----

    [Fact]
    public void Undo_puts_photos_back_in_their_groups_with_their_selection()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy, BSmall);
        var move = StartMove(results);
        var moved = move.Complete();

        results.UndoCommand.Execute(null);
        var undo = _cleanup.LastUndo;
        Assert.Same(moved.Batch, undo.Batch);
        Assert.Equal("Putting photos back…", results.BusyText);
        undo.RestoreAll(moved);

        Assert.Equal([A, ACopy, A2, B, BSmall], Shown(results)); // same order as before: equal savings, then keeper path
        Assert.Equal([ACopy, BSmall], Selected(results));
        Assert.Equal("2 photos selected · 3 MB", results.SelectionText);
        Assert.Equal("2 groups with 3 extra copies", results.Summary);
        Assert.False(results.HasUndo);
        Assert.False(results.UndoCommand.CanExecute(null));
        Assert.Equal("Put back 2 photos.", results.ReportTitle);
        Assert.Empty(results.ReportSections);
    }

    [Fact]
    public void Undo_reports_photos_that_came_back_under_a_new_name_and_they_can_be_moved_again()
    {
        using var sync = InlineSynchronizationContext.Install();
        const string renamed = @"C:\Photos\b small (2).jpg";
        var results = Results();
        Select(results, BSmall);
        var moved = StartMove(results).Complete();

        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.RestoreAll(moved, (BSmall, renamed));

        var photo = PhotoAt(results, renamed);
        Assert.Equal("b small (2).jpg", photo.FileName);
        Assert.True(photo.IsSelected);
        var section = Assert.Single(results.ReportSections);
        Assert.Contains("new name", section.Heading);
        Assert.Equal([$"{BSmall} → b small (2).jpg"], section.Lines);

        // Its new path is what gets moved (and put back) from now on.
        var again = StartMove(results);
        Assert.Equal([renamed], again.Paths);
        var movedAgain = again.Complete();
        Assert.Equal([A, ACopy, A2], Shown(results));

        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.RestoreAll(movedAgain);

        Assert.Equal([A, ACopy, A2, B, renamed], Shown(results));
    }

    [Fact]
    public void Undo_cannot_run_twice()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var moved = StartMove(results).Complete();

        results.UndoCommand.Execute(null);
        results.UndoCommand.Execute(null);

        var undo = Assert.Single(_cleanup.Undos);
        Assert.True(results.IsWorking);
        Assert.False(results.UndoCommand.CanExecute(null));
        Assert.False(results.OpenReviewFolderCommand.CanExecute(null));
        Assert.False(results.MoveCommand.CanExecute(null));

        undo.RestoreAll(moved);

        Assert.False(results.IsWorking);
    }

    [Fact]
    public void A_partly_failed_undo_keeps_Undo_for_the_photos_still_in_the_review_folder()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy, BSmall);
        var moved = StartMove(results).Complete();

        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.Complete([new RestoredFile(ACopy, ACopy)], new CleanupFailure(BSmall, CleanupFailureReason.InUse, "locked"));

        Assert.Equal([A, ACopy, A2], Shown(results));
        Assert.True(results.HasUndo);
        Assert.Equal("Moved 1 photo (2 MB) to the review folder.", results.UndoText);
        Assert.Equal("Put back 1 photo.", results.ReportTitle);
        var section = Assert.Single(results.ReportSections);
        Assert.Contains("click Undo to try again", section.Heading);
        Assert.Equal([$"{BSmall}: it's open in another app."], section.Lines);

        results.UndoCommand.Execute(null);
        Assert.Same(moved.Batch, _cleanup.LastUndo.Batch); // the same batch; the engine re-reads what's left from its manifest
        _cleanup.LastUndo.Complete([new RestoredFile(BSmall, BSmall)]);

        Assert.Equal([A, ACopy, A2, B, BSmall], Shown(results));
        Assert.False(results.HasUndo);
    }

    [Fact]
    public void Photos_gone_from_the_review_folder_stay_out_and_ones_already_back_in_place_return()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy, BSmall);
        StartMove(results).Complete();

        // "a copy" was deleted from the review folder; "b small" was moved back by hand, so undo had nothing to do for it.
        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.Complete([], new CleanupFailure(ACopy, CleanupFailureReason.NotFound, "No longer in the review folder."));

        Assert.Equal([B, BSmall, A, A2], Shown(results));
        Assert.False(PhotoAt(results, BSmall).IsSelected);
        Assert.False(results.HasUndo);
        Assert.Equal("Nothing was put back.", results.ReportTitle);
        var section = Assert.Single(results.ReportSections);
        Assert.Contains("No longer in the review folder", section.Heading);
        Assert.Equal([$"{ACopy}: No longer in the review folder."], section.Lines);
    }

    [Fact]
    public void Undo_goes_back_one_batch_at_a_time_latest_first()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Select(results, ACopy);
        var first = StartMove(results).Complete();
        Select(results, BSmall);
        var second = StartMove(results).Complete();
        Assert.Equal("Moved 1 photo (2 MB) to the review folder.", results.UndoText);
        Assert.Equal("1 earlier clean-up can be undone after this one.", results.EarlierBatchesText);

        results.UndoCommand.Execute(null);
        Assert.Same(second.Batch, _cleanup.LastUndo.Batch);
        _cleanup.LastUndo.RestoreAll(second);

        Assert.Contains(BSmall, Shown(results));
        Assert.DoesNotContain(ACopy, Shown(results));
        Assert.Equal("Moved 1 photo (1 MB) to the review folder.", results.UndoText);
        Assert.Equal("", results.EarlierBatchesText);

        results.UndoCommand.Execute(null);
        Assert.Same(first.Batch, _cleanup.LastUndo.Batch);
        _cleanup.LastUndo.RestoreAll(first);

        Assert.Equal([A, ACopy, A2, B, BSmall], Shown(results));
        Assert.False(results.HasUndo);
    }

    [Fact]
    public void An_unexpected_error_while_undoing_is_shown_and_Undo_stays_available()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        StartMove(results).Complete();

        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.Fail(new IOException("drive gone"));

        Assert.Contains("drive gone", results.ErrorMessage);
        Assert.False(results.IsWorking);
        Assert.True(results.UndoCommand.CanExecute(null));
    }

    // ---- Review folder and leaving ----

    [Fact]
    public void Open_review_folder_opens_the_latest_batch_folder()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        Assert.False(results.OpenReviewFolderCommand.CanExecute(null));
        var move = StartMove(results);
        move.Complete();

        results.OpenReviewFolderCommand.Execute(null);

        Assert.Equal([move.BatchFolder], _shell.Opened);
        Assert.Equal("", results.ErrorMessage);

        _shell.Succeeds = false;
        results.OpenReviewFolderCommand.Execute(null);

        Assert.Contains("Couldn't open", results.ErrorMessage);
    }

    [Fact]
    public void Back_with_something_to_undo_shows_a_reminder_before_leaving()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        StartMove(results).Complete();

        results.BackCommand.Execute(null);

        Assert.Equal(0, _backs);
        Assert.True(results.IsConfirmingLeave);
        Assert.Equal($"1 clean-up can still be undone. {UndoNote}", results.LeaveText);

        results.StayCommand.Execute(null);

        Assert.False(results.IsConfirmingLeave);
        Assert.Equal(0, _backs);

        results.BackCommand.Execute(null);
        results.LeaveCommand.Execute(null);

        Assert.Equal(1, _backs);
    }

    [Fact]
    public void Back_with_nothing_to_undo_leaves_straight_away()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var moved = StartMove(results).Complete();
        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.RestoreAll(moved);

        results.BackCommand.Execute(null);

        Assert.False(results.IsConfirmingLeave);
        Assert.Equal(1, _backs);
    }
}
