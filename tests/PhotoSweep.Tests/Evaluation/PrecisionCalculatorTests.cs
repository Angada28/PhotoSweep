using PhotoSweep.Eval.Precision;

namespace PhotoSweep.Tests.Evaluation;

public class PrecisionCalculatorTests
{
    [Theory]
    [InlineData(0.5, 10, 0.2366, 0.7634)]
    [InlineData(1.0, 30, 0.8865, 1.0)] // all agree: still a real interval, not 100–100%
    [InlineData(0.0, 30, 0.0, 0.1135)]
    public void Wilson_matches_known_values(double p, double n, double low, double high)
    {
        var ci = PrecisionCalculator.Wilson(p, n);
        Assert.Equal(low, ci.Low, 3);
        Assert.Equal(high, ci.High, 3);
    }

    [Fact]
    public void Per_bucket_precision_ignores_unlabelled_pairs()
    {
        var file = Labels.File(
            [("0–2", 0, 2, 500), ("3–4", 3, 4, 40)],
            Labels.Pairs("0–2", 1, same: 8, different: 2, unlabelled: 3),
            Labels.Pairs("3–4", 3, same: 0, different: 0, unlabelled: 2));

        var buckets = PrecisionCalculator.PerBucket(file);

        Assert.Equal((10, 8, 3), (buckets[0].Labelled, buckets[0].Same, buckets[0].Unlabelled));
        Assert.Equal(0.8, buckets[0].Precision);
        Assert.NotNull(buckets[0].Ci);
        Assert.Null(buckets[1].Precision); // nothing labelled: n/a, not a division by zero
        Assert.Null(buckets[1].Ci);
    }

    [Fact]
    public void Overall_precision_is_weighted_by_bucket_population_not_by_labels()
    {
        // Equal labels per bucket, but the 0–2 bucket is 9× more common in the grouping.
        var file = Labels.File(
            [("0–2", 0, 2, 900), ("3–4", 3, 4, 100)],
            Labels.Pairs("0–2", 1, same: 10, different: 0),
            Labels.Pairs("3–4", 3, same: 5, different: 5));

        var overall = PrecisionCalculator.Overall(file, 4);

        Assert.Equal(0.95, overall.Precision!.Value, 10); // 0.9 × 1.0 + 0.1 × 0.5, not the unweighted 15/20
        Assert.Equal(1 / (0.81 / 10 + 0.01 / 10), overall.EffectiveN!.Value, 10);
        Assert.Equal((20, 1000), (overall.Labelled, overall.Population));
        Assert.True(overall.Ci!.Value.Low < 0.95 && overall.Ci.Value.High > 0.95);
    }

    [Fact]
    public void Overall_with_equal_weights_and_labels_is_plain_wilson_on_all_labels()
    {
        var file = Labels.File(
            [("0–2", 0, 2, 50), ("3–4", 3, 4, 50)],
            Labels.Pairs("0–2", 1, same: 10, different: 0),
            Labels.Pairs("3–4", 3, same: 6, different: 4));

        var overall = PrecisionCalculator.Overall(file, 4);

        Assert.Equal(20, overall.EffectiveN!.Value, 10);
        Assert.Equal(PrecisionCalculator.Wilson(0.8, 20), overall.Ci);
    }

    [Fact]
    public void Overall_only_counts_buckets_up_to_the_threshold()
    {
        var file = Labels.File(
            [("0–2", 0, 2, 100), ("3–4", 3, 4, 100)],
            Labels.Pairs("0–2", 1, same: 10, different: 0),
            Labels.Pairs("3–4", 3, same: 0, different: 10));

        Assert.Equal(1.0, PrecisionCalculator.Overall(file, 2).Precision);
        Assert.Equal(0.5, PrecisionCalculator.Overall(file, 4).Precision);
    }

    [Fact]
    public void Overall_is_unknown_when_a_populated_bucket_in_range_has_no_labels()
    {
        var file = Labels.File(
            [("0–2", 0, 2, 100), ("3–4", 3, 4, 100), ("5–6", 5, 6, 0)],
            Labels.Pairs("0–2", 1, same: 10, different: 0),
            Labels.Pairs("3–4", 3, same: 0, different: 0, unlabelled: 5));

        Assert.Null(PrecisionCalculator.Overall(file, 4).Precision);
        // An empty bucket (population 0) has nothing to estimate, so it doesn't block the figure.
        var withEmpty = Labels.File([("0–2", 0, 2, 100), ("5–6", 5, 6, 0)], Labels.Pairs("0–2", 1, same: 3, different: 1));
        Assert.Equal(0.75, PrecisionCalculator.Overall(withEmpty, 6).Precision);
    }

    [Fact]
    public void Threshold_must_be_a_bucket_edge()
    {
        var file = Labels.File([("0–2", 0, 2, 10), ("3–4", 3, 4, 10)], Labels.Pairs("0–2", 1, same: 1, different: 0));

        Assert.Throws<ArgumentException>(() => PrecisionCalculator.Overall(file, 3));
    }

    [Fact]
    public void Markdown_has_the_bucket_table_and_one_row_per_threshold()
    {
        var file = Labels.File(
            [("0–2", 0, 2, 900), ("3–4", 3, 4, 100)],
            Labels.Pairs("0–2", 1, same: 10, different: 0),
            Labels.Pairs("3–4", 3, same: 5, different: 5));

        var markdown = PrecisionCalculator.ToMarkdown(file, [2, 4]);

        Assert.Contains("| 3–4 | 100 | 10 | 5 | 5 | 50.0% |", markdown);
        Assert.Contains("| ≤ 4 | 1000 | 20 | 95.0% |", markdown);
        Assert.Contains("| ≤ 2 | 900 | 10 | 100.0% |", markdown);
    }
}
