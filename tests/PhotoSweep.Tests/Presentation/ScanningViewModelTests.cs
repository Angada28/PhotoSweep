using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

/// <summary>
/// Every test installs <see cref="InlineSynchronizationContext"/> first, so progress reports and the fake's
/// completions are handled synchronously and the tests need no waits.
/// </summary>
public class ScanningViewModelTests
{
    private readonly FakeScanService _service = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<ScanOutcome> _results = [];
    private int _backs;

    private ScanningViewModel Run(MatchLevel level = MatchLevel.SamePhoto, params string[] folders)
    {
        var request = new ScanRequest(new ScanOptions { Folders = folders.Length > 0 ? folders : [@"C:\Photos"] }, level);
        var scanning = new ScanningViewModel(request, _service, () => _backs++, _results.Add, _time);
        _ = scanning.RunAsync();
        return scanning;
    }

    [Fact]
    public void Progress_reports_update_the_stage_counts_and_bar()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        Assert.Equal(ScanState.Running, scanning.State);
        Assert.Equal("Finding photos…", scanning.Heading);
        Assert.True(scanning.IsIndeterminate);

        _service.LastScan.Progress.Report(new ScanProgress(Found: 40, Processed: 10, CacheHits: 3, Errors: 1, 0, 0, EnumerationComplete: false));

        Assert.Equal((40, 10, 3, 1), (scanning.Found, scanning.Checked, scanning.FromCache, scanning.Errors));
        Assert.Equal(ScanStage.FindingPhotos, scanning.Stage);
        Assert.True(scanning.IsIndeterminate); // total not known yet

        _service.LastScan.Progress.Report(new ScanProgress(Found: 50, Processed: 25, CacheHits: 3, Errors: 1, 0, 0, EnumerationComplete: true));

        Assert.Equal(ScanStage.CheckingPhotos, scanning.Stage);
        Assert.Equal("Checking photos…", scanning.Heading);
        Assert.False(scanning.IsIndeterminate);
        Assert.Equal(50, scanning.Percent);

        _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"));

