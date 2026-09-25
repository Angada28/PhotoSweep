using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation;
using static PhotoSweep.Tests.Presentation.Scanned;

namespace PhotoSweep.Tests.Presentation;

public class ResultsViewModelTests
{
    private const long MB = 1024 * 1024;

    private static ResultsViewModel Results(MatchLevel level, IReadOnlyList<PhotoGroup> groups, Action? goBack = null, params string[] unreadable)
    {
        var request = new ScanRequest(new ScanOptions { Folders = [@"C:\Photos"] }, level);
        var scan = new ScanResult(groups.SelectMany(g => g.Members).Select(m => m.File), cacheHits: 0);
        return new ResultsViewModel(new ScanOutcome(request, scan, groups, unreadable), goBack ?? (() => { }));
    }

    private static readonly PhotoGroup[] TwoGroups =
    [
        Group(Photo(@"C:\Photos\a.jpg", 3 * MB), Photo(@"C:\Photos\a copy.jpg", 1 * MB), Photo(@"C:\Photos\a (2).jpg", 1 * MB)),
        Group(Photo(@"C:\Photos\b.jpg", 5 * MB), Photo(@"C:\Photos\b small.jpg", 2 * MB)),
    ];

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

        Assert.Equal("No duplicates found.", results.Summary);
        Assert.Equal("", results.SpaceText);
    }

    [Fact]
    public void Lists_folders_that_were_skipped()
    {
        Assert.False(Results(MatchLevel.SamePhoto, TwoGroups).HasUnreadableFolders);

        var results = Results(MatchLevel.SamePhoto, TwoGroups, null, @"D:\Unplugged");

        Assert.True(results.HasUnreadableFolders);
        Assert.Equal([@"D:\Unplugged"], results.UnreadableFolders);
    }

    [Fact]
    public void Back_goes_back()
    {
        var backs = 0;
        Results(MatchLevel.SamePhoto, TwoGroups, () => backs++).BackCommand.Execute(null);

        Assert.Equal(1, backs);
    }
}
