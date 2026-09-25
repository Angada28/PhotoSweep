using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Core.Grouping;

/// <summary>
/// BK-tree over 64-bit hashes with Hamming distance: finds every stored hash within a radius of a query hash
/// without comparing against all of them.
/// </summary>
/// <remarks>
/// <para>
/// Each child hangs off its parent on an edge labelled with its distance to the parent. Hamming distance is a
/// metric, so the triangle inequality holds: if the query is <c>d</c> away from a node, anything within
/// <c>radius</c> of the query is between <c>d - radius</c> and <c>d + radius</c> away from that node. Only
/// children on those edges can contain matches; the rest of the tree is skipped.
/// </para>
/// <para>
/// Trade-off: 64-bit compares are so cheap that brute force (n²/2 of them) is fine up to tens of thousands of
/// photos. The tree pays off on large libraries, and prunes less the larger the radius.
/// </para>
/// </remarks>
public sealed class BkTree
{
    private Node? _root;

    public int Count { get; private set; }

    public void Add(ulong hash, int id)
    {
        Count++;
        if (_root is null)
        {
            _root = new Node(hash, id);
            return;
        }

        var node = _root;
        while (true)
        {
            var distance = Hamming.Distance(hash, node.Hash);
            if (distance == 0)
            {
                node.Ids.Add(id); // same hash: share the node rather than chaining zero-distance edges
                return;
            }

            node.Children ??= [];
            if (!node.Children.TryGetValue(distance, out var child))
            {
                node.Children[distance] = new Node(hash, id);
                return;
            }

            node = child;
        }
    }

    /// <summary>Ids of every stored hash within <paramref name="radius"/> bits of <paramref name="hash"/> (inclusive), in no particular order.</summary>
    public List<int> Query(ulong hash, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        var results = new List<int>();
        if (_root is null)
            return results;

        // Explicit stack instead of recursion: a degenerate tree could be deep.
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            var distance = Hamming.Distance(hash, node.Hash);
            if (distance <= radius)
                results.AddRange(node.Ids);

            if (node.Children is null)
                continue;

            foreach (var (edge, child) in node.Children)
            {
                if (edge >= distance - radius && edge <= distance + radius)
                    pending.Push(child);
            }
        }

        return results;
    }

    private sealed class Node(ulong hash, int id)
    {
        public ulong Hash { get; } = hash;

        public List<int> Ids { get; } = [id];

        /// <summary>Keyed by edge distance (1–64). Null until the first child, since most nodes are leaves.</summary>
        public Dictionary<int, Node>? Children { get; set; }
    }
}
