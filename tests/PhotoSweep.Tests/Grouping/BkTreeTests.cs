using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Tests.Grouping;

public class BkTreeTests
{
    // Uniformly random hashes are ~32 bits apart, so most small-radius queries would find nothing. The data
    // below therefore also has clusters (a few bits flipped) and exact duplicates, to exercise every case.
    private static List<ulong> MakeHashes(Random random)
    {
        var hashes = new List<ulong>();
        for (var i = 0; i < 300; i++)
        {
            var hash = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
            hashes.Add(hash);

            if (i % 3 == 0)
                hashes.Add(FlipBits(hash, random.Next(1, 16), random));
            if (i % 10 == 0)
                hashes.Add(hash); // exact duplicate
        }

        return hashes;
    }

    private static ulong FlipBits(ulong hash, int count, Random random)
    {
        foreach (var bit in Enumerable.Range(0, 64).OrderBy(_ => random.Next()).Take(count))
            hash ^= 1UL << bit;
        return hash;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(32)]
    [InlineData(64)]
    public void Query_matches_brute_force(int radius)
    {
        var random = new Random(12345); // fixed seed: a failure is reproducible
        var hashes = MakeHashes(random);
        var tree = new BkTree();
        for (var id = 0; id < hashes.Count; id++)
            tree.Add(hashes[id], id);

        var queries = hashes.Take(100)
            .Concat(hashes.Take(50).Select(h => FlipBits(h, random.Next(1, 20), random)))
            .Concat(Enumerable.Range(0, 50).Select(_ => (ulong)random.NextInt64()));

        foreach (var query in queries)
        {
            var expected = Enumerable.Range(0, hashes.Count).Where(id => Hamming.Distance(query, hashes[id]) <= radius);
            Assert.Equal(expected.Order(), tree.Query(query, radius).Order());
        }

        Assert.Equal(hashes.Count, tree.Count);
    }

    [Fact]
    public void Duplicate_hashes_all_come_back()
    {
        var tree = new BkTree();
        tree.Add(42, 1);
        tree.Add(42, 2);
        tree.Add(43, 3);

        Assert.Equal([1, 2], tree.Query(42, 0).Order());
        Assert.Equal([1, 2, 3], tree.Query(42, 1).Order());
    }

    [Fact]
    public void Empty_tree_returns_nothing()
    {
        Assert.Empty(new BkTree().Query(0, 64));
    }

    [Fact]
    public void Negative_radius_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BkTree().Query(0, -1));
    }
}
