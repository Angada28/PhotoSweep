using PhotoSweep.Eval.Precision;

namespace PhotoSweep.Tests.Evaluation;

public class LabelFileTests
{
    private static LabelFile Sample() => Labels.File(
        [("0–2", 0, 2, 12), ("3–4", 3, 4, 5)],
        Labels.Pairs("0–2", 1, same: 1, different: 1, unlabelled: 1),
        Labels.Pairs("3–4", 4, same: 1, different: 0));

    [Fact]
    public void Round_trips_through_json_including_bucket_populations()
    {
        var original = Sample();

        var copy = LabelFile.FromJson(original.ToJson());

        Assert.Equal(original.Buckets, copy.Buckets);
        Assert.Equal(original.Pairs, copy.Pairs);
        Assert.Equal((original.Folder, original.Seed, original.GroupedAtPHash, original.PairsPerBucket), (copy.Folder, copy.Seed, copy.GroupedAtPHash, copy.PairsPerBucket));
    }

    [Fact]
    public void Uses_camel_case_names_the_page_script_expects()
    {
        var json = Sample().ToJson();

        Assert.Contains("\"groupedAtPHash\"", json);
        Assert.Contains("\"memberPath\"", json);
        Assert.Contains("\"verdict\": null", json);
        Assert.Contains("\"population\": 12", json);
    }

    [Fact]
    public void Rejects_another_version()
    {
        var json = Sample().ToJson().Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);

        Assert.Contains("version 2", Assert.Throws<InvalidDataException>(() => LabelFile.FromJson(json)).Message);
    }

    [Fact]
    public void Rejects_an_unknown_verdict()
    {
        var json = Sample().ToJson().Replace("\"verdict\": \"different\"", "\"verdict\": \"maybe\"", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => LabelFile.FromJson(json));
    }

    [Fact]
    public void Rejects_a_pair_whose_distances_are_outside_its_bucket()
    {
        var file = Sample();
        var broken = file with { Pairs = [file.Pairs[0] with { PHash = 5 }] };

        Assert.Throws<InvalidDataException>(() => LabelFile.FromJson(broken.ToJson()));
    }

    [Fact]
    public void Rejects_something_that_is_not_json()
    {
        Assert.Throws<InvalidDataException>(() => LabelFile.FromJson("<html>"));
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(0, 3, false)] // the larger distance decides the bucket
    [InlineData(3, 3, false)]
    public void Bucket_is_keyed_on_the_larger_distance(int p, int d, bool inFirst) =>
        Assert.Equal(inFirst, new BucketInfo("0–2", 0, 2, 0).Contains(p, d));
}
