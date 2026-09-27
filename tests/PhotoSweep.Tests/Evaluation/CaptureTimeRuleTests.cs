using PhotoSweep.Eval.Precision;
using PhotoSweep.Tests.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhotoSweep.Tests.Evaluation;

public class CaptureTimeRuleTests
{
    private static readonly DateTime Shot = new(2020, 5, 23, 13, 39, 35);
    private const string Keeper = @"C:\Photos\keeper.jpg";

    // Labels.File names members m1, m2, … in order, all against the same keeper.
    private static string Member(int i) => $@"C:\Photos\m{i}.jpg";

    [Fact]
    public void Removes_only_pairs_whose_capture_times_are_both_known_and_differ()
    {
        // 0–2: m1 same, m2 same, m3 different, m4 different.  3–4: m5 different, m6 unlabelled.
        var file = Labels.File(
            [("0–2", 0, 2, 100), ("3–4", 3, 4, 50)],
            Labels.Pairs("0–2", 1, same: 2, different: 2),
            Labels.Pairs("3–4", 3, same: 0, different: 1, unlabelled: 1));
        var times = new Dictionary<string, DateTime?>
        {
            [Keeper] = Shot,
            [Member(1)] = Shot,              // same second: kept
            [Member(2)] = Shot.AddSeconds(2), // "same" but 2 s apart: removed (a loss)
            [Member(3)] = Shot.AddSeconds(1), // "different", 1 s apart: removed (a fix)
            [Member(4)] = null,               // no time: kept
            [Member(5)] = Shot.AddSeconds(-3),
            [Member(6)] = Shot.AddSeconds(4), // unlabelled, still removed from the population
        };

        var effect = CaptureTimeRule.Apply(file, times);

        Assert.Equal(new RuleBucketEffect("0–2", 4, 2, 1, 1), effect.Buckets[0]);
        Assert.Equal(new RuleBucketEffect("3–4", 2, 2, 0, 1), effect.Buckets[1]);
        Assert.Equal(5, effect.BothTimesKnown);
        Assert.Equal([1, 4], effect.After.Pairs.Select(p => p.Id));
        // Population scaled by the kept fraction of the sample: 100 × 2/4, 50 × 0/2.
        Assert.Equal([50, 0], effect.After.Buckets.Select(b => b.Population));

        var after = PrecisionCalculator.PerBucket(effect.After);
        Assert.Equal(0.5, after[0].Precision); // m1 same, m4 different
    }

    [Fact]
    public void Without_any_capture_times_nothing_changes()
    {
        var file = Labels.File([("0–2", 0, 2, 100)], Labels.Pairs("0–2", 1, same: 3, different: 1));

        var effect = CaptureTimeRule.Apply(file, new Dictionary<string, DateTime?>());

        Assert.Equal(file.Pairs, effect.After.Pairs);
        Assert.Equal(100, effect.After.Buckets[0].Population);
        Assert.Equal(0, effect.BothTimesKnown);
    }

    [Fact]
    public void Markdown_shows_before_the_rule_and_after()
    {
        var file = Labels.File([("0–2", 0, 2, 100)], Labels.Pairs("0–2", 1, same: 1, different: 1));
        var effect = CaptureTimeRule.Apply(file, new Dictionary<string, DateTime?> { [Keeper] = Shot, [Member(2)] = Shot.AddSeconds(5) });

        var markdown = CaptureTimeRule.ToMarkdown(file, effect, [2]);

        Assert.Contains("# Before: hashes only", markdown);
        Assert.Contains("# After: hashes + capture-time", markdown);
        Assert.Contains("| 0–2 | 2 | 1 | 0 | 1 | 100 → 50 |", markdown);
        Assert.Contains("| 0–2 | 50 | 1 | 1 | 0 | 100.0% |", markdown); // after: one pair left, labelled same
    }

    [Fact]
    public void Reads_exif_times_and_counts_unreadable_files_without_failing()
    {
        using var temp = new TempPhotoFolder();
        var withTime = Path.Combine(temp.Root, "camera.jpg");
        using (var image = Image.Load(TempPhotoFolder.ReadTestPhoto("coffee.jpg")))
        {
            (image.Metadata.ExifProfile ??= new ExifProfile()).SetValue(ExifTag.DateTimeOriginal, "2020:05:23 13:39:35");
            image.SaveAsJpeg(withTime);
        }

        var withoutTime = temp.AddPhoto("chelsea.jpg");
        var notAnImage = temp.AddBytes("notes.jpg", [1, 2, 3]);
        var missing = Path.Combine(temp.Root, "missing.jpg");

        var times = CaptureTimeRule.ReadTimes([withTime, withoutTime, notAnImage, missing, withTime], out var unreadable);

        Assert.Equal(Shot, times[withTime]);
        Assert.Null(times[withoutTime]);
        Assert.Null(times[notAnImage]);
        Assert.Null(times[missing]);
        Assert.Equal(2, unreadable);
    }
}
