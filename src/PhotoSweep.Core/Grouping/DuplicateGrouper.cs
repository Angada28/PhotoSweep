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
/// <item>Look-alike pass (not for <see cref="MatchLevel.Exact"/>): one fingerprint per distinct file, compared
/// pairwise by brute force. A pair matches only if both pHash and dHash are within the level's limits and, at
/// SamePhoto, their EXIF capture times don't differ. (A BK-tree was tried and was 17× slower on a real library; see
/// docs/decisions.md.) Animated images (e.g. burst cover GIFs) are left out: they only match byte-identical copies.</item>
/// <item>Matches are merged with <see cref="UnionFind"/> into connected sets.</item>
/// <item>Each set is split around keepers, because matching is not transitive: in a burst, neighbouring frames
/// match but the first and last may look nothing alike. The best file becomes a keeper and takes every file
/// within the thresholds of IT; the leftovers are split the same way. So every member of a group is directly
/// within the level's thresholds of its keeper.</item>
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
        return Group(files, MatchThresholds.For(level)); // For validates the level up front
    }

    /// <summary>
    /// Groups at arbitrary look-alike limits instead of a level's, for measuring candidate thresholds (tools/PhotoSweep.Eval).
    /// Member kinds are still classified against <see cref="MatchThresholds.SamePhoto"/>.
    /// </summary>
    public static IReadOnlyList<PhotoGroup> Group(IEnumerable<ScannedFile> files, MatchThresholds limits)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.PHashRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.DHashLimit);
        return Group(files, (MatchThresholds?)limits);
    }

    private static IReadOnlyList<PhotoGroup> Group(IEnumerable<ScannedFile> files, MatchThresholds? limits)
    {

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

        if (limits is { } l)
            MergeLookAlikes(candidates, sets, l);

        var groups = new List<PhotoGroup>();
        foreach (var ids in sets.Sets())
        {
            if (ids.Count > 1)
                groups.AddRange(SplitAroundKeepers(ids.Select(i => candidates[i]).ToList(), limits));
        }

        return groups.OrderBy(g => g.Keeper.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void MergeLookAlikes(List<ScannedFile> candidates, UnionFind sets, MatchThresholds limits)
    {
        // One representative per distinct file (after the exact pass each set is one SHA-256): byte-identical copies
        // have identical fingerprints, so comparing them all would only repeat the same work.
        // Byte-identical copies also share their EXIF, so the representative's capture time speaks for them all.
        var representatives = new List<int>();
        var seenSets = new HashSet<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (LookAlikeFingerprint(candidates[i]) is not null && seenSets.Add(sets.Find(i)))
                representatives.Add(i);
        }

        // Flat arrays: the inner loop is two XORs and two popcounts over contiguous memory, which the CPU streams
        // through far faster than any index structure could skip work on clustered photo hashes.
        var count = representatives.Count;
        var pHashes = new ulong[count];
        var dHashes = new ulong[count];
        var taken = new DateTime?[count];
        for (var k = 0; k < count; k++)
        {
            var file = candidates[representatives[k]];
            (pHashes[k], dHashes[k]) = file.Fingerprint!.Value;
            taken[k] = file.Details?.DateTaken;
        }

        for (var a = 0; a < count; a++)
        {
            var (p, d) = (pHashes[a], dHashes[a]);
            for (var b = a + 1; b < count; b++)
            {
                if (Hamming.Distance(p, pHashes[b]) <= limits.PHashRadius && Hamming.Distance(d, dHashes[b]) <= limits.DHashLimit
                    && !(limits.RequireSameCaptureTime && MatchThresholds.CaptureTimesDiffer(taken[a], taken[b])))
                    sets.Union(representatives[a], representatives[b]);
            }
        }
    }

    /// <summary>
    /// Splits one connected set into groups in which every member is within <paramref name="limits"/> of the keeper.
    /// Deterministic: the set is ranked once (independent of input order) and keepers are taken in rank order.
    /// </summary>
    private static IEnumerable<PhotoGroup> SplitAroundKeepers(List<ScannedFile> set, MatchThresholds? limits)
    {
        // A file's fingerprint, or failing that the fingerprint of a byte-identical copy. Deciding per distinct file
        // rather than per path guarantees byte-identical copies always end up in the same group.
        // Animated images have none here, so they join a group only as byte-identical copies.
        var fingerprintBySha = set
            .Where(f => f.Sha256 is not null && LookAlikeFingerprint(f) is not null)
            .GroupBy(f => f.Sha256!)
            .ToDictionary(g => g.Key, g => LookAlikeFingerprint(g.First())!.Value);
        ImageFingerprint? FingerprintOf(ScannedFile f) =>
            LookAlikeFingerprint(f) ?? (f.Sha256 is { } sha && fingerprintBySha.TryGetValue(sha, out var fp) ? fp : null);

        var remaining = KeeperRanker.Rank(set).Ranked.ToList();
        while (remaining.Count > 1)
        {
            var keeper = remaining[0];
            var keeperFingerprint = FingerprintOf(keeper);
            var members = new List<ScannedFile>();
            var leftovers = new List<ScannedFile>();
            foreach (var f in remaining) // stays in rank order, so the keeper is members[0]
            {
                var belongs = ReferenceEquals(f, keeper)
                    || SameBytes(f, keeper)
                    || (limits is { } l && keeperFingerprint is { } k && FingerprintOf(f) is { } fp && Within(l, fp, k, f, keeper));
                (belongs ? members : leftovers).Add(f);
            }

            remaining = leftovers;

            if (members.Count > 1)
                yield return BuildGroup(members, set, FingerprintOf);
        }
    }

    private static PhotoGroup BuildGroup(List<ScannedFile> members, List<ScannedFile> set, Func<ScannedFile, ImageFingerprint?> fingerprintOf)
    {
        // Ranked against the whole set, so the keeper is the same file the split was built around.
        var (ranked, reason) = KeeperRanker.Rank(members, context: set);
        var keeper = ranked[0];
        var result = ranked.Select((file, index) => Classify(file, keeper, isKeeper: index == 0, fingerprintOf)).ToList();
        return new PhotoGroup(result, reason, set);
    }

    private static GroupMember Classify(ScannedFile file, ScannedFile keeper, bool isKeeper, Func<ScannedFile, ImageFingerprint?> fingerprintOf)
    {
        int? p = null, d = null;
        if (fingerprintOf(file) is { } a && fingerprintOf(keeper) is { } b)
        {
            p = Hamming.Distance(a.PHash, b.PHash);
            d = Hamming.Distance(a.DHash, b.DHash);
        }

        var kind = isKeeper ? MatchKind.Keeper
            : SameBytes(file, keeper) ? MatchKind.Identical
            : p is { } pd && d is { } dd && MatchThresholds.SamePhoto.Accepts(pd, dd, file, keeper) ? MatchKind.SamePhoto
            : MatchKind.Similar;

        return new GroupMember(file, kind, p, d);
    }

    private static bool SameBytes(ScannedFile a, ScannedFile b) => a.Sha256 is not null && a.Sha256 == b.Sha256;

    private static bool Within(MatchThresholds limits, ImageFingerprint a, ImageFingerprint b, ScannedFile fileA, ScannedFile fileB) =>
        limits.Accepts(Hamming.Distance(a.PHash, b.PHash), Hamming.Distance(a.DHash, b.DHash), fileA, fileB);

    /// <summary>
    /// The fingerprint used for look-alike matching: none for animated images. A burst's cover GIF hashes like the
    /// still its first frame came from, but it's a different file to keep, so it's matched by bytes only.
    /// </summary>
    private static ImageFingerprint? LookAlikeFingerprint(ScannedFile f) => f.Details is { IsAnimated: true } ? null : f.Fingerprint;
}
