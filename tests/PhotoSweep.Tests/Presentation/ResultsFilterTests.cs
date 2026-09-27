using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using PhotoSweep.Tests.Cleanup;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

// The group-kind badges and filter buttons, and the Takeout sidecar lines in the move/undo reports.
public class ResultsFilterTests
{
    private const string Copy1 = @"C:\Photos\a.jpg";
    private const string Copy2 = @"C:\Photos\a (1).jpg";
    private const string Frame1 = @"C:\Photos\IMG_BURST1.jpg";
    private const string Frame2 = @"C:\Photos\IMG_BURST2.jpg";
    private const string Shot1 = @"C:\Photos\Screenshot_1.png";
    private const string Shot2 = @"C:\Photos\Screenshot_2.png";
    private const string Look1 = @"C:\Photos\x.jpg";
    private const string Look2 = @"C:\Photos\y.jpg";

    private readonly FakeScanService _scan = new();
    private readonly FakeCleanupService _cleanup = new();

    private static ScannedFile Bytes(string path, string sha) => Photo(path) with { Sha256 = sha };

    /// <summary>First member is the keeper; the rest matched as <paramref name="kind"/>.</summary>
    private static PhotoGroup Of(MatchKind kind, params ScannedFile[] files) =>
        new(files.Select((f, i) => new GroupMember(f, i == 0 ? MatchKind.Keeper : kind, null, null)).ToList(), "test");

    // One group of each kind. Only the copy is pre-selected (it's the only byte-identical one).
    private static readonly PhotoGroup[] OneOfEach =
    [
        Of(MatchKind.Identical, Bytes(Copy1, "A"), Bytes(Copy2, "A")),
        Of(MatchKind.Similar, Photo(Frame1), Photo(Frame2)),
        Of(MatchKind.SamePhoto, Photo(Shot1), Photo(Shot2)),
        Of(MatchKind.SamePhoto, Photo(Look1), Photo(Look2)),
    ];

    private ResultsViewModel Results(IReadOnlyList<PhotoGroup>? groups = null)
    {
        groups ??= OneOfEach;
        var request = new ScanRequest(new ScanOptions { Folders = [@"C:\Photos"] }, MatchLevel.SamePhoto);
        var scan = new ScanResult(groups.SelectMany(g => g.Members).Select(m => m.File), cacheHits: 0);
        return new ResultsViewModel(new ScanOutcome(request, scan, groups, []), _scan, _cleanup, new FakeShellService(), new FakeFileAvailability(),
            new FakeWindowService(), new FakePreviewLoader(), () => { }, new FixedTime(DateTimeOffset.UnixEpoch));
    }

    private static GroupFilterOption Filter(ResultsViewModel results, GroupKind? kind) => results.Filters.Single(f => f.Kind == kind);

    private static PhotoViewModel PhotoAt(ResultsViewModel results, string path) =>
        results.AllGroups.SelectMany(g => g.Photos).Single(p => p.File.Path == path);

    private static string Keepers(ResultsViewModel results) => string.Join(",", results.Groups.Select(g => Path.GetFileName(g.Group.Keeper.File.Path)));

    [Fact]
    public void Each_group_gets_a_badge_for_its_kind()
    {
        var results = Results();

        Assert.Equal(
            [(Copy1, GroupKind.Copies, "Copies"), (Frame1, GroupKind.Burst, "Burst"), (Shot1, GroupKind.Screenshots, "Screenshots"), (Look1, GroupKind.LookAlikes, "Look-alikes")],
            results.AllGroups.Select(g => (g.Group.Keeper.File.Path, g.Kind, g.KindLabel)).OrderBy(x => x.Kind));
    }

    [Fact]
    public void Filter_buttons_show_counts_and_start_on_All()
    {
        var results = Results();

        Assert.Equal(["All (4)", "Copies (1)", "Bursts (1)", "Screenshots (1)", "Look-alikes (1)"], results.Filters.Select(f => f.Label));
        Assert.Same(results.Filters[0], results.SelectedFilter);
        Assert.Equal(4, results.Groups.Count);
    }

    [Fact]
    public void A_filter_shows_only_its_kind_and_leaves_the_totals_alone()
    {
        var results = Results();

        results.SelectedFilter = Filter(results, GroupKind.Burst);

        Assert.Equal("IMG_BURST1.jpg", Keepers(results));
        Assert.Equal(4, results.AllGroups.Count);
        Assert.Equal("4 groups with 4 extra copies", results.Summary);

        results.SelectedFilter = Filter(results, null);
        Assert.Equal(4, results.Groups.Count);
    }

    [Fact]
    public void Filtering_keeps_the_selection_and_says_how_many_selected_photos_are_hidden()
    {
        var results = Results();
        PhotoAt(results, Frame2).ToggleCommand.Execute(null); // now Copy2 (suggested) and Frame2 are selected

        results.SelectedFilter = Filter(results, GroupKind.Screenshots);

        Assert.Equal(2, results.SelectedCount);
        Assert.Equal(2, results.HiddenSelectedCount);
        Assert.EndsWith("(2 not shown)", results.SelectionText);
        Assert.True(PhotoAt(results, Copy2).IsSelected);

        results.SelectedFilter = Filter(results, GroupKind.Burst);
        Assert.EndsWith("(1 not shown)", results.SelectionText);

        results.SelectedFilter = Filter(results, null);
        Assert.Equal(0, results.HiddenSelectedCount);
        Assert.DoesNotContain("not shown", results.SelectionText);
    }