        Assert.Equal(ScanStage.Grouping, scanning.Stage);
        Assert.Equal("Grouping photos…", scanning.Heading);
        Assert.True(scanning.IsIndeterminate);
        Assert.Equal(MatchLevel.SamePhoto, _service.LastGrouping.Level);
    }

    [Fact]
    public void Elapsed_time_is_ticked_by_the_injected_clock_and_stops_when_done()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        var refreshes = 0;
        scanning.PropertyChanged += (_, e) => refreshes += e.PropertyName == nameof(ScanningViewModel.ElapsedText) ? 1 : 0;

        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(3, refreshes); // one PeriodicTimer tick per second, from the fake clock
        Assert.Equal("0:03", scanning.ElapsedText);

        _service.LastScan.Complete();
        _service.LastGrouping.Complete();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("0:03", scanning.ElapsedText);
        Assert.Equal(0, _time.LiveTimers); // the ticker was disposed
    }

    [Fact]
    public void Cancel_waits_for_the_scan_to_stop_then_goes_back()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();

        scanning.CancelCommand.Execute(null);

        Assert.True(_service.LastScan.Token.IsCancellationRequested);
        Assert.Equal(ScanState.Cancelling, scanning.State);
        Assert.Equal("Cancelling…", scanning.Heading);
        Assert.False(scanning.CancelCommand.CanExecute(null));
        Assert.Equal(0, _backs); // the scanner may still be saving its cache

        _service.LastScan.StopCancelled();

        Assert.Equal(1, _backs);
        Assert.Empty(_results);
        Assert.Empty(_service.Groupings);
    }

    [Fact]
    public void Cancel_during_grouping_goes_back_without_results()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"));

        scanning.CancelCommand.Execute(null);
        _service.LastGrouping.Complete(); // grouping can't stop part-way, so it finishes anyway

        Assert.Equal(1, _backs);
        Assert.Empty(_results);
    }

    [Fact]
    public void Cancel_and_wait_completes_only_after_the_scan_has_stopped()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();

        var closing = scanning.CancelAndWaitAsync();

        Assert.True(_service.LastScan.Token.IsCancellationRequested);
        Assert.False(closing.IsCompleted);
        _service.LastScan.StopCancelled();
        Assert.True(closing.IsCompletedSuccessfully);
        Assert.Equal(1, _backs);
    }

    [Fact]
    public void A_scan_error_shows_a_message_and_Back_returns()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();

        _service.LastScan.Fail(new IOException("The device is not ready."));

        Assert.Equal(ScanState.Failed, scanning.State);
        Assert.True(scanning.ShowError);
        Assert.False(scanning.ShowProgress);
        Assert.Contains("The device is not ready.", scanning.ErrorMessage);
        Assert.False(scanning.CancelCommand.CanExecute(null));

        scanning.BackCommand.Execute(null);

        Assert.Equal(1, _backs);
    }

    [Fact]
    public void A_grouping_error_shows_a_message()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"));

        _service.LastGrouping.Fail(new InvalidOperationException("boom"));

        Assert.Equal(ScanState.Failed, scanning.State);
        Assert.Contains("boom", scanning.ErrorMessage);
        Assert.Empty(_results);
    }

    [Fact]
    public void When_no_folder_could_be_read_it_fails_with_the_folders_named()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run(MatchLevel.SamePhoto, @"C:\Gone", @"D:\Unplugged");

        _service.LastScan.Complete(MissingFolder(@"C:\Gone"), MissingFolder(@"D:\Unplugged"));

        Assert.Equal(ScanState.Failed, scanning.State);
        Assert.Contains("None of the folders could be read", scanning.ErrorMessage);
        Assert.Contains(@"C:\Gone", scanning.ErrorMessage);
        Assert.Contains(@"D:\Unplugged", scanning.ErrorMessage);
        Assert.Empty(_service.Groupings);
    }

    [Fact]
    public void When_some_folders_could_be_read_it_continues_and_lists_the_skipped_ones()
    {
        using var sync = InlineSynchronizationContext.Install();
        Run(MatchLevel.SamePhoto, @"C:\Photos", @"D:\Unplugged");

        _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"), MissingFolder(@"D:\Unplugged"));
        _service.LastGrouping.Complete();

        var outcome = Assert.Single(_results);
        Assert.Equal([@"D:\Unplugged"], outcome.UnreadableFolders);
    }

    [Fact]
    public void Skipped_online_only_files_stop_before_grouping_and_ask()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();

        _service.LastScan.Complete(
            Photo(@"C:\Photos\a.jpg"),
            OnlineOnly(@"C:\Photos\b.jpg", 1L << 30),
            OnlineOnly(@"C:\Photos\c.jpg", 1L << 29));

        Assert.Equal(ScanState.OnlineOnlyChoice, scanning.State);
        Assert.True(scanning.ShowOnlineOnlyChoice);
        Assert.Equal("2 photos (1.5 GB) are only in the cloud and were skipped.", scanning.OnlineOnlyMessage);
        Assert.Empty(_service.Groupings);
        Assert.True(scanning.ContinueWithoutCommand.CanExecute(null));
        Assert.True(scanning.DownloadAndScanCommand.CanExecute(null));
    }

    [Fact]
    public void One_online_only_file_is_worded_in_the_singular()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();

        _service.LastScan.Complete(OnlineOnly(@"C:\Photos\b.jpg", 2048));

        Assert.Equal("1 photo (2 KB) is only in the cloud and was skipped.", scanning.OnlineOnlyMessage);
    }

    [Fact]
    public void Continue_without_them_groups_what_was_scanned_and_never_downloads()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        var scan = _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"), OnlineOnly(@"C:\Photos\b.jpg", 100));

        scanning.ContinueWithoutCommand.Execute(null);

        var only = Assert.Single(_service.Scans);
        Assert.False(only.Options.IncludeOnlineOnlyFiles);
        Assert.Same(scan, _service.LastGrouping.Scan);
        Assert.Equal(ScanState.Running, scanning.State);
        Assert.False(scanning.ContinueWithoutCommand.CanExecute(null)); // can't be pressed twice

        _service.LastGrouping.Complete();

        Assert.Same(scan, Assert.Single(_results).Scan);
    }

    [Fact]
    public void Download_and_scan_rescans_the_same_folders_with_online_only_files_included()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run(MatchLevel.SamePhoto, @"C:\Photos", @"C:\OneDrive");
        _service.LastScan.Progress.Report(new ScanProgress(10, 10, 0, 0, 1, 100, true));
        _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"), OnlineOnly(@"C:\OneDrive\b.jpg", 100));

        scanning.DownloadAndScanCommand.Execute(null);

        Assert.Equal(2, _service.Scans.Count);
        Assert.False(_service.Scans[0].Options.IncludeOnlineOnlyFiles);
        Assert.True(_service.Scans[1].Options.IncludeOnlineOnlyFiles);
        Assert.Equal(_service.Scans[0].Options.Folders, _service.Scans[1].Options.Folders);
        Assert.Equal(ScanStage.FindingPhotos, scanning.Stage);
        Assert.Equal(0, scanning.Found); // counts start again for the new scan

        var second = _service.LastScan.Complete(Photo(@"C:\Photos\a.jpg"), Photo(@"C:\OneDrive\b.jpg"));
        _service.LastGrouping.Complete();

        Assert.Same(second, Assert.Single(_results).Scan);
    }

    [Fact]
    public void Cancel_while_choosing_goes_back_straight_away()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        _service.LastScan.Complete(OnlineOnly(@"C:\Photos\b.jpg", 100));

        scanning.CancelCommand.Execute(null);

        Assert.Equal(1, _backs);
        Assert.Single(_service.Scans);
        Assert.Empty(_service.Groupings);
    }

    [Fact]
    public void Elapsed_time_pauses_while_waiting_for_the_choice()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run();
        _time.Advance(TimeSpan.FromSeconds(2));
        _service.LastScan.Complete(OnlineOnly(@"C:\Photos\b.jpg", 100));

        _time.Advance(TimeSpan.FromMinutes(5)); // the user thinks about it
        scanning.ContinueWithoutCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("0:03", scanning.ElapsedText);
    }

    [Fact]
    public void A_finished_scan_hands_the_request_scan_and_groups_to_the_results()
    {
        using var sync = InlineSynchronizationContext.Install();
        var scanning = Run(MatchLevel.Exact);
        var a = Photo(@"C:\Photos\a.jpg");
        var b = Photo(@"C:\Photos\b.jpg");
        var scan = _service.LastScan.Complete(a, b);
        var group = Group(a, b);

        _service.LastGrouping.Complete(group);

        var outcome = Assert.Single(_results);
        Assert.Same(scanning.Request, outcome.Request);
        Assert.Same(scan, outcome.Scan);
        Assert.Same(group, Assert.Single(outcome.Groups));
        Assert.Empty(outcome.UnreadableFolders);
        Assert.Equal(MatchLevel.Exact, _service.LastGrouping.Level);
        Assert.Equal(0, _backs);
    }
}
