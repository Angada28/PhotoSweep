using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using PhotoSweep.Presentation.Services;
using PhotoSweep.Tests.Cleanup;
using static PhotoSweep.Tests.Presentation.Scanned;
using Files = PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Presentation;

// The compare window, driven the way the user reaches it: through the results page's Compare commands, with fakes for
// the window, the preview loader and the clean-up engine.
public class CompareViewModelTests
{
    private const long MB = 1024 * 1024;

    // Largest saving first, so the page shows them in this order: A (3 others), B (2 others), C (1 other).
    private const string A = @"C:\Photos\a.jpg";
    private const string A1 = @"C:\Photos\a1.jpg";
    private const string A2 = @"C:\Photos\a2.jpg";
    private const string A3 = @"C:\Photos\a3.jpg";
    private const string B = @"C:\Photos\b.jpg";
    private const string B1 = @"C:\Photos\b1.jpg";
    private const string B2 = @"C:\Photos\b2.jpg";
    private const string C = @"C:\Photos\c.jpg";
    private const string C1 = @"C:\Photos\c1.jpg";

    private static readonly PhotoGroup[] ThreeGroups =
    [
        Group(Photo(A, 9 * MB), Photo(A1, 3 * MB), Photo(A2, 3 * MB), Photo(A3, 3 * MB)),
        Group(Photo(B, 9 * MB), Photo(B1, 2 * MB), Photo(B2, 2 * MB)),
        Group(Photo(C, 9 * MB), Photo(C1, 1 * MB)),
    ];

    private readonly FakeScanService _scan = new();
    private readonly FakeCleanupService _cleanup = new();
    private readonly FakeShellService _shell = new();
    private readonly FakeFileAvailability _availability = new();
    private readonly FakeWindowService _windows = new();
    private readonly FakePreviewLoader _previews = new();

    private ResultsViewModel Results(params PhotoGroup[] groups)
    {
        groups = groups.Length > 0 ? groups : ThreeGroups;
        var request = new ScanRequest(new ScanOptions { Folders = [@"C:\Photos"] }, MatchLevel.SamePhoto);
        var scan = new ScanResult(groups.SelectMany(g => g.Members).Select(m => m.File), cacheHits: 0);
        return new ResultsViewModel(new ScanOutcome(request, scan, groups, []), _scan, _cleanup, _shell, _availability,
            _windows, _previews, () => { }, new FixedTime(DateTimeOffset.UnixEpoch));
    }

    private static PhotoViewModel PhotoAt(ResultsViewModel results, string path) =>
        results.Groups.SelectMany(g => g.Photos).Single(p => p.File.Path == path);

    private static GroupViewModel GroupOf(ResultsViewModel results, string path) =>
        results.Groups.Single(g => g.Photos.Any(p => p.File.Path == path));

    private CompareViewModel Compare(ResultsViewModel results, string path)
    {
        PhotoAt(results, path).CompareCommand.Execute(null);
        return _windows.Last;
    }

    private static (string Left, string Right) Showing(CompareViewModel compare) => (compare.Left.FilePath, compare.Right.FilePath);

    private static HashSet<string> Selected(ResultsViewModel results) =>
        results.Groups.SelectMany(g => g.Photos).Where(p => p.IsSelected).Select(p => p.File.Path).ToHashSet();

