using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

public class MainViewModelTests
{
    private readonly FakeFolderPicker _picker = new();
    private readonly FakeScanService _service = new();

    private readonly FakeCleanupService _cleanup = new();

    private MainViewModel Create() =>
        new(_picker, new FakeKnownFolders(), _service, _cleanup, new FakeShellService(), new FakeFileAvailability(),
            new FakeWindowService(), new FakePreviewLoader(), new ManualTimeProvider());

    [Fact]
    public void Starts_on_the_start_page()
    {
        var main = Create();

        Assert.Same(main.Start, main.CurrentPage);
    }

    [Fact]
    public void Scan_switches_to_the_scanning_page_and_starts_scanning_the_request()
    {
        using var sync = InlineSynchronizationContext.Install();
        var main = Create();
        _picker.WillPick(@"C:\Photos");
        main.Start.AddFolderCommand.Execute(null);
        main.Start.SelectedStrictness = StrictnessOption.For(MatchLevel.Exact);
        var raised = new List<string?>();
        main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        main.Start.ScanCommand.Execute(null);

        var scanning = Assert.IsType<ScanningViewModel>(main.CurrentPage);
        Assert.Equal(MatchLevel.Exact, scanning.Request.Level);
        Assert.Equal([@"C:\Photos"], Assert.Single(_service.Scans).Options.Folders);
        Assert.Contains(nameof(MainViewModel.CurrentPage), raised); // the window's binding only updates on this
    }

    [Fact]
    public void Cancelling_returns_to_the_same_start_page_with_folders_and_strictness_kept()
    {
        using var sync = InlineSynchronizationContext.Install();
        var main = Create();
        var start = main.Start;
        _picker.WillPick(@"C:\Photos", @"D:\Camera");
        start.AddFolderCommand.Execute(null);
        start.SelectedStrictness = StrictnessOption.For(MatchLevel.Similar);
        start.ScanCommand.Execute(null);

        ((ScanningViewModel)main.CurrentPage).CancelCommand.Execute(null);
        _service.LastScan.StopCancelled();

        Assert.Same(start, main.CurrentPage);
        Assert.Equal([@"C:\Photos", @"D:\Camera"], start.Folders);
        Assert.Equal(MatchLevel.Similar, start.SelectedStrictness.Level);
        Assert.True(start.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void A_finished_scan_shows_the_results_and_Back_returns_to_start()
    {
        using var sync = InlineSynchronizationContext.Install();
        var main = Create();
        _picker.WillPick(@"C:\Photos");
        main.Start.AddFolderCommand.Execute(null);
        main.Start.ScanCommand.Execute(null);
        var a = Photo(@"C:\Photos\a.jpg");
        var b = Photo(@"C:\Photos\b.jpg");

        _service.LastScan.Complete(a, b);
        _service.LastGrouping.Complete(Group(a, b));

        var results = Assert.IsType<ResultsViewModel>(main.CurrentPage);
        Assert.Equal(1, results.GroupCount);

        results.BackCommand.Execute(null);

        Assert.Same(main.Start, main.CurrentPage);
        Assert.Equal([@"C:\Photos"], main.Start.Folders);
    }

    [Fact]
    public void Closing_mid_scan_waits_for_the_scan_to_stop()
    {
        using var sync = InlineSynchronizationContext.Install();
        var main = Create();
        Assert.True(main.PrepareToCloseAsync().IsCompleted); // nothing running

        _picker.WillPick(@"C:\Photos");
        main.Start.AddFolderCommand.Execute(null);
        main.Start.ScanCommand.Execute(null);

        var closing = main.PrepareToCloseAsync();

        Assert.True(_service.LastScan.Token.IsCancellationRequested);
        Assert.False(closing.IsCompleted);
        _service.LastScan.StopCancelled();
        Assert.True(closing.IsCompletedSuccessfully);
    }

    [Fact]
    public void Closing_mid_move_waits_for_the_move_to_finish()
    {
        using var sync = InlineSynchronizationContext.Install();
        var main = Create();
        _picker.WillPick(@"C:\Photos");
        main.Start.AddFolderCommand.Execute(null);
        main.Start.ScanCommand.Execute(null);
        var a = Photo(@"C:\Photos\a.jpg");
        var b = Photo(@"C:\Photos\b.jpg");
        _service.LastScan.Complete(a, b);
        _service.LastGrouping.Complete(Group(a, b));
        var results = (ResultsViewModel)main.CurrentPage;
        Assert.True(main.PrepareToCloseAsync().IsCompleted); // nothing running on the results page

        results.MoveCommand.Execute(null);
        results.ConfirmMoveCommand.Execute(null);
        var closing = main.PrepareToCloseAsync();

        Assert.False(closing.IsCompleted);
        _cleanup.LastMove.Complete();
        Assert.True(closing.IsCompletedSuccessfully);
    }
}
