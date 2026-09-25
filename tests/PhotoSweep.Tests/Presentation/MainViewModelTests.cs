using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation;

namespace PhotoSweep.Tests.Presentation;

public class MainViewModelTests
{
    private readonly FakeFolderPicker _picker = new();

    private MainViewModel Create() => new(_picker, new FakeKnownFolders());

    [Fact]
    public void Starts_on_the_start_page()
    {
        var main = Create();

        Assert.Same(main.Start, main.CurrentPage);
    }

    [Fact]
    public void Scan_switches_to_the_scanning_page_with_the_request()
    {
        var main = Create();
        _picker.WillPick(@"C:\Photos");
        main.Start.AddFolderCommand.Execute(null);
        main.Start.SelectedStrictness = StrictnessOption.For(MatchLevel.Exact);
        var raised = new List<string?>();
        main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        main.Start.ScanCommand.Execute(null);

        var scanning = Assert.IsType<ScanningViewModel>(main.CurrentPage);
        Assert.Equal([@"C:\Photos"], scanning.Folders);
        Assert.Equal(MatchLevel.Exact, scanning.Request.Level);
        Assert.Equal("Exact copies", scanning.LevelTitle);
        Assert.Contains(nameof(MainViewModel.CurrentPage), raised); // the window's binding only updates on this
    }

    [Fact]
    public void Back_returns_to_the_same_start_page_with_folders_and_strictness_kept()
    {
        var main = Create();
        var start = main.Start;
        _picker.WillPick(@"C:\Photos", @"D:\Camera");
        start.AddFolderCommand.Execute(null);
        start.SelectedStrictness = StrictnessOption.For(MatchLevel.Similar);
        start.ScanCommand.Execute(null);

        ((ScanningViewModel)main.CurrentPage).BackCommand.Execute(null);

        Assert.Same(start, main.CurrentPage);
        Assert.Equal([@"C:\Photos", @"D:\Camera"], start.Folders);
        Assert.Equal(MatchLevel.Similar, start.SelectedStrictness.Level);
        Assert.True(start.ScanCommand.CanExecute(null));
    }
}
