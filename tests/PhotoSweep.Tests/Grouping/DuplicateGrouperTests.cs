using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Grouping;

public class DuplicateGrouperTests
{
    private static string[] Names(PhotoGroup group) => group.Members.Select(m => Path.GetFileName(m.File.Path)).ToArray();

    private static MatchKind KindOf(PhotoGroup group, string name) =>
        group.Members.Single(m => Path.GetFileName(m.File.Path) == name).Kind;

    [Fact]
    public void Exact_level_groups_only_byte_identical_files()
    {
        ScannedFile[] files =
        [
            Photo("a.jpg", sha: "S1"),
            Photo("a copy.jpg", sha: "S1"),
            Photo("a_resized.jpg", sha: "S2"), // same fingerprint (0/0), different bytes
        ];

        var group = Assert.Single(DuplicateGrouper.Group(files, MatchLevel.Exact));

        Assert.Equal(["a.jpg", "a copy.jpg"], Names(group));
        Assert.Equal(MatchKind.Identical, KindOf(group, "a copy.jpg"));
        Assert.Equal(KeeperRanker.IdenticalReason, group.KeeperReason);
    }

    [Fact]
    public void SamePhoto_level_adds_look_alikes_to_the_exact_copies()
    {
        ScannedFile[] files =
        [
            Photo("a.jpg", sha: "S1"),
            Photo("a copy.jpg", sha: "S1"),
            Photo("a_resized.jpg", pHash: Bits(3), dHash: Bits(2), sha: "S2", width: 500, height: 375),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(files, MatchLevel.SamePhoto));

        Assert.Equal(3, group.Members.Count);
        var resized = group.Members.Single(m => m.Kind == MatchKind.SamePhoto);
        Assert.Equal((3, 2), (resized.PHashDistance, resized.DHashDistance));
        Assert.StartsWith("Highest resolution (1000×750 vs 500×375)", group.KeeperReason);
    }

    [Fact]
    public void Undecodable_files_are_grouped_by_sha256()
    {
        ScannedFile[] files = [Undecodable("IMG_1.heic", "H1"), Undecodable(@"backup\IMG_1.heic", "H1"), Undecodable("IMG_2.heic", "H2")];

        foreach (var level in Enum.GetValues<MatchLevel>())
        {
            var group = Assert.Single(DuplicateGrouper.Group(files, level));
            Assert.All(group.Members.Skip(1), m => Assert.Equal(MatchKind.Identical, m.Kind));
            Assert.Equal(2, group.Members.Count);
            Assert.All(group.Members, m => Assert.Null(m.PHashDistance));
        }
    }

    // SamePhoto limits are 8/8. A match needs pHash AND dHash within their limits.
    [Theory]
    [InlineData(8, 8, true)]
    [InlineData(3, 3, true)]
    [InlineData(9, 0, false)] // pHash too far, even though dHash is a perfect match
    [InlineData(0, 9, false)] // dHash too far: found by the pHash tree query, then rejected
    [InlineData(20, 20, false)]
    public void Both_hashes_must_agree(int pDistance, int dDistance, bool grouped)
    {
        ScannedFile[] files = [Photo("a.jpg"), Photo("b.jpg", pHash: Bits(pDistance), dHash: Bits(dDistance))];

        var groups = DuplicateGrouper.Group(files, MatchLevel.SamePhoto);

        Assert.Equal(grouped ? 1 : 0, groups.Count);
    }

    [Fact]
    public void Similar_level_finds_looser_matches_and_labels_them_similar()
    {
        ScannedFile[] files = [Photo("a.jpg"), Photo("b.jpg", pHash: Bits(12), dHash: Bits(12))];

        Assert.Empty(DuplicateGrouper.Group(files, MatchLevel.SamePhoto));
        var group = Assert.Single(DuplicateGrouper.Group(files, MatchLevel.Similar));
        Assert.Equal(MatchKind.Similar, group.Members[1].Kind);
    }

