using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using PhotoSweep.Tests.Cleanup;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

public class ResultsViewModelTests
{
    private const long MB = 1024 * 1024;

    private readonly FakeScanService _service = new();
    private readonly FakeFileAvailability _availability = new();
    private readonly FakeCleanupService _cleanup = new();
    private readonly FakeShellService _shell = new();
    private readonly FakeWindowService _windows = new();

    private ResultsViewModel Results(MatchLevel level, IReadOnlyList<PhotoGroup> groups, Action? goBack = null, string[]? unreadable = null, ScanResult? scan = null)
    {
        var request = new ScanRequest(new ScanOptions { Folders = [@"C:\Photos"] }, level);
        scan ??= new ScanResult(groups.SelectMany(g => g.Members).Select(m => m.File), cacheHits: 0);
        var outcome = new ScanOutcome(request, scan, groups, unreadable ?? []);
        return new ResultsViewModel(outcome, _service, _cleanup, _shell, _availability, _windows, new FakePreviewLoader(), goBack ?? (() => { }), new FixedTime(DateTimeOffset.UnixEpoch));
    }

    private static readonly PhotoGroup[] TwoGroups =
    [
        Group(Photo(@"C:\Photos\a.jpg", 3 * MB), Photo(@"C:\Photos\a copy.jpg", 1 * MB), Photo(@"C:\Photos\a (2).jpg", 1 * MB)),
        Group(Photo(@"C:\Photos\b.jpg", 5 * MB), Photo(@"C:\Photos\b small.jpg", 2 * MB)),
    ];

    /// <summary>A group whose members matched the keeper as <paramref name="kinds"/> (the first must be Keeper).</summary>
    private static PhotoGroup Mixed(string name, params MatchKind[] kinds) =>
        new(kinds.Select((kind, i) => new GroupMember(Photo($@"C:\Photos\{name}{i}.jpg", MB), kind, null, null)).ToList(), "test");

    private static IEnumerable<PhotoViewModel> AllPhotos(ResultsViewModel results) => results.Groups.SelectMany(g => g.Photos);

    // ---- Totals and summary (carried over from the placeholder page) ----

    [Fact]
    public void Counts_extra_copies_and_their_size_leaving_out_keepers()
    {
        var results = Results(MatchLevel.SamePhoto, TwoGroups);

        Assert.Equal(2, results.GroupCount);
        Assert.Equal(3, results.ExtraCopies);
        Assert.Equal(4 * MB, results.ExtraBytes);
        Assert.Equal("2 groups with 3 extra copies", results.Summary);
    }

    [Theory]
    [InlineData(MatchLevel.Exact, "4 MB in extra copies")]
    [InlineData(MatchLevel.SamePhoto, "4 MB in extra copies")]
    [InlineData(MatchLevel.Similar, "Up to 4 MB in look-alike copies")]
    public void Space_is_only_a_ceiling_at_Similar(MatchLevel level, string expected)
    {
        Assert.Equal(expected, Results(level, TwoGroups).SpaceText);
    }

    [Fact]
    public void One_group_is_worded_in_the_singular()
    {
        var results = Results(MatchLevel.Exact, [Group(Photo(@"C:\Photos\a.jpg"), Photo(@"C:\Photos\b.jpg"))]);

        Assert.Equal("1 group with 1 extra copy", results.Summary);
    }

    [Fact]
    public void No_groups_says_so()
    {
        var results = Results(MatchLevel.SamePhoto, []);

        Assert.True(results.IsEmpty);
        Assert.Equal("No duplicates found.", results.Summary);
        Assert.Equal("", results.SpaceText);
        Assert.Equal("No photos selected", results.SelectionText);
    }

    [Fact]
    public void Lists_folders_that_were_skipped()
    {
        Assert.False(Results(MatchLevel.SamePhoto, TwoGroups).HasNotIncluded);

        var results = Results(MatchLevel.SamePhoto, TwoGroups, unreadable: [@"D:\Unplugged"]);

        Assert.True(results.HasUnreadableFolders);
        Assert.True(results.HasNotIncluded);
        Assert.Equal([@"D:\Unplugged"], results.UnreadableFolders);
    }