    /// <summary>Selects exactly these, then moves them to the review folder with the fake engine.</summary>
    private void Move(ResultsViewModel results, params string[] paths)
    {
        results.ClearSelectionCommand.Execute(null);
        foreach (var path in paths)
            PhotoAt(results, path).ToggleCommand.Execute(null);
        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null);
        _cleanup.LastMove.Complete();
    }

    /// <summary>A group in the order the ranker puts them, measured against <paramref name="context"/> like the grouper does.</summary>
    private static PhotoGroup Ranked(IReadOnlyCollection<ScannedFile> context, params ScannedFile[] files)
    {
        var (ranked, reason) = KeeperRanker.Rank(files, context);
        return new PhotoGroup(
            ranked.Select((f, i) => new GroupMember(f, i == 0 ? MatchKind.Keeper : MatchKind.SamePhoto, null, null)).ToList(), reason, context);
    }

    private static PhotoGroup Ranked(params ScannedFile[] files) => Ranked(files, files);

    private static CompareDetailRow Row(CompareViewModel compare, string label) => compare.Details.Single(r => r.Label == label);

    // ---- Opening ----

    [Fact]
    public void Opens_on_the_clicked_photo_with_the_keeper_on_the_left()
    {
        var results = Results();

        var compare = Compare(results, A2);

        Assert.Equal((A, A2), Showing(compare));
        Assert.Equal("Photo 2 of 3 besides the keeper", compare.PhotoPosition);
        Assert.Equal("Group 1 of 3", compare.GroupPosition);
        Assert.Same(PhotoAt(results, A2), compare.Right.Photo); // the page's own view-model, not a copy
    }

    [Fact]
    public void Opening_from_the_keeper_or_the_group_row_shows_the_first_other_photo()
    {
        var results = Results();

        Assert.Equal((B, B1), Showing(Compare(results, B)));

        GroupOf(results, C).CompareCommand.Execute(null);
        Assert.Equal((C, C1), Showing(_windows.Last));
    }

    [Fact]
    public void Opening_again_points_the_same_window_at_the_new_photo()
    {
        var results = Results();
        var first = Compare(results, A1);

        var second = Compare(results, B2);

        Assert.Same(first, second);
        Assert.Equal((B, B2), Showing(second));
        Assert.Equal(2, _windows.Shown.Count); // shown (brought to front) again, but one window
    }

    [Fact]
    public void Opening_the_compare_window_never_changes_the_selection()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        PhotoAt(results, A3).ToggleCommand.Execute(null); // not just the suggestions
        results.MoveCommand.Execute(null); // and the confirmation is open
        var before = Selected(results);
        var (count, bytes) = (results.SelectedCount, results.SelectedBytes);

        var compare = Compare(results, A1);
        Compare(results, A); // the keeper
        GroupOf(results, B).CompareCommand.Execute(null);
        compare.NextPhotoCommand.Execute(null);
        compare.NextGroupCommand.Execute(null);
        compare.PreviousGroupCommand.Execute(null);
        compare.SetPaneSize(800, 600);
        compare.ToggleActualSizeCommand.Execute(null);

        Assert.Equal(before, Selected(results));
        Assert.Equal((count, bytes), (results.SelectedCount, results.SelectedBytes));
        Assert.True(results.IsConfirming); // a selection change would have closed it
        Assert.Empty(_cleanup.Moves);
    }

    // ---- Navigation ----

    [Fact]
    public void Left_and_right_step_through_the_group_and_stop_at_the_ends()
    {
        var compare = Compare(Results(), A1);

        Assert.False(compare.PreviousPhotoCommand.CanExecute(null));
        compare.PreviousPhotoCommand.Execute(null); // a key press at the end does nothing
        Assert.Equal((A, A1), Showing(compare));

        compare.NextPhotoCommand.Execute(null);
        Assert.Equal((A, A2), Showing(compare));
        compare.NextPhotoCommand.Execute(null);
        Assert.Equal((A, A3), Showing(compare));
        Assert.Equal("Photo 3 of 3 besides the keeper", compare.PhotoPosition);

        Assert.False(compare.NextPhotoCommand.CanExecute(null));
        compare.NextPhotoCommand.Execute(null);
        Assert.Equal((A, A3), Showing(compare)); // doesn't wrap

        compare.PreviousPhotoCommand.Execute(null);
        Assert.Equal((A, A2), Showing(compare));
    }

    [Fact]
    public void A_group_of_two_has_nowhere_to_step()
    {
        var compare = Compare(Results(), C1);

        Assert.False(compare.PreviousPhotoCommand.CanExecute(null));
        Assert.False(compare.NextPhotoCommand.CanExecute(null));
        Assert.Equal("Photo 1 of 1 besides the keeper", compare.PhotoPosition);
    }

    [Fact]
    public void Page_up_and_down_move_between_groups_and_stop_at_the_ends()
    {
        var compare = Compare(Results(), A3);

        Assert.False(compare.PreviousGroupCommand.CanExecute(null));
        compare.PreviousGroupCommand.Execute(null);
        Assert.Equal((A, A3), Showing(compare));

        compare.NextGroupCommand.Execute(null);
        Assert.Equal((B, B1), Showing(compare)); // the next group's first photo after the keeper
        Assert.Equal("Group 2 of 3", compare.GroupPosition);

        compare.NextGroupCommand.Execute(null);
        Assert.Equal((C, C1), Showing(compare));
        Assert.False(compare.NextGroupCommand.CanExecute(null));
        compare.NextGroupCommand.Execute(null);
        Assert.Equal((C, C1), Showing(compare));

        compare.PreviousGroupCommand.Execute(null);
        compare.PreviousGroupCommand.Execute(null);
        Assert.Equal((A, A1), Showing(compare));
    }

    // ---- Details: highlighted differences and the better side ----

    [Fact]
    public void Higher_resolution_is_marked_on_the_keepers_side_and_equal_details_are_not_highlighted()
    {
        var group = Ranked(
            Files.Photo("small.jpg", width: 800, height: 600),
            Files.Photo("big.jpg", width: 4000, height: 3000));
        var compare = Compare(Results(group), @"C:\Photos\small.jpg");

        var resolution = Row(compare, "Resolution");
        Assert.Equal(("4000×3000", "800×600"), (resolution.Left, resolution.Right));
        Assert.True(resolution.Differs);
        Assert.Equal(CompareSide.Left, resolution.Better);
        Assert.True(resolution.LeftBetter);

        Assert.False(Row(compare, "Folder").Differs);
        Assert.False(Row(compare, "Format").Differs);
        Assert.True(Row(compare, "File name").Differs);
        Assert.Equal(CompareSide.None, Row(compare, "File name").Better); // neither name looks like a copy
        Assert.Equal("PhotoSweep prefers the left photo: Highest resolution (4000×3000 vs 800×600)", compare.Verdict);
    }

    [Fact]
    public void Resolution_within_one_percent_is_highlighted_but_not_marked()
    {
        var group = Ranked(
            Files.Photo("cropped.jpg", width: 4000, height: 2990),
            Files.Photo("camera.jpg", width: 4000, height: 3000, camera: "Canon EOS R5"));
        var compare = Compare(Results(group), @"C:\Photos\cropped.jpg");

        Assert.Equal(@"C:\Photos\camera.jpg", compare.Left.FilePath);
        Assert.True(Row(compare, "Resolution").Differs);
        Assert.Equal(CompareSide.None, Row(compare, "Resolution").Better); // the ranker calls it a tie
        Assert.Equal(("Canon EOS R5", "Not recorded"), (Row(compare, "Camera").Left, Row(compare, "Camera").Right));
        Assert.Equal(CompareSide.Left, Row(compare, "Camera").Better);
        Assert.Equal(CompareSide.None, Row(compare, "Date taken").Better); // neither has one, so nothing to mark
        Assert.StartsWith("PhotoSweep prefers the left photo: Has camera data", compare.Verdict);
    }

    [Fact]
    public void File_size_is_only_marked_between_files_of_the_same_format()
    {
        var group = Ranked(
            Files.Photo("photo.jpg", size: 1_000_000, modified: Files.DefaultTime.AddDays(-1)),
            Files.Photo("photo.png", size: 5_000_000));
        var compare = Compare(Results(group), @"C:\Photos\photo.png");

        Assert.True(Row(compare, "File size").Differs);
        Assert.Equal(CompareSide.None, Row(compare, "File size").Better); // a PNG of a JPEG is bigger, not better
        Assert.Equal(("JPEG", "PNG"), (Row(compare, "Format").Left, Row(compare, "Format").Right));
        Assert.Equal(CompareSide.Left, Row(compare, "Modified").Better); // older copy decides instead
    }

    [Fact]
    public void Sizes_that_round_the_same_are_shown_in_bytes()
    {
        var group = Ranked(Files.Photo("big.jpg", size: 1_363_148), Files.Photo("small (1).jpg", size: 1_340_000));
        var compare = Compare(Results(group), @"C:\Photos\small (1).jpg");

        var size = Row(compare, "File size");
        Assert.Equal(("1,363,148 bytes", "1,340,000 bytes"), (size.Left, size.Right)); // both "1.3 MB" otherwise
        Assert.Equal(CompareSide.Left, size.Better);
        Assert.Equal(CompareSide.Left, Row(compare, "File name").Better); // "small (1)" looks like a copy
    }

    [Fact]
    public void Jpg_and_jpeg_are_the_same_format()
    {
        var group = Ranked(Files.Photo("a.jpg"), Files.Photo("b.jpeg"));
        var compare = Compare(Results(group), @"C:\Photos\b.jpeg");

        Assert.False(Row(compare, "Format").Differs);
    }

    [Fact]
    public void Better_is_measured_against_the_set_the_group_was_ranked_in()
    {
        // Against the whole set, "wide" is within 1% of the largest photo and "narrow" isn't, so resolution decides.
        // Against just the two of them both would be "largest" and narrow's camera data would win instead.
        var wide = Files.Photo("wide.jpg", width: 1000, height: 1000);
        var narrow = Files.Photo("narrow.jpg", width: 995, height: 1000, camera: "Pixel 8");
        var outsider = Files.Photo("outsider.jpg", width: 1010, height: 1000);
        var group = Ranked([wide, narrow, outsider], wide, narrow);

        var compare = Compare(Results(group), narrow.Path);

        Assert.Equal((wide.Path, narrow.Path), Showing(compare));
        Assert.Equal(CompareSide.Left, Row(compare, "Resolution").Better);
        Assert.StartsWith("PhotoSweep prefers the left photo: Highest resolution", compare.Verdict);
    }

    [Fact]
    public void The_verdict_always_agrees_with_the_keeper()
    {
        var files = new[]
        {
            Files.Photo("IMG_1.jpg", width: 4000, height: 3000, size: 3_000_000),
            Files.Photo("IMG_1 (1).jpg", width: 4000, height: 3000, size: 3_000_000, camera: "iPhone 15"),
            Files.Photo("export.jpg", width: 2000, height: 1500, camera: "iPhone 15"),
            Files.Photo("IMG_1.png", width: 4000, height: 2980, size: 9_000_000),
            Files.Photo("old.jpg", width: 4000, height: 3000, size: 3_010_000, modified: Files.DefaultTime.AddYears(-1)),
        };
        var group = Ranked(files);
        var compare = Compare(Results(group), group.Members[1].File.Path);

        for (var i = 1; i < files.Length; i++)
        {
            Assert.StartsWith("PhotoSweep prefers the left photo", compare.Verdict);
            compare.NextPhotoCommand.Execute(null);
        }
    }

    [Fact]
    public void Byte_identical_copies_say_so()
    {
        var group = Ranked(Files.Photo("a.jpg", sha: "same"), Files.Photo("a copy.jpg", sha: "same"));
        var compare = Compare(Results(group), @"C:\Photos\a copy.jpg");

        Assert.Equal("Identical copies: the two files are the same, byte for byte.", compare.Verdict);
    }

    // ---- Selection ----

    [Fact]
    public void Selecting_in_the_window_selects_on_the_results_page()
    {
        var results = Results();
        results.ClearSelectionCommand.Execute(null);
        var compare = Compare(results, B2);

        compare.Right.ToggleCommand.Execute(null); // Space

        Assert.True(PhotoAt(results, B2).IsSelected);
        Assert.True(compare.Right.IsSelected);
        Assert.Equal("✓ Selected to move to the review folder", compare.Right.SelectionText);
        Assert.Equal(1, results.SelectedCount);
        Assert.Equal(2 * MB, results.SelectedBytes);

        compare.Right.ToggleCommand.Execute(null);
        Assert.False(PhotoAt(results, B2).IsSelected);
        Assert.Equal(0, results.SelectedCount);
    }

    [Fact]
    public void Selecting_on_the_results_page_shows_in_the_window()
    {
        var results = Results();
        results.ClearSelectionCommand.Execute(null);
        var compare = Compare(results, B2);
        var changed = new List<string?>();
        compare.Right.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        PhotoAt(results, B2).ToggleCommand.Execute(null);

        Assert.True(compare.Right.IsSelected);
        Assert.Contains(nameof(ComparePaneViewModel.IsSelected), changed); // so the window redraws
        Assert.Equal("Keep this photo", compare.Right.ToggleText);
    }

    [Fact]
    public void The_keep_one_rule_holds_in_the_window()
    {
        var results = Results();
        results.ClearSelectionCommand.Execute(null);
        var compare = Compare(results, C1);

        compare.Left.ToggleCommand.Execute(null); // keep the other copy instead of the suggested keeper

        Assert.True(compare.Left.IsSelected);
        Assert.False(compare.Right.ToggleCommand.CanExecute(null));
        compare.Right.ToggleCommand.Execute(null); // Space, even if the view ignored CanExecute
        Assert.False(compare.Right.IsSelected);
        Assert.Equal(1, results.SelectedCount);
        Assert.Equal("This is the last photo left in the group. Keep at least one copy.", compare.Right.ToggleHint);
    }

    [Fact]
    public void Selecting_waits_while_the_results_page_is_moving()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var compare = Compare(results, B1);
        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null); // the fake engine holds the move until completed

        Assert.True(compare.IsBusy);
        Assert.Equal("Moving photos to the review folder…", compare.BusyText);
        Assert.False(compare.Left.ToggleCommand.CanExecute(null));
        Assert.False(compare.Right.ToggleCommand.CanExecute(null));
    }

    // ---- Keeping up with the results page ----

    [Fact]
    public void When_the_right_photo_is_moved_the_window_moves_on_to_the_next_one()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var compare = Compare(results, A2);

        Move(results, A2);

        Assert.Equal((A, A3), Showing(compare)); // A3 took A2's place
        Assert.Same(GroupOf(results, A), compare.Group);
        Assert.Equal("Photo 2 of 2 besides the keeper", compare.PhotoPosition);
        Assert.False(compare.IsClosed);
    }

    [Fact]
    public void When_the_group_is_gone_the_window_moves_on_to_the_next_group()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var compare = Compare(results, B1);

        Move(results, B1, B2); // B is left alone, so the group is gone

        Assert.Equal((C, C1), Showing(compare)); // the group that followed B
        Assert.Equal("Group 2 of 2", compare.GroupPosition);
    }

    [Fact]
    public void When_nothing_is_left_the_window_closes()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results(Group(Photo(C, 9 * MB), Photo(C1, 1 * MB)));
        var compare = Compare(results, C1);

        Move(results, C1);

        Assert.True(compare.IsClosed);
        Assert.Equal(1, _windows.CloseRequests);
        Assert.False(compare.Right.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public void A_move_elsewhere_keeps_the_same_photos_on_the_new_rows_without_reloading()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var compare = Compare(results, B2);
        compare.SetPaneSize(800, 600);
        var loads = _previews.For(B2).Count;

        Move(results, A1);

        Assert.Equal((B, B2), Showing(compare));
        Assert.Same(PhotoAt(results, B2), compare.Right.Photo); // the rebuilt row's view-model, so selection stays in sync
        Assert.Equal(loads, _previews.For(B2).Count); // same file, same preview

        PhotoAt(results, B2).ToggleCommand.Execute(null);
        Assert.True(compare.Right.IsSelected);
    }

    [Fact]
    public void When_the_keeper_is_moved_the_best_copy_left_takes_the_left_pane()
    {
        using var sync = InlineSynchronizationContext.Install();
        var results = Results();
        var compare = Compare(results, A1);

        Move(results, A); // keep the others instead

        Assert.Equal((A1, A2), Showing(compare));
    }

    [Fact]
    public void Leaving_the_results_page_closes_the_window()
    {
        var results = Results();
        var compare = Compare(results, A1);

        results.BackCommand.Execute(null);

        Assert.True(compare.IsClosed);
    }

    [Fact]
    public void A_closed_window_stops_following_the_page_and_the_next_open_starts_fresh()
    {
        var results = Results();
        var first = Compare(results, A1);

        first.CloseCommand.Execute(null); // Esc
        var second = Compare(results, B1);

        Assert.True(first.IsClosed);
        Assert.NotSame(first, second);
        Assert.Equal((B, B1), Showing(second));
    }

    // ---- Previews ----

    [Fact]
    public void Loads_nothing_until_the_pane_size_is_known_then_both_photos_and_the_neighbours()
    {
        var compare = Compare(Results(), A2);
        Assert.Empty(_previews.Calls);
        Assert.True(compare.Right.IsLoading);

        compare.SetPaneSize(900, 700);

        Assert.Equal(new PreviewSize(900, 700), _previews.For(A).Single().Size);
        Assert.Equal(new PreviewSize(900, 700), _previews.For(A2).Single().Size);
        Assert.Equal(_previews.For(A2).Single().Image, compare.Right.Image);
        Assert.Equal(PreviewState.Shown, compare.Right.Preview);
        // Prefetched: the photos either side, and the next group's pair.
        Assert.Equal([A, A2, A1, A3, B, B1], _previews.Paths);
    }

    [Fact]
    public void Online_only_and_missing_photos_get_placeholders_and_are_never_loaded()
    {
        _availability.Set(A2, FileAvailability.OnlineOnly).Set(A3, FileAvailability.Unavailable).Set(B1, FileAvailability.OnlineOnly);
        var compare = Compare(Results(), A2);

        compare.SetPaneSize(800, 600);
        Assert.Equal(PreviewState.OnlineOnly, compare.Right.Preview);
        Assert.True(compare.Right.ShowCloudPlaceholder);
        Assert.Null(compare.Right.Image);

        compare.NextPhotoCommand.Execute(null);
        Assert.Equal(PreviewState.Unavailable, compare.Right.Preview);
        Assert.True(compare.Right.ShowUnavailablePlaceholder);

        compare.NextGroupCommand.Execute(null);
        compare.ToggleActualSizeCommand.Execute(null);
        compare.SetPaneSize(1200, 900);

        // Not opened for a preview, a prefetch or actual size (CLAUDE.md rule 6), and not for a missing file.
        Assert.DoesNotContain(A2, _previews.Paths);
        Assert.DoesNotContain(A3, _previews.Paths);
        Assert.DoesNotContain(B1, _previews.Paths);
    }

    [Fact]
    public void A_file_that_went_online_only_after_the_scan_is_checked_again_each_time()
    {
        var compare = Compare(Results(), B2);
        compare.SetPaneSize(800, 600);
        Assert.Equal(PreviewState.Shown, compare.Right.Preview);

        _availability.Set(B2, FileAvailability.OnlineOnly); // OneDrive freed up space
        compare.PreviousPhotoCommand.Execute(null);
        compare.NextPhotoCommand.Execute(null);

        Assert.Equal(PreviewState.OnlineOnly, compare.Right.Preview);
        Assert.Single(_previews.For(B2));
    }

    [Fact]
    public void A_photo_that_cant_be_decoded_shows_the_no_preview_placeholder()
    {
        _previews.Hold = true;
        using var sync = InlineSynchronizationContext.Install();
        var compare = Compare(Results(), C1);
        compare.SetPaneSize(800, 600);

        _previews.For(C1).Single().Complete(ThumbnailStatus.CannotDecode);

        Assert.True(compare.Right.ShowNoPreviewPlaceholder);
    }

    [Fact]
    public void Moving_on_cancels_the_load_and_prefetches_and_drops_their_late_results()
    {
        _previews.Hold = true;
        using var sync = InlineSynchronizationContext.Install();
        var compare = Compare(Results(), A1);
        compare.SetPaneSize(800, 600);
        var firstRight = _previews.For(A1).Single();
        var prefetches = _previews.Calls.Where(c => c.Path is A2 or B or B1).ToList();

        compare.NextPhotoCommand.Execute(null);
        firstRight.CompleteOk(); // arrives after the user moved on

        Assert.True(firstRight.Token.IsCancellationRequested);
        Assert.All(prefetches, c => Assert.True(c.Token.IsCancellationRequested));
        Assert.Null(compare.Right.Image); // A2's load is still pending: nothing shown, and never A1's picture
        Assert.True(compare.Right.IsLoading);
        Assert.False(_previews.For(A).Single().Token.IsCancellationRequested); // the left pane didn't change

        _previews.Calls.First(c => c.Path == A2 && !c.Token.IsCancellationRequested).CompleteOk();
        Assert.Equal(PreviewState.Shown, compare.Right.Preview);
    }

    [Fact]
    public void A_slightly_bigger_pane_keeps_the_preview_and_a_much_bigger_one_reloads_it()
    {
        var compare = Compare(Results(), C1);
        compare.SetPaneSize(800, 600);

        compare.SetPaneSize(850, 640);
        Assert.Single(_previews.For(C1));

        compare.SetPaneSize(1600, 1200);
        Assert.Equal(new PreviewSize(1600, 1200), _previews.For(C1)[^1].Size);
    }

    [Fact]
    public void Actual_size_loads_every_pixel_without_prefetching_and_fit_comes_back()
    {
        var compare = Compare(Results(), A1);
        compare.SetPaneSize(800, 600);
        var fitCalls = _previews.Calls.Count;

        compare.ToggleActualSizeCommand.Execute(null);

        var actual = _previews.Calls.Skip(fitCalls).ToList();
        Assert.Equal([A, A1], actual.Select(c => c.Path)); // just the two panes, no prefetch
        Assert.All(actual, c => Assert.True(c.Size.IsActualSize));

        compare.NextPhotoCommand.Execute(null);
        Assert.True(_previews.For(A2)[^1].Size.IsActualSize); // (the first was a fit-size prefetch)

        compare.ToggleActualSizeCommand.Execute(null);
        Assert.Equal(new PreviewSize(800, 600), _previews.For(A2)[^1].Size); // back to fit, to free the big pictures
    }

    [Fact]
    public void A_preview_that_is_already_the_whole_photo_is_not_decoded_again()
    {
        _previews.Small.Add(C1);
        var compare = Compare(Results(), C1);
        compare.SetPaneSize(800, 600);

        compare.SetPaneSize(2400, 1800);
        compare.ToggleActualSizeCommand.Execute(null);

        Assert.Single(_previews.For(C1)); // 300×200 fitted into 800×600 is every pixel already
        Assert.Equal(3, _previews.For(C).Count); // the big keeper needed a bigger decode, then actual size
    }

    [Fact]
    public void Zoom_only_works_at_actual_size_and_stays_within_limits()
    {
        var compare = Compare(Results(), A1);
        Assert.False(compare.ZoomInCommand.CanExecute(null));

        compare.IsActualSize = true;
        Assert.Equal(1, compare.Zoom);
        Assert.Equal("100%", compare.ZoomText);

        compare.ZoomInCommand.Execute(null);
        Assert.Equal(1.5, compare.Zoom);
        for (var i = 0; i < 20; i++)
            compare.ZoomInCommand.Execute(null);
        Assert.Equal(CompareViewModel.MaxZoom, compare.Zoom);
        Assert.False(compare.ZoomInCommand.CanExecute(null));

        for (var i = 0; i < 20; i++)
            compare.ZoomOutCommand.Execute(null);
        Assert.Equal(CompareViewModel.MinZoom, compare.Zoom);
        Assert.False(compare.ZoomOutCommand.CanExecute(null));

        compare.Zoom = 100; // e.g. from the view
        Assert.Equal(CompareViewModel.MaxZoom, compare.Zoom);

        compare.IsActualSize = false;
        compare.IsActualSize = true;
        Assert.Equal(1, compare.Zoom); // entering 1:1 starts at actual size
    }

    // ---- Show in folder ----

    [Fact]
    public void Show_in_folder_selects_the_file_in_Explorer_and_says_when_it_cant()
    {
        var compare = Compare(Results(), B2);

        compare.Right.ShowInFolderCommand.Execute(null);
        Assert.Equal([B2], _shell.ShownInFolder);
        Assert.Equal("", compare.ErrorMessage);

        _shell.Succeeds = false;
        compare.Left.ShowInFolderCommand.Execute(null);
        Assert.Equal([B2, B], _shell.ShownInFolder);
        Assert.Contains("Couldn't show b.jpg in Explorer", compare.ErrorMessage);
    }
}
