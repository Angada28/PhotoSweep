using PhotoSweep.Eval.Synthetic;

namespace PhotoSweep.Tests.Evaluation;

public class ThresholdGridTests
{
    private static readonly ThresholdGrid Grid = new(
        [
            new VariantDistance(0, VariantKind.Half, 0, 0),
            new VariantDistance(0, VariantKind.Quarter, 4, 2),
            new VariantDistance(1, VariantKind.Half, 2, 6),
            new VariantDistance(1, VariantKind.Quarter, 9, 9),
        ],
        [
            new PairDistance(0, 1, 5, 5),
            new PairDistance(0, 2, 3, 12),
            new PairDistance(1, 2, 30, 31),
        ]);

    [Theory]
    [InlineData(2, 2, 0.25)] // only 0/0
    [InlineData(4, 4, 0.5)] // + 4/2
    [InlineData(4, 6, 0.75)] // + 2/6: pHash and dHash limits are independent
    [InlineData(14, 14, 1.0)]
    public void Recall_is_the_share_within_both_limits(int p, int d, double expected) =>
        Assert.Equal(expected, Grid.Recall(p, d));

    [Fact]
    public void Recall_can_be_restricted_to_one_variant_kind()
    {
        Assert.Equal(1.0, Grid.Recall(2, 6, VariantKind.Half));
        Assert.Equal(0.0, Grid.Recall(2, 6, VariantKind.Quarter));
        Assert.Null(Grid.Recall(8, 8, VariantKind.Png)); // nothing measured, not 0%
    }

    [Theory]
    [InlineData(4, 4, 0)]
    [InlineData(5, 5, 1)]
    [InlineData(5, 12, 2)] // the 3/12 pair needs a loose dHash limit
    [InlineData(14, 11, 1)]
    public void False_positives_count_pairs_of_different_originals_within_both_limits(int p, int d, int expected) =>
        Assert.Equal(expected, Grid.FalsePositives(p, d));

    [Fact]
    public void Report_contains_both_grids_and_lists_the_close_unrelated_pairs()
    {
        var run = new SyntheticRun(@"C:\Photos", 1, [@"C:\Photos\a.jpg", @"C:\Photos\sub\b.jpg", @"C:\Photos\c.jpg"], 2, 1, Grid);

        var markdown = SyntheticReport.ToMarkdown(run);

        Assert.Contains("### Recall", markdown);
        Assert.Contains("### False positives", markdown);
        Assert.Contains("| 5 | 5 | `a.jpg` | `sub\\b.jpg` |", markdown);
        Assert.DoesNotContain("| 30 | 31 |", markdown); // beyond 14/14: not a candidate false positive
        Assert.Contains("2 online-only (never opened), 1 that didn't decode", markdown);
    }

    [Theory]
    [InlineData(0, "1")]
    [InlineData(50, "3")]
    [InlineData(95, "10")]
    [InlineData(100, "10")]
    public void Percentile_uses_nearest_rank(int percent, string expected) =>
        Assert.Equal(expected, SyntheticReport.Percentile([10, 1, 3, 2, 5], percent));

    [Theory]
    [InlineData(1.0, "100")]
    [InlineData(0.0, "0")]
    [InlineData(0.99333, "99.3")]
    [InlineData(null, "–")]
    public void Percent_formats_shares(double? share, string expected) => Assert.Equal(expected, SyntheticReport.Percent(share));
}