    [Fact]
    public void Says_how_many_cloud_only_photos_were_not_scanned()
    {
        var files = TwoGroups.SelectMany(g => g.Members).Select(m => m.File)
            .Append(OnlineOnly(@"C:\Photos\cloud1.jpg", 1 * MB))
            .Append(OnlineOnly(@"C:\Photos\cloud2.jpg", 2 * MB));

        var results = Results(MatchLevel.SamePhoto, TwoGroups, scan: new ScanResult(files, cacheHits: 0));

        Assert.True(results.HasCloudSkipped);
        Assert.True(results.HasNotIncluded);
        Assert.Equal("2 cloud-only photos (3 MB) weren't scanned.", results.CloudSkippedText);
        Assert.False(Results(MatchLevel.SamePhoto, TwoGroups).HasCloudSkipped);
    }

    [Fact]
    public void Back_goes_back()
    {
        var backs = 0;
        Results(MatchLevel.SamePhoto, TwoGroups, () => backs++).BackCommand.Execute(null);

        Assert.Equal(1, backs);
    }

    // ---- Order ----

    [Fact]
    public void Groups_are_sorted_by_space_freed_largest_first_then_by_keeper_path()
    {
        PhotoGroup[] groups =
        [
            Group(Photo(@"C:\Photos\small.jpg", 9 * MB), Photo(@"C:\Photos\small2.jpg", 1 * MB)),   // frees 1 MB
            Group(Photo(@"C:\Photos\z-big.jpg", 1 * MB), Photo(@"C:\Photos\z-big2.jpg", 5 * MB)),   // frees 5 MB
            Group(Photo(@"C:\Photos\a-big.jpg", 1 * MB), Photo(@"C:\Photos\a-big2.jpg", 5 * MB)),   // frees 5 MB, earlier path
            Group(Photo(@"C:\Photos\mid.jpg", 1 * MB), Photo(@"C:\Photos\mid2.jpg", 2 * MB), Photo(@"C:\Photos\mid3.jpg", 1 * MB)), // 3 MB
        ];

        var results = Results(MatchLevel.Exact, groups);

        Assert.Equal(["a-big.jpg", "z-big.jpg", "mid.jpg", "small.jpg"], results.Groups.Select(g => g.Photos[0].FileName));
        Assert.Equal([5 * MB, 5 * MB, 3 * MB, 1 * MB], results.Groups.Select(g => g.FreeableBytes));
    }

    [Fact]
    public void Each_row_starts_with_the_keeper()
    {
        var row = Results(MatchLevel.Exact, TwoGroups).Groups.Single(g => g.Photos[0].FileName == "a.jpg");

        Assert.True(row.Photos[0].IsKeeper);
        Assert.All(row.Photos.Skip(1), p => Assert.False(p.IsKeeper));
        Assert.Equal("test", row.KeeperReason);
        Assert.Equal("★ Suggested keeper", row.Photos[0].MatchText);
    }

    // ---- Pre-selection ----

    [Fact]
    public void Every_byte_identical_copy_is_preselected()
    {
        var results = Results(MatchLevel.Exact, TwoGroups);

        Assert.All(AllPhotos(results), p => Assert.Equal(!p.IsKeeper, p.IsSelected));
        Assert.Equal("3 photos selected · 4 MB", results.SelectionText);
    }

    [Theory]
    [InlineData(MatchLevel.SamePhoto)]
    [InlineData(MatchLevel.Similar)]
    public void Only_byte_identical_copies_are_preselected_never_look_alikes(MatchLevel level)
    {
        var results = Results(level,
        [
            Mixed("x", MatchKind.Keeper, MatchKind.Identical, MatchKind.SamePhoto, MatchKind.Similar),
            Mixed("y", MatchKind.Keeper, MatchKind.SamePhoto),
        ]);

        var selected = AllPhotos(results).Where(p => p.IsSelected).ToList();
        Assert.Equal(MatchKind.Identical, Assert.Single(selected).Member.Kind);
        Assert.Equal("1 photo selected · 1 MB", results.SelectionText);
        Assert.True(results.SelectSuggestedCommand.CanExecute(null));
    }

