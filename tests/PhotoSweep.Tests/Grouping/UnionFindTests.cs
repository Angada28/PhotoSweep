using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Tests.Grouping;

public class UnionFindTests
{
    [Fact]
    public void Every_id_starts_in_its_own_set()
    {
        var sets = new UnionFind(3);

        Assert.Equal([0, 1, 2], Enumerable.Range(0, 3).Select(sets.Find));
        Assert.Equal(3, sets.Sets().Count);
    }

    [Fact]
    public void Union_is_transitive()
    {
        var sets = new UnionFind(5);

        sets.Union(0, 1);
        sets.Union(3, 1);

        Assert.Equal(sets.Find(0), sets.Find(3));
        Assert.NotEqual(sets.Find(0), sets.Find(2));
    }

    [Fact]
    public void Union_reports_whether_anything_changed()
    {
        var sets = new UnionFind(3);

        Assert.True(sets.Union(0, 1));
        Assert.False(sets.Union(1, 0));
        Assert.False(sets.Union(2, 2));
    }

    [Fact]
    public void Sets_lists_members_in_order_and_sets_by_smallest_member()
    {
        var sets = new UnionFind(6);
        sets.Union(4, 1);
        sets.Union(5, 0);
        sets.Union(1, 3);

        var result = sets.Sets().Select(s => s.ToArray()).ToArray();

        Assert.Equal([[0, 5], [1, 3, 4], [2]], result);
    }

    [Fact]
    public void Long_chain_collapses_into_one_set()
    {
        const int n = 10_000;
        var sets = new UnionFind(n);
        for (var i = 1; i < n; i++)
            sets.Union(i - 1, i);

        var root = sets.Find(0);
        Assert.All(Enumerable.Range(0, n), i => Assert.Equal(root, sets.Find(i)));
    }

    [Fact]
    public void Empty_is_allowed()
    {
        Assert.Empty(new UnionFind(0).Sets());
    }
}
