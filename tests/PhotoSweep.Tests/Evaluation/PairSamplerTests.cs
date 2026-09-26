using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Eval.Precision;
using static PhotoSweep.Tests.Grouping.Files;

namespace PhotoSweep.Tests.Evaluation;

public class PairSamplerTests
{
    [Fact]
    public void Pairs_come_from_keeper_and_look_alike_members_but_not_byte_identical_copies()
    {
        ScannedFile[] files =
        [
            Photo("a.jpg", sha: "S1", width: 4000, height: 3000), // clearly the keeper
            Photo("a copy.jpg", sha: "S1", width: 4000, height: 3000),
            Photo("b.jpg", pHash: Bits(3)),
            Photo("c.jpg", pHash: Bits(6), dHash: Bits(1)),
        ];
        var groups = DuplicateGrouper.Group(files, new MatchThresholds(8, 8));

        var pairs = PairSampler.FromGroups(groups);

        Assert.Equal(
            [("a.jpg", "b.jpg", 3, 0), ("a.jpg", "c.jpg", 6, 1)],
            pairs.Select(p => (Path.GetFileName(p.Keeper.Path), Path.GetFileName(p.Member.Path), p.PHash, p.DHash)).Order());
    }

    [Fact]
    public void Stratify_draws_up_to_the_same_number_per_bucket_and_records_each_population()
    {
        var pairs = Pairs(Enumerable.Repeat(1, 50).Concat(Enumerable.Repeat(4, 10)).Concat(Enumerable.Repeat(6, 2)).Append(12));

        var sample = PairSampler.Stratify(pairs, perBucket: 5, seed: 7);

        Assert.Equal([50, 10, 2, 0], sample.Buckets.Select(b => b.Population));
        Assert.Equal([5, 5, 2, 0], sample.Buckets.Select(b => sample.Pairs.Count(p => p.Bucket == b)));
        Assert.Equal(1, sample.OutsideBuckets); // the 12-bit pair
        Assert.All(sample.Pairs, p => Assert.True(p.Bucket.Contains(p.Pair.PHash, p.Pair.DHash)));
    }

    [Fact]
    public void Stratify_is_deterministic_and_independent_of_input_order()
    {
        var pairs = Pairs(Enumerable.Range(0, 40).Select(i => i % 9));
        var reversed = Enumerable.Reverse(pairs).ToList();

        var a = PairSampler.Stratify(pairs, 3, seed: 11).Pairs.Select(p => p.Pair.Member.Path);
        var b = PairSampler.Stratify(reversed, 3, seed: 11).Pairs.Select(p => p.Pair.Member.Path);
        var c = PairSampler.Stratify(pairs, 3, seed: 12).Pairs.Select(p => p.Pair.Member.Path);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Default_buckets_are_0_2_3_4_5_6_7_8_by_the_larger_distance()
    {
        Assert.Equal([(0, 2), (3, 4), (5, 6), (7, 8)], PairSampler.DefaultBuckets.Select(b => (b.Min, b.Max)));

        var sample = PairSampler.Stratify([Pair("x.jpg", pHash: 2, dHash: 5)], 1, 1);

        Assert.Equal("5–6", sample.Pairs.Single().Bucket.Name);
    }

    private static List<CandidatePair> Pairs(IEnumerable<int> distances) =>
        distances.Select((d, i) => Pair($"m{i:D3}.jpg", d, d)).ToList();

    private static CandidatePair Pair(string member, int pHash, int dHash) =>
        new(Photo("keeper.jpg"), Photo(member), pHash, dHash);
}
