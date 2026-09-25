using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Grouping;

/// <summary>
/// Turns scan results into duplicate groups. Pure and in-memory: it never touches the disk, so it can't open an
/// online-only file, and it's fast to test with made-up <see cref="ScannedFile"/>s.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Exact pass: files with the same SHA-256 are merged. This is the only way undecodable files (e.g. HEIC)
/// get grouped, since they have no fingerprint.</item>
/// <item>Look-alike pass (not for <see cref="MatchLevel.Exact"/>): one fingerprint per distinct file goes into a
/// <see cref="BkTree"/> keyed by pHash. Each is queried within the pHash radius, and a hit counts only if the
/// dHash distance is also within its limit.</item>
/// <item>Matches are merged with <see cref="UnionFind"/>, so groups are transitive: A~B and B~C gives {A, B, C}.</item>
/// </list>
/// </remarks>
public static class DuplicateGrouper
{
    public static IReadOnlyList<PhotoGroup> Group(ScanResult scan, MatchLevel level)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return Group(scan.Files, level);
    }

    /// <summary>Groups of two or more, ordered by keeper path.</summary>
    public static IReadOnlyList<PhotoGroup> Group(IEnumerable<ScannedFile> files, MatchLevel level)
    {
        ArgumentNullException.ThrowIfNull(files);
        var thresholds = MatchThresholds.For(level); // validates the level up front

        // Anything with data takes part, whatever its status: that includes online-only files served from the
        // cache and undecodable files that were still hashed. Files never read (online-only skipped, unreadable)
        // have neither and are left out.
        var candidates = files.Where(f => f.Sha256 is not null || f.Fingerprint is not null).ToList();
        var sets = new UnionFind(candidates.Count);

        var firstWithSha = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Sha256 is not { } sha)
                continue;

            if (firstWithSha.TryGetValue(sha, out var first))
                sets.Union(first, i);
            else
                firstWithSha[sha] = i;
        }

        if (thresholds is { } limits)
            MergeLookAlikes(candidates, sets, limits);

        return sets.Sets()
            .Where(ids => ids.Count > 1)
            .Select(ids => BuildGroup(ids.Select(i => candidates[i]).ToList()))
            .OrderBy(g => g.Keeper.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void MergeLookAlikes(List<ScannedFile> candidates, UnionFind sets, MatchThresholds limits)
    {
        // One representative per distinct file (after the exact pass each set is one SHA-256): byte-identical copies
        // have identical fingerprints, so querying them all would only repeat the same work.
        var representatives = new List<int>();
        var seenSets = new HashSet<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Fingerprint is not null && seenSets.Add(sets.Find(i)))
                representatives.Add(i);
        }

        var tree = new BkTree();
        foreach (var i in representatives)
            tree.Add(candidates[i].Fingerprint!.Value.PHash, i);

        foreach (var i in representatives)
        {
            var fp = candidates[i].Fingerprint!.Value;
            foreach (var j in tree.Query(fp.PHash, limits.PHashRadius))
            {
                // j > i: each pair is found from both ends; handle it once.
                if (j > i && Hamming.Distance(fp.DHash, candidates[j].Fingerprint!.Value.DHash) <= limits.DHashLimit)
                    sets.Union(i, j);
            }
        }
    }

    private static PhotoGroup BuildGroup(List<ScannedFile> files)
    {
        var (ranked, reason) = KeeperRanker.Rank(files);
        var keeper = ranked[0];
        var members = ranked.Select((file, index) => Classify(file, keeper, isKeeper: index == 0)).ToList();
        return new PhotoGroup(members, reason);
    }

    private static GroupMember Classify(ScannedFile file, ScannedFile keeper, bool isKeeper)
    {
        int? p = null, d = null;
        if (file.Fingerprint is { } a && keeper.Fingerprint is { } b)
        {
            p = Hamming.Distance(a.PHash, b.PHash);
            d = Hamming.Distance(a.DHash, b.DHash);
        }

        var kind = isKeeper ? MatchKind.Keeper
            : file.Sha256 is not null && file.Sha256 == keeper.Sha256 ? MatchKind.Identical
            : p is { } pd && d is { } dd && MatchThresholds.SamePhoto.Accepts(pd, dd) ? MatchKind.SamePhoto
            : MatchKind.Similar;

        return new GroupMember(file, kind, p, d);
    }
}
