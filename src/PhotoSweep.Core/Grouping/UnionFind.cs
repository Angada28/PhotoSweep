namespace PhotoSweep.Core.Grouping;

/// <summary>
/// Disjoint-set over the ids 0..count-1. Starts with every id in its own set; <see cref="Union"/> merges two sets.
/// Used to turn pairwise matches (A~B, B~C) into groups ({A, B, C}) without caring about the order they're found in.
/// </summary>
/// <remarks>
/// Each set is a tree whose root is its representative. Two standard tricks keep the trees nearly flat, so
/// <see cref="Find"/> is effectively constant time: union by size (hang the smaller tree under the larger) and
/// path halving (while walking up, point each visited node at its grandparent).
/// </remarks>
public sealed class UnionFind
{
    private readonly int[] _parent;
    private readonly int[] _size;

    public UnionFind(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _parent = new int[count];
        _size = new int[count];
        for (var i = 0; i < count; i++)
        {
            _parent[i] = i;
            _size[i] = 1;
        }
    }

    public int Count => _parent.Length;

    /// <summary>The representative of <paramref name="id"/>'s set. Two ids are in the same set exactly when their Find is equal.</summary>
    public int Find(int id)
    {
        while (_parent[id] != id)
        {
            _parent[id] = _parent[_parent[id]];
            id = _parent[id];
        }

        return id;
    }

    /// <summary>Merges the sets containing <paramref name="a"/> and <paramref name="b"/>. Returns false if they were already one set.</summary>
    public bool Union(int a, int b)
    {
        var rootA = Find(a);
        var rootB = Find(b);
        if (rootA == rootB)
            return false;

        if (_size[rootA] < _size[rootB])
            (rootA, rootB) = (rootB, rootA);

        _parent[rootB] = rootA;
        _size[rootA] += _size[rootB];
        return true;
    }

    /// <summary>Every set, each listed in ascending id order, sets ordered by their smallest id. Deterministic.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Sets()
    {
        var byRoot = new Dictionary<int, List<int>>();
        var sets = new List<IReadOnlyList<int>>();
        for (var id = 0; id < Count; id++)
        {
            var root = Find(id);
            if (!byRoot.TryGetValue(root, out var members))
            {
                members = [];
                byRoot[root] = members;
                sets.Add(members);
            }

            members.Add(id);
        }

        return sets;
    }
}
