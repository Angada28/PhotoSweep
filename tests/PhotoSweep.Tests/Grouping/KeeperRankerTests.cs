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
    [InlineData("IMG_1234.0", true)]
    [InlineData("IMG_1234.1", true)]
    [InlineData("20190714_160231.0", true)]
    [InlineData("Zelda_Breath_of_the_Wild_all_shrines_map_Champions_Ballad_DLC.0", true)] // real name (sweep-test)
    [InlineData("IMG_1234", false)]
    // Real names from sweep-test with dots that aren't copy markers.
    [InlineData("2016-03-09 21.03.33", false)]
    [InlineData("Sugoroku.Hajime.600.1995993", false)]
    [InlineData("Legend of Zelda, The - The Minish Cap (U).st1", false)]
    [InlineData("Screen_Shot_2017-11-06_at_12.41.31_PM", false)]
    [InlineData("IMG_0508.JPG", false)] // "IMG_0508.JPG.jpg": the stem ends in letters
    [InlineData("720px-Huffman_coding_visualisation.svg", false)]
    // Dotted dates, versions, numbers and two-digit suffixes.
    [InlineData("14.07.2019", false)]
    [InlineData("2019.07.4", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("3.5", false)]
    [InlineData("IMG.10", false)]
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

    // Each pair differs by more than the 1% tie margin, so size decides; the first two both round to "1.3 MB".
    [Theory]
    [InlineData(1_405_092, 1_352_663, "1.34 MB vs 1.29 MB")]
    [InlineData(1_363_149, 1_342_177, "1.30 MB vs 1.28 MB")] // trailing zero kept so the two line up
    [InlineData(1_048_576_000, 1_027_604_480, "1000 MB vs 980 MB")] // one decimal already differs: unchanged
    public void Sizes_that_round_the_same_get_more_decimals(long larger, long smaller, string expected)
    {
        var (_, reason) = Rank(Photo("a.jpg", size: smaller), Photo("b.jpg", size: larger));

        Assert.Equal($"Larger file, less compressed ({expected})", reason);
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

    // ---- Compare: the pairwise view the compare window uses ----

    [Fact]
    public void Compare_agrees_with_the_ranking_for_every_pair()
    {
        ScannedFile[] files =
        [
            Photo("a.jpg", width: 4000, height: 2990),
            Photo("b.jpg", width: 4000, height: 3000),
            Photo("c.jpg", width: 4000, height: 2995, camera: "X"),
            Photo("d.png", size: 1),
            Photo("e (1).jpg"),
            Photo("f.jpg", size: 99_500),
            Photo("g.jpg", modified: DefaultTime.AddDays(-3)),
            Photo("h.jpg", width: 800, height: 600, camera: "Y"),
            Photo("i.png", width: 4000, height: 3000, size: 5_000_000),
        ];
        var ranked = KeeperRanker.Rank(files).Ranked.ToList();

        foreach (var a in files)
        {
            foreach (var b in files.Where(f => f != a))
            {
                var aFirst = ranked.IndexOf(a) < ranked.IndexOf(b);
                Assert.Equal(aFirst ? -1 : 1, KeeperRanker.Compare(a, b, files).Winner);
            }
        }
    }

    [Fact]
    public void Compare_reports_each_criterion_on_its_own()
    {
        var left = Photo("IMG_1 (1).jpg", width: 4000, height: 3000, size: 900_000);
        var right = Photo("IMG_1.jpg", width: 800, height: 600, camera: "Pixel 8", size: 100_000, modified: DefaultTime.AddYears(-1));

        var comparison = KeeperRanker.Compare(left, right);

        Assert.Equal(-1, comparison.Winner);
        Assert.Equal(KeeperCriterion.Resolution, comparison.DecidedBy);
        Assert.Equal("Highest resolution (4000×3000 vs 800×600)", comparison.Reason);
        Assert.Equal(-1, comparison.ByCriterion[KeeperCriterion.Resolution]);
        Assert.Equal(1, comparison.ByCriterion[KeeperCriterion.CameraData]);
        Assert.Equal(1, comparison.ByCriterion[KeeperCriterion.OriginalName]);
        Assert.Equal(-1, comparison.ByCriterion[KeeperCriterion.FileSize]);
        Assert.Equal(1, comparison.ByCriterion[KeeperCriterion.OlderCopy]);
    }

    [Fact]
    public void Compare_explains_from_the_winners_side()
    {
        var comparison = KeeperRanker.Compare(Photo("small.jpg", width: 800, height: 600), Photo("big.jpg", width: 4000, height: 3000));

        Assert.Equal(1, comparison.Winner);
        Assert.Equal("Highest resolution (4000×3000 vs 800×600)", comparison.Reason);
    }

    [Fact]
    public void Compare_only_weighs_file_size_within_the_same_format()
    {
        var comparison = KeeperRanker.Compare(Photo("a.jpg", size: 1_000_000), Photo("a.png", size: 5_000_000));

        Assert.Equal(0, comparison.ByCriterion[KeeperCriterion.FileSize]);
    }

    [Fact]
    public void Compare_measures_against_the_context_like_the_grouper_does()
    {
        // wide is within 1% of the largest in the set, narrow isn't; on their own, both are "largest".
        var wide = Photo("wide.jpg", width: 1000, height: 1000);
        var narrow = Photo("narrow.jpg", width: 995, height: 1000, camera: "Pixel 8");
        var outsider = Photo("outsider.jpg", width: 1010, height: 1000);

        Assert.Equal(1, KeeperRanker.Compare(wide, narrow).Winner);
        Assert.Equal(KeeperCriterion.CameraData, KeeperRanker.Compare(wide, narrow).DecidedBy);

        var inSet = KeeperRanker.Compare(wide, narrow, [wide, narrow, outsider]);
        Assert.Equal(-1, inSet.Winner);
        Assert.Equal(KeeperCriterion.Resolution, inSet.DecidedBy);
        Assert.Equal(wide, KeeperRanker.Rank([wide, narrow], [wide, narrow, outsider]).Ranked[0]);
    }

    [Fact]
    public void Compare_calls_byte_identical_files_identical()
    {
        var comparison = KeeperRanker.Compare(Photo("a.jpg", sha: "S"), Photo("a copy.jpg", sha: "S"));

        Assert.Equal(-1, comparison.Winner);
        Assert.Equal(KeeperRanker.IdenticalReason, comparison.Reason);
    }
}
