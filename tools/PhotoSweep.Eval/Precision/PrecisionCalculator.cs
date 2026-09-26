using System.Globalization;
using System.Text;

namespace PhotoSweep.Eval.Precision;

/// <summary>A 95% confidence interval for a proportion.</summary>
public readonly record struct Interval(double Low, double High);

public sealed record BucketPrecision(BucketInfo Bucket, int Labelled, int Same, int Unlabelled, double? Precision, Interval? Ci);

/// <param name="EffectiveN">Kish effective sample size of the weighted estimate (see <see cref="PrecisionCalculator.Overall"/>).</param>
public sealed record OverallPrecision(int Threshold, int Labelled, int Population, double? Precision, Interval? Ci, double? EffectiveN);

/// <summary>Precision from a <see cref="LabelFile"/>: per bucket, and overall at a threshold reweighted by bucket population.</summary>
public static class PrecisionCalculator
{
    private const double Z95 = 1.959964;

    /// <summary>
    /// Wilson score interval: unlike the textbook p ± z·√(p(1−p)/n), it stays inside [0, 1] and doesn't collapse to
    /// zero width when every label agrees (30 of 30 "same" still gives roughly 89–100%, not 100–100%).
    /// <paramref name="n"/> may be fractional (an effective sample size).
    /// </summary>
    public static Interval Wilson(double p, double n, double z = Z95)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        var z2 = z * z;
        var denominator = 1 + z2 / n;
        var centre = (p + z2 / (2 * n)) / denominator;
        var half = z / denominator * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n));
        return new Interval(Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    public static IReadOnlyList<BucketPrecision> PerBucket(LabelFile file) =>
        file.Buckets.Select(b =>
        {
            var pairs = file.Pairs.Where(p => p.Bucket == b.Name).ToList();
            var labelled = pairs.Count(p => p.Verdict is not null);
            var same = pairs.Count(p => p.Verdict == LabelFile.Same);
            double? precision = labelled == 0 ? null : (double)same / labelled;
            return new BucketPrecision(b, labelled, same, pairs.Count - labelled, precision, precision is { } pr ? Wilson(pr, labelled) : null);
        }).ToList();

    /// <summary>
    /// Precision of every pair the grouping would match at pHash ≤ T and dHash ≤ T, i.e. the buckets with Max ≤ T.
    /// The labelled sample over-represents rare buckets (same count from each), so each bucket's precision is weighted by
    /// its share of the population: P = Σ w_b·p_b, w_b = N_b / Σ N. The interval is Wilson with the Kish effective sample
    /// size n_eff = 1 / Σ(w_b² / n_b), which shrinks when a heavily weighted bucket has few labels. Null precision if any
    /// populated bucket in range has no labels, because its share can't be estimated.
    /// </summary>
    public static OverallPrecision Overall(LabelFile file, int threshold)
    {
        if (!file.Buckets.Any(b => b.Max == threshold))
        {
            var edges = string.Join(", ", file.Buckets.Select(b => b.Max));
            throw new ArgumentException($"Threshold {threshold} isn't a bucket edge; use one of {edges}.", nameof(threshold));
        }

        var inRange = PerBucket(file).Where(b => b.Bucket.Max <= threshold && b.Bucket.Population > 0).ToList();
        var population = inRange.Sum(b => b.Bucket.Population);
        var labelled = inRange.Sum(b => b.Labelled);
        if (population == 0 || inRange.Any(b => b.Labelled == 0))
            return new OverallPrecision(threshold, labelled, population, null, null, null);

        double precision = 0, inverseN = 0;
        foreach (var b in inRange)
        {
            var w = (double)b.Bucket.Population / population;
            precision += w * b.Precision!.Value;
            inverseN += w * w / b.Labelled;
        }

        var effectiveN = 1 / inverseN;
        return new OverallPrecision(threshold, labelled, population, precision, Wilson(precision, effectiveN), effectiveN);
    }

    public static string ToMarkdown(LabelFile file, IReadOnlyList<int> thresholds)
    {
        var sb = new StringBuilder();
        var labelled = file.Pairs.Count(p => p.Verdict is not null);
        sb.AppendLine("## Precision on real groups").AppendLine();
        sb.AppendLine(Inv($"- Folder: `{file.Folder}`, grouped at pHash ≤ {file.GroupedAtPHash}, dHash ≤ {file.GroupedAtDHash}, seed {file.Seed}"));
        sb.AppendLine(Inv($"- {labelled} of {file.Pairs.Count} sampled pairs labelled (up to {file.PairsPerBucket} per bucket); byte-identical copies excluded"));
        sb.AppendLine("- Bucket = max(pHash, dHash) distance from member to keeper. 95% Wilson intervals.").AppendLine();

        sb.AppendLine("| Bucket | Pairs in grouping | Labelled | Same | Different | Precision | 95% CI |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var b in PerBucket(file))
            sb.AppendLine(Inv($"| {b.Bucket.Name} | {b.Bucket.Population} | {b.Labelled} | {b.Same} | {b.Labelled - b.Same} | {Pct(b.Precision)} | {Ci(b.Ci)} |"));
        sb.AppendLine();

        sb.AppendLine("| Threshold (pHash ≤ T and dHash ≤ T) | Pairs in grouping | Labelled | Weighted precision | 95% CI | Effective n |");
        sb.AppendLine("|---|---:|---:|---:|---|---:|");
        foreach (var t in thresholds)
        {
            var o = Overall(file, t);
            var n = o.EffectiveN is { } e ? e.ToString("0.0", CultureInfo.InvariantCulture) : "–";
            sb.AppendLine(Inv($"| ≤ {t} | {o.Population} | {o.Labelled} | {Pct(o.Precision)} | {Ci(o.Ci)} | {n} |"));
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Pct(double? p) => p is { } v ? (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

    private static string Ci(Interval? ci) => ci is { } i
        ? Inv($"{i.Low * 100:0.0}–{i.High * 100:0.0}%")
        : "–";

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
