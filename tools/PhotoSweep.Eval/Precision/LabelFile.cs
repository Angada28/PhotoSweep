using System.Text.Json;

namespace PhotoSweep.Eval.Precision;

/// <summary>
/// A distance bucket: pairs whose larger distance, max(pHash, dHash), is in [Min, Max]. A threshold "T" (pHash ≤ T and
/// dHash ≤ T) is exactly the buckets with Max ≤ T, which is why the bucket is keyed on the max.
/// </summary>
/// <param name="Population">How many pairs of this bucket the whole grouping produced (the stratum's size), used to
/// reweight per-bucket precision into an overall figure.</param>
public sealed record BucketInfo(string Name, int Min, int Max, int Population)
{
    public bool Contains(int pHash, int dHash) => Math.Max(pHash, dHash) is var m && m >= Min && m <= Max;
}

/// <summary>One sampled (keeper, member) pair. <see cref="Verdict"/> is "same", "different" or null (not labelled yet).</summary>
public sealed record LabelledPair
{
    public required int Id { get; init; }
    public required string Bucket { get; init; }
    public required string KeeperPath { get; init; }
    public required string MemberPath { get; init; }
    public string? KeeperSha256 { get; init; }
    public string? MemberSha256 { get; init; }
    public required int PHash { get; init; }
    public required int DHash { get; init; }
    public string? Verdict { get; init; }
}

/// <summary>
/// The labels exchanged between the review page and the <c>precision</c> command. The page embeds it (verdicts empty)
/// and "Export labels" downloads it with verdicts filled in. It records the grouping limits, seed and bucket
/// populations, so precision can be computed from this file alone: no rescan, no access to the photo folder.
/// </summary>
public sealed record LabelFile
{
    public const int CurrentVersion = 1;
    public const string Same = "same";
    public const string Different = "different";

    /// <summary>camelCase, so the page's JavaScript reads and writes the same names.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Version { get; init; } = CurrentVersion;
    public required string Folder { get; init; }
    public required int Seed { get; init; }
    public required int GroupedAtPHash { get; init; }
    public required int GroupedAtDHash { get; init; }
    public required int PairsPerBucket { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required IReadOnlyList<BucketInfo> Buckets { get; init; }
    public required IReadOnlyList<LabelledPair> Pairs { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses and validates: version, known verdicts, and every pair's bucket matching its distances.</summary>
    public static LabelFile FromJson(string json)
    {
        LabelFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LabelFile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a labels file: {ex.Message}", ex);
        }

        if (file is null)
            throw new InvalidDataException("Not a labels file: empty.");
        if (file.Version != CurrentVersion)
            throw new InvalidDataException($"Labels file version {file.Version}; this tool reads version {CurrentVersion}.");

        var buckets = file.Buckets.ToDictionary(b => b.Name, StringComparer.Ordinal);
        foreach (var pair in file.Pairs)
        {
            if (pair.Verdict is not (null or Same or Different))
                throw new InvalidDataException($"Pair {pair.Id}: unknown verdict \"{pair.Verdict}\".");
            if (!buckets.TryGetValue(pair.Bucket, out var bucket) || !bucket.Contains(pair.PHash, pair.DHash))
                throw new InvalidDataException($"Pair {pair.Id}: {pair.PHash}/{pair.DHash} isn't in bucket \"{pair.Bucket}\".");
        }

        return file;
    }

    public static LabelFile Load(string path) => FromJson(File.ReadAllText(path));
}
