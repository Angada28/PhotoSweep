using PhotoSweep.Eval.Precision;

namespace PhotoSweep.Tests.Evaluation;

/// <summary>Builds made-up <see cref="LabelFile"/>s for the precision tests.</summary>
internal static class Labels
{
    public static LabelFile File(IEnumerable<(string Name, int Min, int Max, int Population)> buckets, params IEnumerable<LabelledPair>[] pairs)
    {
        var all = pairs.SelectMany(p => p).Select((p, i) => p with { Id = i + 1, MemberPath = $@"C:\Photos\m{i + 1}.jpg" }).ToList();
        return new LabelFile
        {
            Folder = @"C:\Photos",
            Seed = 1,
            GroupedAtPHash = 8,
            GroupedAtDHash = 8,
            PairsPerBucket = 30,
            CreatedUtc = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc),
            Buckets = buckets.Select(b => new BucketInfo(b.Name, b.Min, b.Max, b.Population)).ToList(),
            Pairs = all,
        };
    }

    /// <summary>Pairs in <paramref name="bucket"/>, all at distance <paramref name="distance"/>/<paramref name="distance"/>.</summary>
    public static IEnumerable<LabelledPair> Pairs(string bucket, int distance, int same, int different, int unlabelled = 0) =>
        Enumerable.Repeat(LabelFile.Same, same)
            .Concat(Enumerable.Repeat(LabelFile.Different, different))
            .Concat(Enumerable.Repeat<string?>(null, unlabelled))
            .Select(verdict => new LabelledPair
            {
                Id = 0, // renumbered by File
                Bucket = bucket,
                KeeperPath = @"C:\Photos\keeper.jpg",
                MemberPath = "",
                PHash = distance,
                DHash = distance,
                Verdict = verdict,
            });
}
