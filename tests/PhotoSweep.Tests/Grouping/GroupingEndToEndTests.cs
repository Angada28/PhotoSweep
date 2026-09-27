using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Tests.Scanning;
using Xunit.Abstractions;

namespace PhotoSweep.Tests.Grouping;

// Real files through the real pipeline: PhotoScanner (SHA-256, fingerprints, details) → DuplicateGrouper.
public class GroupingEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] Subjects = ["astronaut", "chelsea", "coffee"];

    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private Task<ScanResult> Scan() =>
        new PhotoScanner(ScanCache.Load(_temp.CachePath)).ScanAsync(new ScanOptions { Folders = [_temp.Root] });

    private static string Name(GroupMember m) => Path.GetFileName(m.File.Path);

    /// <summary>
    /// All 18 TestData photos, plus a byte-for-byte copy with a copy-style name and a pair of identical HEICs.
    /// Every file gets the same timestamp, so the keeper doesn't depend on when the test files were written.
    /// </summary>
    private void AddLibrary()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "Photos"), "*.*g"))
            _temp.AddPhoto(Path.GetFileName(file));

        _temp.AddPhoto("astronaut.jpg", "astronaut (1).jpg");
        var heic = "ftypheic not really a photo"u8.ToArray(); // undecodable: only SHA-256 can group these
        _temp.AddBytes("phone.heic", heic);
        _temp.AddBytes(@"backup\phone.heic", heic);

        foreach (var file in Directory.GetFiles(_temp.Root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(MatchLevel.SamePhoto)]
    [InlineData(MatchLevel.Similar)] // looser, but unrelated photos (24+ bits apart) must still not merge
    public async Task Each_subject_forms_one_group_and_heic_copies_form_another(MatchLevel level)
    {
        AddLibrary();
        var scan = await Scan();

        var groups = DuplicateGrouper.Group(scan, level);
        foreach (var g in groups)
            output.WriteLine($"{Name(g.Keeper)}: {g.KeeperReason} | {string.Join(", ", g.Members.Skip(1).Select(m => $"{Name(m)} {m.Kind} p{m.PHashDistance} d{m.DHashDistance}"))}");

        Assert.Equal(4, groups.Count);
        foreach (var subject in Subjects)
        {
            var group = Assert.Single(groups, g => Name(g.Keeper).StartsWith(subject));
            Assert.All(group.Members, m => Assert.StartsWith(subject, Name(m)));
            Assert.Equal(subject == "astronaut" ? 7 : 6, group.Members.Count);

            // Resolution rules out _half/_harsh, the name rules out "(1)", size (within format, 1% tolerance) rules
            // out _q50 and ties the rest; with equal timestamps the path decides, and ".jpg" sorts before "_exif6".
            Assert.Equal(subject + ".jpg", Name(group.Keeper));

            foreach (var member in group.Members.Skip(1))
            {
                var sameBytes = member.File.Sha256 == group.Keeper.File.Sha256;
                // Different bytes: the edits are close to the keeper, so SamePhoto, never merely Similar.
                Assert.Equal(sameBytes ? MatchKind.Identical : MatchKind.SamePhoto, member.Kind);
            }
        }

        var heicGroup = Assert.Single(groups, g => Name(g.Keeper) == "phone.heic");
        Assert.Equal(2, heicGroup.Members.Count);
        Assert.Equal(MatchKind.Identical, heicGroup.Members[1].Kind);
    }

    [Fact]
    public async Task Exact_level_finds_only_the_byte_identical_copies()
    {
        AddLibrary();

        var groups = DuplicateGrouper.Group(await Scan(), MatchLevel.Exact);

        Assert.Equal(2, groups.Count);
        var astronaut = Assert.Single(groups, g => Name(g.Keeper).StartsWith("astronaut"));
        Assert.Equal(["astronaut.jpg", "astronaut (1).jpg"], astronaut.Members.Select(Name));
        Assert.Equal(MatchKind.Identical, astronaut.Members[1].Kind);
        Assert.Equal(KeeperRanker.IdenticalReason, astronaut.KeeperReason);
        Assert.Single(groups, g => Name(g.Keeper) == "phone.heic");
    }

    [Fact]
    public async Task Online_only_file_with_cached_data_still_groups_with_its_local_duplicate()
    {
        _temp.AddPhoto("chelsea.jpg");
        var cloudCopy = _temp.AddPhoto("chelsea.jpg", @"OneDrive\chelsea.jpg");
        _temp.AddPhoto("chelsea_half.jpg");
        await Scan(); // both read while still local

        // Like OneDrive "free up space": same size and timestamp, but now online-only. Opening it would download it.
        File.SetAttributes(cloudCopy, FileAttributes.Offline);
        var scan = await Scan();

        var cloud = scan.Files.Single(f => f.Path == cloudCopy);
        Assert.Equal(ScanStatus.Ok, cloud.Status); // served from the cache, not skipped and not re-read
        Assert.Empty(scan.OnlineOnlySkipped);
        Assert.Equal(3, scan.CacheHits);

        var exact = Assert.Single(DuplicateGrouper.Group(scan, MatchLevel.Exact));
        Assert.Contains(exact.Members, m => m.File.Path == cloudCopy && m.Kind is MatchKind.Keeper or MatchKind.Identical);

        var samePhoto = Assert.Single(DuplicateGrouper.Group(scan, MatchLevel.SamePhoto));
        Assert.Equal(3, samePhoto.Members.Count);
        Assert.Contains(samePhoto.Members, m => m.File.Path == cloudCopy);
    }

    [Fact]
    public async Task Online_only_file_never_scanned_is_left_out_of_groups()
    {
        _temp.AddPhoto("coffee.jpg");
        var cloudCopy = _temp.AddPhoto("coffee.jpg", @"OneDrive\coffee.jpg");
        File.SetAttributes(cloudCopy, FileAttributes.Offline); // online-only before the first scan: no data at all

        var scan = await Scan();

        Assert.Equal(cloudCopy, Assert.Single(scan.OnlineOnlySkipped).Path);
        Assert.Empty(DuplicateGrouper.Group(scan, MatchLevel.Similar));
    }
}
