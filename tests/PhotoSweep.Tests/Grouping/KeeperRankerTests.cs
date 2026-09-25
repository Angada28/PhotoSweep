using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Grouping;

// Each test makes one criterion the deciding one; everything else is equal (Files.Photo defaults) or loses on
// a later criterion, which proves the order.
public class KeeperRankerTests
{
    private static (string Keeper, string Reason) Rank(params ScannedFile[] files)
    {
        var (ranked, reason) = KeeperRanker.Rank(files);
        return (Path.GetFileName(ranked[0].Path), reason);
    }

    [Fact]
    public void Higher_resolution_wins_over_everything_below_it()
    {
        var (keeper, reason) = Rank(
            Photo("small.jpg", width: 800, height: 600, camera: "Pixel 8", size: 900_000, modified: DefaultTime.AddYears(-5)),
            Photo("big (1).jpg", width: 4000, height: 3000));

        Assert.Equal("big (1).jpg", keeper);
        Assert.Equal("Highest resolution (4000×3000 vs 800×600)", reason);
    }

    [Fact]
    public void Resolution_within_one_percent_is_a_tie()
    {
        var (keeper, reason) = Rank(
            Photo("cropped.jpg", width: 4000, height: 2990),
            Photo("camera.jpg", width: 4000, height: 3000, camera: "Canon EOS R5"));
        Assert.Equal("camera.jpg", keeper);
        Assert.Equal("Has camera data (Canon EOS R5)", reason);

        // Same files, but now the larger one is more than 1% larger: resolution decides.
        (keeper, _) = Rank(
            Photo("bigger.jpg", width: 4000, height: 3100),
            Photo("camera.jpg", width: 4000, height: 3000, camera: "Canon EOS R5"));
        Assert.Equal("bigger.jpg", keeper);
    }

    [Fact]
    public void Camera_data_beats_an_original_looking_name()
    {
        var (keeper, reason) = Rank(Photo("IMG_1.jpg"), Photo("IMG_1 (1).jpg", camera: "iPhone 15"));

        Assert.Equal("IMG_1 (1).jpg", keeper);
        Assert.StartsWith("Has camera data", reason);
    }

    [Fact]
    public void Copy_style_name_loses_even_to_a_smaller_file()
    {
        var (keeper, reason) = Rank(Photo("IMG_1 - Copy.jpg", size: 500_000), Photo("IMG_1.jpg", size: 100_000));

        Assert.Equal("IMG_1.jpg", keeper);
        Assert.Equal("Original file name (\"IMG_1 - Copy.jpg\" looks like a copy)", reason);
    }

    [Theory]
    [InlineData("IMG_1234 (1)", true)]
    [InlineData("IMG_1234 (12)", true)]
    [InlineData("Copy of IMG_1234", true)]
    [InlineData("IMG_1234 - Copy", true)]
    [InlineData("IMG_1234 - Copy (2)", true)]
    [InlineData("IMG_1234_copy", true)]
    [InlineData("holiday_edited", true)]
    [InlineData("holiday-resized", true)]
    [InlineData("holiday exported", true)]
    [InlineData("IMG_1234", false)]
    [InlineData("copyright notice", false)]
    [InlineData("photocopy", false)]
    [InlineData("2019-07-14 beach", false)]
    public void Recognises_copy_style_names(string stem, bool looksLikeCopy)
    {
        Assert.Equal(looksLikeCopy, KeeperRanker.LooksLikeCopy($@"C:\Photos\{stem}.jpg"));
    }

    [Fact]
    public void Folder_names_do_not_count_as_copy_names()
    {
        Assert.False(KeeperRanker.LooksLikeCopy(@"C:\Photos\Copy of old drive\IMG_1.jpg"));
    }

    [Fact]
    public void Larger_file_wins_within_the_same_format()
    {
        var (keeper, reason) = Rank(
            Photo("q50.jpg", size: 200 * 1024, modified: DefaultTime.AddDays(-1)),
            Photo("q90.jpeg", size: 1_500 * 1024)); // .jpeg and .jpg are the same format

        Assert.Equal("q90.jpeg", keeper);
        Assert.Equal("Larger file, less compressed (1.5 MB vs 200 KB)", reason);
    }