    [Fact]
    public void Select_all_suggested_selects_identical_copies_and_leaves_look_alikes_alone()
    {
        var results = Results(MatchLevel.SamePhoto,
        [
            Mixed("x", MatchKind.Keeper, MatchKind.Identical, MatchKind.SamePhoto),
            Mixed("y", MatchKind.Keeper, MatchKind.SamePhoto),
        ]);
        var lookAlike = AllPhotos(results).First(p => p.Member.Kind == MatchKind.SamePhoto);
        lookAlike.ToggleCommand.Execute(null); // the user picks a look-alike by hand...
        results.ClearSelectionCommand.Execute(null);

        results.SelectSuggestedCommand.Execute(null); // ...and "Select all suggested" doesn't bring it back

        Assert.Equal([MatchKind.Identical], AllPhotos(results).Where(p => p.IsSelected).Select(p => p.Member.Kind));
    }

    [Fact]
    public void Select_suggested_is_off_when_the_policy_suggests_nothing()
    {
        var results = Results(MatchLevel.SamePhoto, [Mixed("y", MatchKind.Keeper, MatchKind.Similar, MatchKind.SamePhoto)]);

        Assert.Equal(0, results.SelectedCount);
        Assert.False(results.SelectSuggestedCommand.CanExecute(null));
        Assert.False(results.ClearSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void Groups_with_look_alikes_carry_the_note_and_identical_only_groups_do_not()
    {
        var results = Results(MatchLevel.SamePhoto,
        [
            Mixed("same", MatchKind.Keeper, MatchKind.SamePhoto),
            Mixed("similar", MatchKind.Keeper, MatchKind.Similar),
            Mixed("mixed", MatchKind.Keeper, MatchKind.Identical, MatchKind.SamePhoto),
            Mixed("exact", MatchKind.Keeper, MatchKind.Identical),
        ]);

        var byName = results.Groups.ToDictionary(g => g.Photos[0].FileName, g => g.HasLookAlikes);
        Assert.Equal(new Dictionary<string, bool> { ["same0.jpg"] = true, ["similar0.jpg"] = true, ["mixed0.jpg"] = true, ["exact0.jpg"] = false }, byName);
        Assert.Equal("Look-alikes aren't selected automatically. Compare them and choose.", GroupViewModel.LookAlikeNote);
    }

    // ---- Toggling and the keep-one rule ----

    [Fact]
    public void Clicking_a_photo_toggles_it_and_updates_the_totals()
    {
        var results = Results(MatchLevel.Exact, TwoGroups);
        var copy = AllPhotos(results).Single(p => p.FileName == "b small.jpg");
        Assert.True(copy.IsSelected);
        Assert.Equal("3 photos selected · 4 MB", results.SelectionText);

        copy.ToggleCommand.Execute(null);

        Assert.False(copy.IsSelected);
        Assert.Equal(2, results.SelectedCount);
        Assert.Equal(2 * MB, results.SelectedBytes);
        Assert.Equal("2 photos selected · 2 MB", results.SelectionText);

        copy.ToggleCommand.Execute(null);

        Assert.True(copy.IsSelected);
        Assert.Equal("3 photos selected · 4 MB", results.SelectionText);
    }

    [Fact]
    public void The_last_unselected_photo_in_a_group_cannot_be_selected()
    {
        var results = Results(MatchLevel.Exact, [TwoGroups[1]]);
        var (keeper, copy) = (results.Groups[0].Photos[0], results.Groups[0].Photos[1]);
        Assert.True(copy.IsSelected);

        Assert.False(keeper.ToggleCommand.CanExecute(null));
        Assert.Contains("Keep at least one copy", keeper.Hint);

        keeper.ToggleCommand.Execute(null); // even if the view ignored CanExecute

        Assert.False(keeper.IsSelected);
        Assert.Equal(1, results.SelectedCount);
    }

    [Fact]
    public void The_keeper_can_be_selected_while_another_photo_stays()
    {
        var results = Results(MatchLevel.Exact, [TwoGroups[1]]);
        var (keeper, copy) = (results.Groups[0].Photos[0], results.Groups[0].Photos[1]);

        copy.ToggleCommand.Execute(null);   // keep the copy instead...
        Assert.True(keeper.ToggleCommand.CanExecute(null));
        keeper.ToggleCommand.Execute(null); // ...and remove the suggested keeper

        Assert.True(keeper.IsSelected);
        Assert.False(copy.IsSelected);
        Assert.False(copy.ToggleCommand.CanExecute(null)); // now the copy is the last one left
        Assert.True(keeper.ToggleCommand.CanExecute(null)); // deselecting is always allowed
        Assert.Equal("1 photo selected · 5 MB", results.SelectionText);
    }

    [Fact]
    public void Clear_and_select_suggested_reset_every_group()
    {
        var results = Results(MatchLevel.SamePhoto, TwoGroups);
        var keeper = results.Groups[0].Photos[0];
        results.Groups[0].Photos[1].ToggleCommand.Execute(null);
        keeper.ToggleCommand.Execute(null);

        results.ClearSelectionCommand.Execute(null);

        Assert.All(AllPhotos(results), p => Assert.False(p.IsSelected));
        Assert.Equal("No photos selected", results.SelectionText);
        Assert.False(results.ClearSelectionCommand.CanExecute(null));

        results.SelectSuggestedCommand.Execute(null);

        Assert.All(AllPhotos(results), p => Assert.Equal(!p.IsKeeper, p.IsSelected));
        Assert.Equal(3, results.SelectedCount);
        Assert.Equal(4 * MB, results.SelectedBytes);
    }

    // ---- Changing strictness ----

    [Fact]
    public void Changing_strictness_regroups_the_existing_scan_without_scanning_again()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(MatchLevel.SamePhoto, TwoGroups);

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Similar);

        Assert.True(results.IsRegrouping);
        Assert.Empty(_service.Scans);
        var call = Assert.Single(_service.Groupings);
        Assert.Equal(MatchLevel.Similar, call.Level);
        Assert.Same(results.Outcome.Scan, call.Scan);

        call.Complete(Mixed("x", MatchKind.Keeper, MatchKind.Identical, MatchKind.Similar));

        Assert.False(results.IsRegrouping);
        Assert.Equal(MatchLevel.Similar, results.Level);
        var group = Assert.Single(results.Groups);
        Assert.Equal([false, true, false], group.Photos.Select(p => p.IsSelected)); // the Similar policy
        Assert.Equal("1 photo selected · 1 MB", results.SelectionText);
        Assert.Equal("Up to 2 MB in look-alike copies", results.SpaceText);
    }