    [Fact]
    public void Matches_chain_into_one_group_and_the_far_end_is_labelled_similar()
    {
        // a–b and b–c are 6 bits apart (within SamePhoto's 8); a–c is 12 bits apart.
        ScannedFile[] files =
        [
            Photo("a.jpg", width: 2000, height: 1500), // highest resolution, so the keeper
            Photo("b.jpg", pHash: Bits(6), dHash: Bits(6)),
            Photo("c.jpg", pHash: Bits(12), dHash: Bits(12)),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(files, MatchLevel.SamePhoto));

        Assert.Equal("a.jpg", Path.GetFileName(group.Keeper.File.Path));
        Assert.Equal(MatchKind.SamePhoto, KindOf(group, "b.jpg"));
        Assert.Equal(MatchKind.Similar, KindOf(group, "c.jpg"));
        Assert.Equal(12, group.Members.Single(m => m.File.Path.EndsWith("c.jpg")).PHashDistance);
    }

    [Fact]
    public void Files_without_any_data_are_left_out()
    {
        var neverRead = new ScannedFile(@"C:\Photos\cloud.jpg", 100_000, DefaultTime) { Status = ScanStatus.OnlineOnlySkipped };
        var locked = new ScannedFile(@"C:\Photos\locked.jpg", 100_000, DefaultTime) { Status = ScanStatus.Unreadable, Error = "locked" };
        ScannedFile[] files = [Photo("a.jpg"), neverRead, locked];

        Assert.Empty(DuplicateGrouper.Group(files, MatchLevel.Similar));
    }

    [Fact]
    public void Any_file_with_data_takes_part_whatever_its_status()
    {
        // E.g. a fingerprint without a hash, and a file whose later step failed after hashing.
        var fingerprintOnly = Photo("b.jpg") with { Sha256 = null };
        var hashedThenFailed = Undecodable("c.jpg", sha: "SHA-a.jpg") with { Status = ScanStatus.UnexpectedError };
        ScannedFile[] files = [Photo("a.jpg"), fingerprintOnly, hashedThenFailed];

        var group = Assert.Single(DuplicateGrouper.Group(files, MatchLevel.SamePhoto));

        Assert.Equal(3, group.Members.Count);
        Assert.Equal(MatchKind.Identical, KindOf(group, "c.jpg"));
    }

    [Fact]
    public void Output_does_not_depend_on_input_order()
    {
        ScannedFile[] files =
        [
            Photo("x1.jpg", pHash: 0xFFFF_0000_FFFF_0000, dHash: 0xFFFF_0000_FFFF_0000),
            Photo("x2.jpg", pHash: 0xFFFF_0000_FFFF_0001, dHash: 0xFFFF_0000_FFFF_0000),
            Photo("a1.jpg"),
            Photo("a2.jpg", pHash: Bits(1)),
            Photo("a3.jpg", sha: "SHA-a1.jpg"),
            Photo("lonely.jpg", pHash: 0x0F0F_0F0F_0F0F_0F0F, dHash: 0x0F0F_0F0F_0F0F_0F0F),
        ];

        static string Describe(IReadOnlyList<PhotoGroup> groups) =>
            string.Join(" | ", groups.Select(g => string.Join(",", Names(g)) + ":" + g.KeeperReason));

        var expected = Describe(DuplicateGrouper.Group(files, MatchLevel.SamePhoto));
        Assert.Equal(expected, Describe(DuplicateGrouper.Group(files.Reverse(), MatchLevel.SamePhoto)));
        Assert.Equal(expected, Describe(DuplicateGrouper.Group(files.OrderBy(f => f.Sha256), MatchLevel.SamePhoto)));
        Assert.Equal("a1.jpg,a2.jpg,a3.jpg", expected.Split(':')[0]); // all equally good, so by path; groups sorted by keeper path
    }

    [Fact]
    public void No_files_means_no_groups()
    {
        Assert.Empty(DuplicateGrouper.Group([], MatchLevel.Similar));
    }

    [Fact]
    public void Placeholder_thresholds_keep_levels_nested()
    {
        Assert.Null(MatchThresholds.For(MatchLevel.Exact));
        Assert.True(MatchThresholds.SamePhoto.PHashRadius <= MatchThresholds.Similar.PHashRadius);
        Assert.True(MatchThresholds.SamePhoto.DHashLimit <= MatchThresholds.Similar.DHashLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchThresholds.For((MatchLevel)99));
    }
}