    [Fact]
    public void Select_all_suggested_and_clear_reach_hidden_groups_too()
    {
        var results = Results();
        results.SelectedFilter = Filter(results, GroupKind.Burst);

        results.ClearSelectionCommand.Execute(null);
        Assert.False(PhotoAt(results, Copy2).IsSelected);

        results.SelectSuggestedCommand.Execute(null);
        Assert.True(PhotoAt(results, Copy2).IsSelected);
        Assert.EndsWith("(1 not shown)", results.SelectionText);
    }

    [Fact]
    public void Move_includes_hidden_selected_photos_and_the_confirmation_says_so()
    {
        using var _ = InlineSynchronizationContext.Install();
        var results = Results();
        PhotoAt(results, Frame2).ToggleCommand.Execute(null);
        results.SelectedFilter = Filter(results, GroupKind.Burst);

        results.MoveCommand.Execute(null);

        Assert.True(results.IsConfirming);
        Assert.StartsWith("Move 2 photos", results.ConfirmText);
        Assert.True(results.HasConfirmHiddenText);
        Assert.Equal("1 of these photos is in groups the \"Bursts\" filter hides. Choose All to see them.", results.ConfirmHiddenText);

        results.ConfirmMoveCommand.Execute(null);
        Assert.Equal([Copy2, Frame2], _cleanup.LastMove.Paths.Order());
        Assert.False(results.HasConfirmHiddenText);
    }

    [Fact]
    public void The_confirmation_has_no_hidden_note_when_everything_moving_is_shown()
    {
        var results = Results();

        results.MoveCommand.Execute(null);

        Assert.True(results.IsConfirming);
        Assert.Equal("", results.ConfirmHiddenText);
        Assert.False(results.HasConfirmHiddenText);
    }

    [Fact]
    public void After_a_move_the_filter_stays_and_the_counts_and_kinds_are_worked_out_again()
    {
        using var _ = InlineSynchronizationContext.Install();
        // A burst frame with a byte-identical copy of the keeper: moving the other frame leaves only copies.
        PhotoGroup mixed = new(
        [
            new GroupMember(Bytes(Frame1, "F"), MatchKind.Keeper, null, null),
            new GroupMember(Bytes(@"C:\Photos\IMG_BURST1 - Copy.jpg", "F"), MatchKind.Identical, null, null),
            new GroupMember(Photo(Frame2), MatchKind.Similar, null, null),
        ], "test");
        var results = Results([mixed, OneOfEach[3]]);
        Assert.Equal(GroupKind.Burst, results.AllGroups[0].Kind);
        var bursts = Filter(results, GroupKind.Burst);
        results.SelectedFilter = bursts;
        results.ClearSelectionCommand.Execute(null);
        PhotoAt(results, Frame2).ToggleCommand.Execute(null);

        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null);
        _cleanup.LastMove.Complete();

        Assert.Same(bursts, results.SelectedFilter);
        Assert.Equal("Bursts (0)", bursts.Label);
        Assert.Equal("Copies (1)", Filter(results, GroupKind.Copies).Label);
        Assert.True(results.IsEmpty);
        Assert.Equal("No groups in \"Bursts\" left at this strictness.", results.EmptyText);
    }

    [Fact]
    public void A_filter_with_no_groups_says_so()
    {
        var results = Results([OneOfEach[0]]);

        results.SelectedFilter = Filter(results, GroupKind.Screenshots);

        Assert.True(results.IsEmpty);
        Assert.Equal("No groups in \"Screenshots\" at this strictness.", results.EmptyText);
    }

    [Fact]
    public void A_null_filter_from_the_view_is_ignored()
    {
        var results = Results();

        results.SelectedFilter = null!; // a ListBox can push null while its items are replaced

        Assert.Same(results.Filters[0], results.SelectedFilter);
    }

    [Fact]
    public void Sidecars_left_behind_are_reported_without_counting_as_failed_photos()
    {
        using var _ = InlineSynchronizationContext.Install();
        var results = Results();
        PhotoAt(results, Frame2).ToggleCommand.Execute(null);
        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null);
        _cleanup.LastMove.CompleteWithSidecarFailure(Frame2 + ".json");

        Assert.Equal("Moved the photos, but 1 metadata file stayed behind", results.ReportTitle);
        var section = Assert.Single(results.ReportSections);
        Assert.Equal(ResultsViewModel.SidecarsLeftHeading, section.Heading);
        Assert.StartsWith(Frame2 + ".json", Assert.Single(section.Lines));
        Assert.DoesNotContain(Frame2, results.AllGroups.SelectMany(g => g.Photos).Select(p => p.File.Path)); // the photo did move
    }

    [Fact]
    public void Sidecars_undo_could_not_put_back_are_reported()
    {
        using var _ = InlineSynchronizationContext.Install();
        var results = Results();
        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null);
        var moved = _cleanup.LastMove.Complete();

        results.UndoCommand.Execute(null);
        _cleanup.LastUndo.Complete(
            moved.Moved.Select(m => new RestoredFile(m.OriginalPath, m.OriginalPath)),
            sidecarFailures: [new CleanupFailure(Copy2 + ".json", CleanupFailureReason.InUse, "in use")]);

        Assert.Equal("Put back 1 photo.", results.ReportTitle);
        var section = Assert.Single(results.ReportSections);
        Assert.Equal(ResultsViewModel.SidecarsNotBackHeading, section.Heading);
        Assert.Equal([Copy2 + ".json: in use"], section.Lines);
    }
}
