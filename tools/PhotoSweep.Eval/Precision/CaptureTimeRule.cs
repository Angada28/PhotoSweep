using System.Globalization;
using System.Text;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Eval.Precision;

/// <summary>What the capture-time rule did to one bucket's sampled pairs.</summary>
/// <param name="Sampled">Pairs drawn from the bucket, labelled or not.</param>
/// <param name="Removed">Pairs the rule moves out of SamePhoto (both capture times known and different).</param>
public sealed record RuleBucketEffect(string Bucket, int Sampled, int Removed, int RemovedSame, int RemovedDifferent);

/// <param name="After">The labels with the removed pairs taken out and each bucket's population scaled down to match.</param>
/// <param name="BothTimesKnown">Pairs where both photos have an EXIF capture time, i.e. where the rule could apply at all.</param>
/// <param name="Unreadable">Photos whose time couldn't be read (missing, online-only, not decodable); their pairs are kept.</param>
public sealed record RuleEffect(LabelFile After, IReadOnlyList<RuleBucketEffect> Buckets, int BothTimesKnown, int Unreadable);

/// <summary>
/// Measures the app's capture-time rule (<see cref="MatchThresholds.CaptureTimesDiffer"/>) on existing labels: a pair
/// whose photos were taken a second or more apart is Similar, not SamePhoto, so it leaves the SamePhoto grouping.
/// </summary>
/// <remarks>
/// The labels hold only paths, so the times are read from the photos' EXIF headers. After the rule, each bucket keeps
/// the fraction of its sampled pairs that the rule kept, so its population is estimated as N_b × kept_b / sampled_b.
/// That's the usual stratified estimate: the sample was drawn at random within the bucket, so it stands for the bucket.
/// </remarks>
public static class CaptureTimeRule
{
    public const string Name = "capture-time";

    /// <summary>
    /// EXIF capture time per path, or null when it has none or it can't be read. Online-only files are never opened
    /// (rule 6); they count as unreadable. Header only (<see cref="ImageDetails.Read"/>), no pixels decoded.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime?> ReadTimes(IEnumerable<string> paths, out int unreadable)
    {
        var times = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        unreadable = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DateTime? taken = null;
            if (CloudFileAttributes.Check(path) == FileAvailability.Local)
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    taken = ImageDetails.Read(stream).DateTaken;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SixLabors.ImageSharp.ImageFormatException)
                {
                    unreadable++;
                }
            }
            else
            {
                unreadable++;
            }

            times[path] = taken;
        }

        return times;
    }

    public static RuleEffect Apply(LabelFile file, IReadOnlyDictionary<string, DateTime?> times, int unreadable = 0)
    {
        DateTime? TimeOf(string path) => times.GetValueOrDefault(path);
        bool Removed(LabelledPair p) => MatchThresholds.CaptureTimesDiffer(TimeOf(p.KeeperPath), TimeOf(p.MemberPath));

        var effects = new List<RuleBucketEffect>();
        var buckets = new List<BucketInfo>();
        foreach (var bucket in file.Buckets)
        {
            var pairs = file.Pairs.Where(p => p.Bucket == bucket.Name).ToList();
            var removed = pairs.Where(Removed).ToList();
            effects.Add(new RuleBucketEffect(bucket.Name, pairs.Count, removed.Count,
                removed.Count(p => p.Verdict == LabelFile.Same), removed.Count(p => p.Verdict == LabelFile.Different)));

            var population = pairs.Count == 0 ? bucket.Population
                : (int)Math.Round((double)bucket.Population * (pairs.Count - removed.Count) / pairs.Count, MidpointRounding.AwayFromZero);
            buckets.Add(bucket with { Population = population });
        }

        var after = file with { Buckets = buckets, Pairs = file.Pairs.Where(p => !Removed(p)).ToList() };
        var bothKnown = file.Pairs.Count(p => TimeOf(p.KeeperPath) is not null && TimeOf(p.MemberPath) is not null);
        return new RuleEffect(after, effects, bothKnown, unreadable);
    }

    /// <summary>The before tables, what the rule removed, and the after tables.</summary>
    public static string ToMarkdown(LabelFile before, RuleEffect effect, IReadOnlyList<int> thresholds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Before: hashes only").AppendLine();
        sb.Append(PrecisionCalculator.ToMarkdown(before, thresholds));

        sb.AppendLine($"# Rule: {Name}").AppendLine();
        sb.AppendLine("A pair whose two photos both have an EXIF capture time, and the times differ (1 s or more), is Similar, not SamePhoto,");
        sb.AppendLine("so it leaves the SamePhoto grouping. Bucket populations after the rule: N × kept / sampled.").AppendLine();
        sb.AppendLine(Inv($"- Both capture times known for {effect.BothTimesKnown} of {before.Pairs.Count} sampled pairs; {effect.Unreadable} photos unreadable (kept)."));
        sb.AppendLine();
        sb.AppendLine("| Bucket | Sampled | Removed by rule | Removed \"same\" (lost) | Removed \"different\" (fixed) | Population before → after |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (b, i) in effect.Buckets.Select((b, i) => (b, i)))
        {
            sb.AppendLine(Inv(
                $"| {b.Bucket} | {b.Sampled} | {b.Removed} | {b.RemovedSame} | {b.RemovedDifferent} | {before.Buckets[i].Population} → {effect.After.Buckets[i].Population} |"));
        }

        var totals = (Removed: effect.Buckets.Sum(b => b.Removed), Same: effect.Buckets.Sum(b => b.RemovedSame), Different: effect.Buckets.Sum(b => b.RemovedDifferent));
        sb.AppendLine(Inv($"| **All** | {effect.Buckets.Sum(b => b.Sampled)} | {totals.Removed} | {totals.Same} | {totals.Different} | |"));
        sb.AppendLine();

        sb.AppendLine($"# After: hashes + {Name}").AppendLine();
        sb.Append(PrecisionCalculator.ToMarkdown(effect.After, thresholds));
        return sb.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
