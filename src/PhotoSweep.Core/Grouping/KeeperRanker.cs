using System.Globalization;
using System.Text.RegularExpressions;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Grouping;

/// <summary>
/// Orders a group's files best-first to suggest which copy to keep, and explains the choice in a few words.
/// </summary>
/// <remarks>
/// Criteria are applied in order; the first one that separates two files decides between them:
/// <list type="number">
/// <item>Higher resolution. Anything within 1% of the group's largest counts as largest (a few cropped pixels don't matter).</item>
/// <item>Has camera EXIF (make, model or date taken). Exports and messaging apps usually strip it.</item>
/// <item>Name doesn't look like a copy ("IMG_1 (1)", "Copy of …", "… - Copy", "…_edited").</item>
/// <item>Larger file, compared only within the same format (a PNG of a JPEG is bigger but holds no more detail).
/// Within 1% of that format's largest counts as largest.</item>
/// <item>Older last-modified time, to the whole second.</item>
/// <item>Path, ordinal. Only a tie-breaker so the result never depends on input order.</item>
/// </list>
/// Sorting needs a consistent (transitive) order, and "within 1%" or "only within the same format" compared
/// pairwise isn't: A≈B and B≈C doesn't make A≈C. So those two become per-file sort keys computed against the
/// whole group first (resolution tier, size relative to the largest file of the same format).
/// </remarks>
public static partial class KeeperRanker
{
    public const string IdenticalReason = "Identical copies";

    private static readonly Criterion[] Criteria =
    [
        new((a, b) => b.ResolutionTier.CompareTo(a.ResolutionTier),
            (a, b) => $"Highest resolution ({Dimensions(a)} vs {Dimensions(b)})"),
        new((a, b) => b.HasCameraData.CompareTo(a.HasCameraData),
            (a, _) => CameraName(a) is { } camera ? $"Has camera data ({camera})" : "Has camera data"),
        new((a, b) => a.LooksLikeCopy.CompareTo(b.LooksLikeCopy),
            (_, b) => $"Original file name (\"{Path.GetFileName(b.File.Path)}\" looks like a copy)"),
        new((a, b) => b.SizeWithinFormat.CompareTo(a.SizeWithinFormat),
            (a, b) => $"Larger file, less compressed ({Bytes(a.File.SizeBytes)} vs {Bytes(b.File.SizeBytes)})"),
        new((a, b) => WholeSeconds(a.File.LastWriteUtc).CompareTo(WholeSeconds(b.File.LastWriteUtc)),
            (a, b) => $"Older copy (modified {Dates(a.File.LastWriteUtc, b.File.LastWriteUtc)})"),
        new((a, b) => string.CompareOrdinal(a.File.Path, b.File.Path),
            (_, _) => "Equally good copies; first by path"),
    ];

    private static readonly Comparer<Candidate> Order = Comparer<Candidate>.Create((a, b) =>
    {
        foreach (var criterion in Criteria)
        {
            var result = criterion.Compare(a, b);
            if (result != 0)
                return result;
        }

        return 0;
    });

    /// <summary>
    /// Ranks <paramref name="files"/> best-first. The reason compares the keeper with the best copy that is NOT
    /// byte-identical to it: "it has a shorter path than its identical twin" tells the user nothing when a
    /// lower-resolution copy is also in the group.
    /// </summary>
    /// <param name="files">The files to rank.</param>
    /// <param name="context">
    /// The collection the group-relative keys (resolution tier, size within format) are measured against; defaults
    /// to <paramref name="files"/>. <paramref name="files"/> must be a subset of it. With a fixed context, ranking any
    /// subset gives the same order as ranking the whole context and filtering, so splitting a set into groups can't
    /// change which file comes first.
    /// </param>
    public static (IReadOnlyList<ScannedFile> Ranked, string Reason) Rank(
        IReadOnlyCollection<ScannedFile> files,
        IReadOnlyCollection<ScannedFile>? context = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            return ([], IdenticalReason);

        var ranked = Candidate.For(files, context ?? files).Order(Order).ToList();
        var keeper = ranked[0];
        var rival = ranked.Skip(1).FirstOrDefault(c => !SameBytes(c.File, keeper.File));
        var reason = rival is null ? IdenticalReason : Explain(keeper, rival);
        return (ranked.Select(c => c.File).ToList(), reason);
    }