    [Fact]
    public void Changing_strictness_resets_the_selection_and_switching_back_reuses_the_groups()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(MatchLevel.SamePhoto, TwoGroups);
        AllPhotos(results).First(p => p.IsSelected).ToggleCommand.Execute(null);
        Assert.Equal(2, results.SelectedCount);

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Exact);
        _service.LastGrouping.Complete(TwoGroups[1]);
        results.SelectedStrictness = StrictnessOption.For(MatchLevel.SamePhoto);

        Assert.Single(_service.Groupings); // SamePhoto came from the scan outcome, not a second grouping
        Assert.False(results.IsRegrouping);
        Assert.Equal(MatchLevel.SamePhoto, results.Level);
        Assert.Equal(2, results.GroupCount);
        Assert.Equal(3, results.SelectedCount); // back to the policy, not the earlier hand-made selection
    }

    [Fact]
    public void A_regroup_result_that_arrives_after_another_change_is_ignored()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(MatchLevel.SamePhoto, TwoGroups);

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Similar);
        var similar = _service.LastGrouping;
        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Exact);
        var exact = _service.LastGrouping;

        Assert.True(similar.Token.IsCancellationRequested);
        exact.Complete(TwoGroups[0]);
        similar.Complete(Mixed("late", MatchKind.Keeper, MatchKind.Similar));

        Assert.Equal(MatchLevel.Exact, results.Level);
        Assert.Equal("a.jpg", Assert.Single(results.Groups).Photos[0].FileName);
        Assert.False(results.IsRegrouping);
    }

    [Fact]
    public void A_failed_regroup_keeps_the_groups_and_puts_the_strictness_back()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(MatchLevel.SamePhoto, TwoGroups);

        results.SelectedStrictness = StrictnessOption.For(MatchLevel.Similar);
        _service.LastGrouping.Fail(new InvalidOperationException("boom"));

        Assert.Equal(MatchLevel.SamePhoto, results.SelectedStrictness.Level);
        Assert.Equal(MatchLevel.SamePhoto, results.Level);
        Assert.Equal(2, results.GroupCount);
        Assert.Contains("boom", results.ErrorMessage);
        Assert.False(results.IsRegrouping);
    }

    // ---- Photo details and the cloud placeholder ----

    [Fact]
    public void A_photo_shows_its_name_folder_resolution_size_and_date_taken()
    {
        var file = Photo(@"C:\Photos\2019\beach.jpg", 3 * MB) with
        {
            Details = new ImageDetails(4032, 3024) { DateTaken = new DateTime(2019, 7, 14, 16, 2, 31) },
        };
        var bare = Photo(@"C:\Photos\beach.heic", 2 * MB); // undecodable: no details, so no date taken
        var photos = Results(MatchLevel.Exact, [Group(file, bare)]).Groups[0].Photos;

        Assert.Equal("beach.jpg", photos[0].FileName);
        Assert.Equal(@"C:\Photos\2019", photos[0].Folder);
        Assert.Equal("4032×3024", photos[0].Resolution);
        Assert.Equal("3 MB", photos[0].Size);
        Assert.Equal("Taken 2019-07-14 16:02", photos[0].Date);

        Assert.Equal("Unknown size", photos[1].Resolution);
        Assert.Equal("Modified 2024-01-01 00:00", photos[1].Date); // file time, in the (UTC) test time zone
    }

    [Fact]
    public void Online_only_photos_are_flagged_for_the_cloud_placeholder_when_shown()
    {
        var results = Results(MatchLevel.Exact, TwoGroups);
        var photos = results.Groups[0].Photos;
        _availability.Set(photos[1].File.Path, FileAvailability.OnlineOnly)
                     .Set(photos[2].File.Path, FileAvailability.Unavailable);
        Assert.Empty(_availability.Checked); // not checked when the page is built, only when a photo is shown

        Assert.True(photos[0].RefreshAvailability());
        Assert.False(photos[1].RefreshAvailability());
        Assert.False(photos[2].RefreshAvailability());

        Assert.Equal(PreviewState.Loading, photos[0].Preview);
        Assert.True(photos[1].ShowCloudPlaceholder);
        Assert.True(photos[2].ShowUnavailablePlaceholder);
        Assert.False(photos[0].ShowCloudPlaceholder);
    }

    [Fact]
    public void A_photo_freed_up_after_the_scan_gets_the_placeholder_the_next_time_it_is_shown()
    {
        var photo = Results(MatchLevel.Exact, TwoGroups).Groups[0].Photos[1];
        Assert.True(photo.RefreshAvailability());
        photo.PreviewLoaded(ThumbnailStatus.Ok);
        Assert.Equal(PreviewState.Shown, photo.Preview);

        _availability.Set(photo.File.Path, FileAvailability.OnlineOnly); // OneDrive "Free up space"

        Assert.False(photo.RefreshAvailability());
        Assert.True(photo.ShowCloudPlaceholder);
    }

    [Theory]
    [InlineData(ThumbnailStatus.Ok, PreviewState.Shown)]
    [InlineData(ThumbnailStatus.OnlineOnly, PreviewState.OnlineOnly)] // the loader's own last-moment check
    [InlineData(ThumbnailStatus.Unavailable, PreviewState.Unavailable)]
    [InlineData(ThumbnailStatus.CannotDecode, PreviewState.NoPreview)]
    public void A_finished_thumbnail_load_sets_what_the_tile_shows(ThumbnailStatus status, PreviewState expected)
    {
        var photo = Results(MatchLevel.Exact, TwoGroups).Groups[0].Photos[0];

        photo.PreviewLoaded(status);

        Assert.Equal(expected, photo.Preview);
    }
}