    [Fact]
    public void Size_within_one_percent_is_a_tie()
    {
        // Like the TestData _exif6 copies: same pixels and quality, a few bytes more for the EXIF block.
        var (keeper, reason) = Rank(
            Photo("with_exif.jpg", size: 25_598),
            Photo("original.jpg", size: 25_553, modified: DefaultTime.AddDays(-1)));

        Assert.Equal("original.jpg", keeper);
        Assert.StartsWith("Older copy", reason);
    }

    [Fact]
    public void File_size_is_not_compared_across_formats()
    {
        // The PNG is 10x bigger but that's the format, not quality, so age decides.
        var (keeper, reason) = Rank(
            Photo("converted.png", size: 5_000_000),
            Photo("original.jpg", size: 500_000, modified: DefaultTime.AddYears(-1)));

        Assert.Equal("original.jpg", keeper);
        Assert.StartsWith("Older copy (modified 2023-", reason);
    }

    [Fact]
    public void Older_file_wins_when_all_else_is_equal()
    {
        var (keeper, _) = Rank(Photo("new.jpg"), Photo("old.jpg", modified: DefaultTime.AddHours(-3)));

        Assert.Equal("old.jpg", keeper);
    }

    [Fact]
    public void Less_than_a_second_older_is_not_older()
    {
        var (keeper, reason) = Rank(
            Photo("b.jpg", modified: DefaultTime.AddMilliseconds(100)),
            Photo("a.jpg", modified: DefaultTime.AddMilliseconds(900)));

        Assert.Equal("a.jpg", keeper);
        Assert.Equal("Equally good copies; first by path", reason);
    }

    [Fact]
    public void Identical_copies_are_explained_as_such()
    {
        var (keeper, reason) = Rank(Photo(@"b\IMG_1.jpg", sha: "S"), Photo(@"a\IMG_1.jpg", sha: "S"));

        Assert.Equal("IMG_1.jpg", keeper);
        Assert.Equal(KeeperRanker.IdenticalReason, reason);
    }

    [Fact]
    public void Reason_compares_with_the_best_copy_that_is_not_identical()
    {
        // The runner-up is the keeper's byte-identical twin (it only loses on path). Explaining against it would
        // say "first by path"; the useful reason is why it beat the smaller version.
        var (ranked, reason) = KeeperRanker.Rank(
        [
            Photo("small.jpg", width: 500, height: 375),
            Photo(@"b\IMG_1.jpg", sha: "S"),
            Photo(@"a\IMG_1.jpg", sha: "S"),
        ]);

        Assert.Equal([@"C:\Photos\a\IMG_1.jpg", @"C:\Photos\b\IMG_1.jpg", @"C:\Photos\small.jpg"], ranked.Select(f => f.Path));
        Assert.Equal("Highest resolution (1000×750 vs 500×375)", reason);
    }

    [Fact]
    public void Undecodable_files_rank_on_the_remaining_criteria()
    {
        var (keeper, reason) = Rank(Undecodable("IMG_1 (1).heic", "A"), Undecodable("IMG_1.heic", "B"));

        Assert.Equal("IMG_1.heic", keeper);
        Assert.StartsWith("Original file name", reason);
    }

    [Fact]
    public void Ranking_is_the_same_whatever_the_input_order()
    {
        ScannedFile[] files =
        [
            Photo("a.jpg", width: 4000, height: 2990),
            Photo("b.jpg", width: 4000, height: 3000),
            Photo("c.jpg", width: 4000, height: 2995, camera: "X"),
            Photo("d.png", size: 1),
            Photo("e (1).jpg"),
        ];

        var forward = KeeperRanker.Rank(files).Ranked.Select(f => f.Path);
        var backward = KeeperRanker.Rank(files.Reverse().ToArray()).Ranked.Select(f => f.Path);

        Assert.Equal(forward, backward);
    }
}
