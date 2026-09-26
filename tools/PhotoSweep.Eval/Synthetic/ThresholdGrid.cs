using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Eval.Synthetic;

/// <summary>Distance from one variant to its own original.</summary>
public sealed record VariantDistance(int Original, VariantKind Kind, int PHash, int DHash);

/// <summary>Distance between two different originals (indexes into the sample).</summary>
public sealed record PairDistance(int First, int Second, int PHash, int DHash);

/// <summary>
/// Recall and false positives for any pair of limits, counted from distances computed once. A pair "matches" exactly
/// by the app's own rule, <see cref="MatchThresholds.Accepts"/> (pHash ≤ P AND dHash ≤ D), so the tool can't drift from it.
/// </summary>
public sealed class ThresholdGrid(IReadOnlyList<VariantDistance> variants, IReadOnlyList<PairDistance> unrelated)
{
    public const int Min = 2;
    public const int Max = 14;

    public IReadOnlyList<VariantDistance> Variants { get; } = variants;

    public IReadOnlyList<PairDistance> Unrelated { get; } = unrelated;

    /// <summary>Share (0–1) of variants within the limits of their original; only <paramref name="kind"/> if given. Null if none.</summary>
    public double? Recall(int pHashLimit, int dHashLimit, VariantKind? kind = null)
    {
        var limits = new MatchThresholds(pHashLimit, dHashLimit);
        var total = 0;
        var matched = 0;
        foreach (var v in Variants)
        {
            if (kind is { } k && v.Kind != k)
                continue;

            total++;
            if (limits.Accepts(v.PHash, v.DHash))
                matched++;
        }

        return total == 0 ? null : (double)matched / total;
    }

    /// <summary>Pairs of different originals that would be (wrongly) matched at these limits.</summary>
    public int FalsePositives(int pHashLimit, int dHashLimit)
    {
        var limits = new MatchThresholds(pHashLimit, dHashLimit);
        return Unrelated.Count(u => limits.Accepts(u.PHash, u.DHash));
    }
}
