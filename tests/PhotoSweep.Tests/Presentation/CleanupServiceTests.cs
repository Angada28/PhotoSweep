using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using PhotoSweep.Presentation.Services;
using PhotoSweep.Tests.Scanning;
using static PhotoSweep.Tests.Cleanup.Groups;

namespace PhotoSweep.Tests.Presentation;

// The results page with the real CleanupService, moving real files in a temp folder and back again.
public class CleanupServiceTests : IDisposable
{
    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Move_then_undo_puts_every_file_back_byte_for_byte()
    {
        using var sync = InlineSynchronizationContext.Install();
        var chelsea = Scanned(_temp.AddPhoto("chelsea.jpg"));
        var chelseaCopy = Scanned(_temp.AddPhoto("chelsea.jpg", @"old\chelsea copy.jpg"));
        var chelseaSmall = Scanned(_temp.AddPhoto("chelsea_half.jpg", @"old\deeper\chelsea small.jpg"));
        var coffee = Scanned(_temp.AddPhoto("coffee.jpg"));
        var coffeeQ50 = Scanned(_temp.AddPhoto("coffee_q50.jpg", "coffee q50.jpg"));
        ScannedFile[] files = [chelsea, chelseaCopy, chelseaSmall, coffee, coffeeQ50];
        var bytesBefore = files.ToDictionary(f => f.Path, f => File.ReadAllBytes(f.Path));

        PhotoGroup[] groups = [Of(chelsea, chelseaCopy, chelseaSmall), Of(coffee, coffeeQ50)];
        var request = new ScanRequest(new ScanOptions { Folders = [_temp.Root] }, MatchLevel.SamePhoto);
        var outcome = new ScanOutcome(request, new ScanResult(files, cacheHits: 0), groups, []);
        var results = new ResultsViewModel(outcome, new FakeScanService(), new CleanupService(), new FakeShellService(),
            new DiskFileAvailability(), () => { });
        Assert.Equal(3, results.SelectedCount); // every copy except the keepers

        results.MoveCommand.Execute(null);
        Assert.True(results.IsConfirming, results.ReportTitle);
        await results.ConfirmMoveCommand.ExecuteAsync(null);

        Assert.False(results.HasReport, string.Join("; ", results.ReportSections.SelectMany(s => s.Lines)));
        Assert.True(results.IsEmpty);
        Assert.Equal("Moved 3 photos", results.UndoText[..14]);
        Assert.All([chelseaCopy, chelseaSmall, coffeeQ50], f => Assert.False(File.Exists(f.Path)));
        Assert.True(File.Exists(chelsea.Path));
        Assert.True(File.Exists(coffee.Path));
        var reviewDir = Path.Combine(_temp.Root, ScanOptions.ReviewFolderName);
        Assert.Equal(3, Directory.EnumerateFiles(reviewDir, "*.jpg", SearchOption.AllDirectories).Count());

        await results.UndoCommand.ExecuteAsync(null);

        Assert.Equal("Put back 3 photos.", results.ReportTitle);
        Assert.Empty(results.ReportSections); // no failures, no new names
        foreach (var (path, bytes) in bytesBefore)
            Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(reviewDir)); // emptied by the undo, so its own clean-up removed it
        Assert.Equal(2, results.GroupCount);
        Assert.Equal(3, results.SelectedCount);
        Assert.False(results.HasUndo);
    }
}
