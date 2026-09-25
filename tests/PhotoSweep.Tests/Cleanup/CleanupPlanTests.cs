using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Scanning;
using static PhotoSweep.Tests.Cleanup.Groups;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Cleanup;

// Validation only looks at paths and groups, so these use made-up files under C:\Photos and never touch the disk.
public class CleanupPlanTests
{
    private static readonly string[] Roots = [@"C:\Photos"];

    [Fact]
    public void Selecting_every_copy_in_a_group_refuses_the_whole_clean_up()
    {
        var a = Photo("a.jpg", sha: "S1");
        var aCopy = Photo("a copy.jpg", sha: "S1");
        var b = Photo("b.jpg");
        var bCopy = Photo("b copy.jpg");

        var result = CleanupPlan.Validate([Of(a, aCopy), Of(b, bCopy)], [a.Path, aCopy.Path, bCopy.Path], Roots);

        Assert.False(result.IsValid);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(CleanupProblemKind.RemovesEveryCopy, problem.Kind);
        Assert.Equal(a.Path, problem.Path); // reported against the keeper; group b alone would have been fine
    }

    [Fact]
    public void Keeping_one_copy_is_accepted_and_the_plan_records_what_stays()
    {
        var keeper = Photo("a.jpg");
        var copy1 = Photo(@"backup\a.jpg");
        var copy2 = Photo("a (1).jpg");
        var untouched = Of(Photo("b.jpg"), Photo("b copy.jpg"));

        var result = CleanupPlan.Validate([Of(keeper, copy1, copy2), untouched], [copy1.Path, copy2.Path], Roots);

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
        var group = Assert.Single(result.Plan.Groups); // groups with nothing selected aren't in the plan
        Assert.Equal([keeper], group.Keep);
        Assert.Equal([@"backup\a.jpg", "a (1).jpg"], group.Remove.Select(m => m.RelativePath));
        Assert.All(group.Remove, m => Assert.Equal(@"C:\Photos", m.Root));
        Assert.Equal(2, result.Plan.FileCount);
    }

    [Fact]
    public void A_path_that_is_in_no_group_is_refused()
    {
        var group = Of(Photo("a.jpg"), Photo("a copy.jpg"));

        var result = CleanupPlan.Validate([group], [@"C:\Photos\random.jpg"], Roots);

        Assert.Equal(CleanupProblemKind.NotInAnyGroup, Assert.Single(result.Problems).Kind);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void A_file_outside_the_scanned_folders_is_refused()
    {
        var copy = Photo("a copy.jpg");

        var result = CleanupPlan.Validate([Of(Photo("a.jpg"), copy)], [copy.Path], [@"D:\Other"]);

        Assert.Equal(CleanupProblemKind.OutsideScannedFolders, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void A_file_already_in_a_review_folder_is_refused()
    {
        var removed = Photo($@"{ScanOptions.ReviewFolderName}\2026-01-01 10.00.00\a.jpg");

        var result = CleanupPlan.Validate([Of(Photo("a.jpg"), removed)], [removed.Path], Roots);

        Assert.Equal(CleanupProblemKind.InsideReviewFolder, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Nested_roots_use_the_innermost_one()
    {
        var copy = Photo(@"Phone\2024\a.jpg");

        var result = CleanupPlan.Validate([Of(Photo("a.jpg"), copy)], [copy.Path], [@"C:\Photos", @"C:\Photos\Phone\"]);

        var move = Assert.Single(Assert.Single(result.Plan!.Groups).Remove);
        Assert.Equal(@"C:\Photos\Phone", move.Root);
        Assert.Equal(@"2024\a.jpg", move.RelativePath);
    }

    [Fact]
    public void Paths_match_case_insensitively()
    {
        var copy = Photo("a copy.jpg");

        var result = CleanupPlan.Validate([Of(Photo("a.jpg"), copy)], [copy.Path.ToUpperInvariant()], Roots);

        Assert.True(result.IsValid);
    }
}
