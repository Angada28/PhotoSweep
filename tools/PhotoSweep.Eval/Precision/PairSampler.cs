using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Eval.Precision;

/// <summary>A keeper and one look-alike member of its group, with their distances.</summary>
public sealed record CandidatePair(ScannedFile Keeper, ScannedFile Member, int PHash, int DHash);

/// <summary>The population per bucket, and the pairs drawn from each.</summary>
public sealed record StratifiedSample(IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<(BucketInfo Bucket, CandidatePair Pair)> Pairs, int OutsideBuckets);

/// <summary>
/// Draws the pairs to label, the same number from each distance bucket. A plain random sample would be mostly 0–2-bit
/// re-saves (the common case) and leave few labels at 5–6 bits, which is exactly where the SamePhoto limit is being
/// chosen. Stratifying gives each bucket its own precision; <see cref="PrecisionCalculator"/> reweights them by
/// population for an overall figure.
/// </summary>
public static class PairSampler
{
    /// <summary>Buckets by max(pHash, dHash). 7–8 only fills if the grouping used limits of at least 8.</summary>
    public static readonly IReadOnlyList<(string Name, int Min, int Max)> DefaultBuckets =
    [
        ("0–2", 0, 2), ("3–4", 3, 4), ("5–6", 5, 6), ("7–8", 7, 8),
    ];

    /// <summary>
    /// Every keeper–member pair decided by the perceptual hashes. Byte-identical members are left out: they're certain
    /// matches, and counting them would inflate precision for reasons that have nothing to do with the thresholds.
    /// </summary>
    public static IReadOnlyList<CandidatePair> FromGroups(IEnumerable<PhotoGroup> groups) =>
        groups.SelectMany(g => g.Members.Skip(1)
                .Where(m => m.Kind != MatchKind.Identical && m.PHashDistance is not null && m.DHashDistance is not null)
                .Select(m => new CandidatePair(g.Keeper.File, m.File, m.PHashDistance!.Value, m.DHashDistance!.Value)))
            .ToList();

    /// <summary>
    /// Up to <paramref name="perBucket"/> pairs per bucket, chosen with <paramref name="seed"/>. Pairs are sorted by path
    /// before shuffling, so the draw doesn't depend on input order.
    /// </summary>
    public static StratifiedSample Stratify(IReadOnlyList<CandidatePair> pairs, int perBucket, int seed, IReadOnlyList<(string Name, int Min, int Max)>? buckets = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perBucket, 1);
        buckets ??= DefaultBuckets;
        var random = new Random(seed);
        var infos = new List<BucketInfo>();
        var drawn = new List<(BucketInfo, CandidatePair)>();
        var inAnyBucket = 0;

        foreach (var (name, min, max) in buckets)
        {
            var members = pairs
                .Where(p => Math.Max(p.PHash, p.DHash) is var m && m >= min && m <= max)
                .OrderBy(p => p.Keeper.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Member.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            inAnyBucket += members.Length;

            var info = new BucketInfo(name, min, max, members.Length);
            infos.Add(info);
            random.Shuffle(members);
            drawn.AddRange(members.Take(perBucket).Select(p => (info, p)));
        }

        return new StratifiedSample(infos, drawn, pairs.Count - inAnyBucket);
    }
}
