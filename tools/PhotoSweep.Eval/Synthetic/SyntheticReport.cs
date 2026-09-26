using System.Globalization;
using System.Text;

namespace PhotoSweep.Eval.Synthetic;

/// <summary>What one synthetic run measured, ready for <see cref="SyntheticReport.ToMarkdown"/>.</summary>
public sealed record SyntheticRun(
    string Folder,
    int Seed,
    IReadOnlyList<string> Originals,
    int OnlineOnlySkipped,
    int DecodeFailed,
    ThresholdGrid Grid);

/// <summary>Formats a <see cref="SyntheticRun"/> as Markdown to paste into docs/decisions.md.</summary>
public static class SyntheticReport
{
    /// <summary>Limits shown per variant: the SamePhoto candidates, plus today's SamePhoto (8/8) and Similar (14/14).</summary>
    public static readonly IReadOnlyList<(int P, int D, string Note)> Candidates =
    [
        (4, 4, ""), (5, 5, ""), (6, 6, ""), (8, 8, "current SamePhoto"), (10, 10, ""), (14, 14, "current Similar"),
    ];

    private const int MaxListedFalsePositives = 100;

    public static string ToMarkdown(SyntheticRun run)
    {
        var grid = run.Grid;
        var sb = new StringBuilder();
        var n = run.Originals.Count;
        sb.AppendLine("## Synthetic evaluation: recall and false positives").AppendLine();
        sb.AppendLine(Inv($"- Folder: `{run.Folder}`, seed {run.Seed}"));
        sb.AppendLine(Inv($"- {n} originals, {grid.Variants.Count} variants ({VariantGenerator.AllKinds.Count} per original), {grid.Unrelated.Count:N0} pairs of different originals"));
        sb.AppendLine(Inv($"- Skipped while sampling: {run.OnlineOnlySkipped} online-only (never opened), {run.DecodeFailed} that didn't decode"));
        sb.AppendLine("- Variants: " + string.Join(", ", VariantGenerator.AllKinds.Select(VariantGenerator.Label)));
        sb.AppendLine("- A pair matches when pHash ≤ P **and** dHash ≤ D (`MatchThresholds.Accepts`).");
        sb.AppendLine("- \"100\" means every variant matched; \"100.0\" is rounded up from just below.");
        sb.AppendLine("- pHash distances are almost always even (each pHash sets exactly the 32 bits above its median), so an odd pHash limit behaves like the even one below it.").AppendLine();

        sb.AppendLine("### Recall: % of variants matched to their original").AppendLine();
        AppendGrid(sb, (p, d) => Percent(grid.Recall(p, d)));

        sb.AppendLine(Inv($"### False positives: pairs of different originals matched (out of {grid.Unrelated.Count:N0})")).AppendLine();
        AppendGrid(sb, (p, d) => grid.FalsePositives(p, d).ToString(CultureInfo.InvariantCulture));

        sb.AppendLine("### Recall per variant at candidate limits").AppendLine();
        sb.Append("| Variant |");
        foreach (var c in Candidates)
            sb.Append(Inv($" {c.P}/{c.D}{(c.Note.Length > 0 ? $" ({c.Note})" : "")} |"));
        sb.AppendLine().Append("|---|").AppendLine(string.Concat(Candidates.Select(_ => "---:|")));
        foreach (var kind in VariantGenerator.AllKinds)
            sb.Append($"| {VariantGenerator.Label(kind)} |").AppendLine(string.Concat(Candidates.Select(c => $" {Percent(grid.Recall(c.P, c.D, kind))} |")));
        sb.Append("| **all** |").AppendLine(string.Concat(Candidates.Select(c => $" **{Percent(grid.Recall(c.P, c.D))}** |")));
        sb.Append("| false positives |").AppendLine(string.Concat(Candidates.Select(c => Inv($" {grid.FalsePositives(c.P, c.D)} |"))));
        sb.AppendLine();

        sb.AppendLine("### Distance from variant to original (bits)").AppendLine();
        sb.AppendLine("| Variant | pHash median | pHash p95 | pHash max | dHash median | dHash p95 | dHash max |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var kind in VariantGenerator.AllKinds)
        {
            var ofKind = grid.Variants.Where(v => v.Kind == kind).ToList();
            var p = ofKind.Select(v => v.PHash).ToList();
            var d = ofKind.Select(v => v.DHash).ToList();
            sb.AppendLine(Inv($"| {VariantGenerator.Label(kind)} | {Percentile(p, 50)} | {Percentile(p, 95)} | {Percentile(p, 100)} | {Percentile(d, 50)} | {Percentile(d, 95)} | {Percentile(d, 100)} |"));
        }

        var unrelatedP = grid.Unrelated.Select(u => u.PHash).ToList();
        var unrelatedD = grid.Unrelated.Select(u => u.DHash).ToList();
        sb.AppendLine(Inv($"| *different originals (min / p5 / median)* | {Percentile(unrelatedP, 0)} | {Percentile(unrelatedP, 5)} | {Percentile(unrelatedP, 50)} | {Percentile(unrelatedD, 0)} | {Percentile(unrelatedD, 5)} | {Percentile(unrelatedD, 50)} |"));
        sb.AppendLine();

        AppendFalsePositivePairs(sb, run);
        return sb.ToString();
    }

    private static void AppendGrid(StringBuilder sb, Func<int, int, string> cell)
    {
        sb.Append("| pHash ≤ \\ dHash ≤ |");
        for (var d = ThresholdGrid.Min; d <= ThresholdGrid.Max; d++)
            sb.Append(Inv($" {d} |"));
        sb.AppendLine().Append("|---:|");
        for (var d = ThresholdGrid.Min; d <= ThresholdGrid.Max; d++)
            sb.Append("---:|");
        sb.AppendLine();

        for (var p = ThresholdGrid.Min; p <= ThresholdGrid.Max; p++)
        {
            sb.Append(Inv($"| **{p}** |"));
            for (var d = ThresholdGrid.Min; d <= ThresholdGrid.Max; d++)
                sb.Append(' ').Append(cell(p, d)).Append(" |");
            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static void AppendFalsePositivePairs(StringBuilder sb, SyntheticRun run)
    {
        // Listed so each can be checked by eye: the library has real bursts and copies, so two "different originals"
        // may genuinely be the same photo, which would make the pair a real match rather than a false positive.
        var pairs = run.Grid.Unrelated
            .Where(u => u.PHash <= ThresholdGrid.Max && u.DHash <= ThresholdGrid.Max)
            .OrderBy(u => Math.Max(u.PHash, u.DHash)).ThenBy(u => u.PHash + u.DHash)
            .ToList();

        sb.AppendLine(Inv($"### Pairs of different originals within {ThresholdGrid.Max}/{ThresholdGrid.Max} ({pairs.Count})")).AppendLine();
        if (pairs.Count == 0)
        {
            sb.AppendLine("None.").AppendLine();
            return;
        }

        sb.AppendLine("| pHash | dHash | First | Second |");
        sb.AppendLine("|---:|---:|---|---|");
        foreach (var u in pairs.Take(MaxListedFalsePositives))
            sb.AppendLine(Inv($"| {u.PHash} | {u.DHash} | `{Relative(run, u.First)}` | `{Relative(run, u.Second)}` |"));
        if (pairs.Count > MaxListedFalsePositives)
            sb.AppendLine(Inv($"| | | … and {pairs.Count - MaxListedFalsePositives} more | |"));
        sb.AppendLine();
    }

    private static string Relative(SyntheticRun run, int index) => Path.GetRelativePath(run.Folder, run.Originals[index]);

    /// <summary>Nearest-rank percentile (0 = min, 100 = max); "–" for no data.</summary>
    public static string Percentile(IReadOnlyList<int> values, int percent)
    {
        if (values.Count == 0)
            return "–";

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"99.3", "100", or "–" when there was nothing to measure.</summary>
    public static string Percent(double? share) => share is { } s
        ? (s * 100).ToString(s is 1 or 0 ? "0" : "0.0", CultureInfo.InvariantCulture)
        : "–";

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