    public static bool LooksLikeCopy(string path) => CopyName().IsMatch(Path.GetFileNameWithoutExtension(path));

    // "(1)", "copy" as a whole word (so not "copyright" or "photocopy"; "_" counts as a separator), and
    // edit/export suffixes. Letters-only lookarounds instead of \b, because \b treats "_" as part of a word.
    [GeneratedRegex(
        @"\(\d+\)|(?<![a-z])(copy|edited|resized|compressed|scaled|thumb|thumbnail|export|exported)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyName();

    // Sub-second differences say nothing about which file came first (a batch copy writes many files within one
    // second, and FAT/exFAT only store 2-second times), and would show as "09:40:11 vs 09:40:11" in the reason.
    private static long WholeSeconds(DateTime time) => time.Ticks / TimeSpan.TicksPerSecond;

    private static bool SameBytes(ScannedFile a, ScannedFile b) => a.Sha256 is not null && a.Sha256 == b.Sha256;

    private static string Explain(Candidate keeper, Candidate rival)
    {
        foreach (var criterion in Criteria)
        {
            if (criterion.Compare(keeper, rival) != 0)
                return criterion.Explain(keeper, rival);
        }

        return IdenticalReason; // unreachable: the path criterion always separates two different files
    }

    private static string Dimensions(Candidate c) => c.File.Details is { } d ? $"{d.Width}×{d.Height}" : "unknown";

    private static string? CameraName(Candidate c)
    {
        var (make, model) = (c.File.Details?.CameraMake, c.File.Details?.CameraModel);
        if (model is null) return make;
        if (make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model; // "Canon" + "Canon EOS R5"
        return $"{make} {model}";
    }

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    // Local time, as the user sees it in Explorer; add the time of day only when the dates alone look equal.
    private static string Dates(DateTime a, DateTime b)
    {
        var format = a.ToLocalTime().Date == b.ToLocalTime().Date ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd";
        return $"{a.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)} vs {b.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)}";
    }

    private sealed record Criterion(Func<Candidate, Candidate, int> Compare, Func<Candidate, Candidate, string> Explain);

    /// <summary>A file plus its sort keys, some of which depend on the rest of the group.</summary>
    private sealed record Candidate(ScannedFile File, long ResolutionTier, bool HasCameraData, bool LooksLikeCopy, double SizeWithinFormat)
    {
        public static IEnumerable<Candidate> For(IReadOnlyCollection<ScannedFile> files, IReadOnlyCollection<ScannedFile> context)
        {
            var maxPixels = context.Max(f => Pixels(f));
            var maxSizeByFormat = context
                .GroupBy(f => Format(f.Path))
                .ToDictionary(g => g.Key, g => g.Max(f => f.SizeBytes));

            return files.Select(f =>
            {
                var pixels = Pixels(f);
                var tier = pixels * 100 >= maxPixels * 99 ? maxPixels : pixels; // within 1% of the largest = largest
                // Also within 1% = largest: an EXIF block or a rotation tag adds a few bytes without adding quality.
                var maxSize = maxSizeByFormat[Format(f.Path)];
                var sizeRatio = maxSize == 0 ? 1 : (double)f.SizeBytes / maxSize;
                return new Candidate(
                    f,
                    tier,
                    f.Details?.HasCameraData ?? false,
                    KeeperRanker.LooksLikeCopy(f.Path),
                    sizeRatio >= 0.99 ? 1 : sizeRatio);
            });
        }

        private static long Pixels(ScannedFile f) => f.Details?.PixelCount ?? 0;

        private static string Format(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpeg" or ".jpe" => ".jpg",
            ".tiff" => ".tif",
            ".heif" => ".heic",
            var ext => ext,
        };
    }
}
