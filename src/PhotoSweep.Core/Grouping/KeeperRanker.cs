using System.Globalization;
using System.Text.RegularExpressions;
using PhotoSweep.Core.Imaging;
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
/// <item>Name doesn't look like a copy ("IMG_1 (1)", "Copy of …", "… - Copy", "…_edited", "IMG_1.0").</item>
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
        new(KeeperCriterion.Resolution,
            (a, b) => b.ResolutionTier.CompareTo(a.ResolutionTier),
            (a, b) => $"Highest resolution ({Dimensions(a)} vs {Dimensions(b)})"),
        new(KeeperCriterion.CameraData,
            (a, b) => b.HasCameraData.CompareTo(a.HasCameraData),
            (a, _) => CameraName(a.File.Details) is { } camera ? $"Has camera data ({camera})" : "Has camera data"),
        new(KeeperCriterion.OriginalName,
            (a, b) => a.LooksLikeCopy.CompareTo(b.LooksLikeCopy),
            (_, b) => $"Original file name (\"{Path.GetFileName(b.File.Path)}\" looks like a copy)"),
        new(KeeperCriterion.FileSize,
            (a, b) => b.SizeWithinFormat.CompareTo(a.SizeWithinFormat),
            (a, b) => $"Larger file, less compressed ({Sizes(a.File.SizeBytes, b.File.SizeBytes)})"),
        new(KeeperCriterion.OlderCopy,
            (a, b) => WholeSeconds(a.File.LastWriteUtc).CompareTo(WholeSeconds(b.File.LastWriteUtc)),
            (a, b) => $"Older copy (modified {Dates(a.File.LastWriteUtc, b.File.LastWriteUtc)})"),
        new(KeeperCriterion.Path,
            (a, b) => string.CompareOrdinal(a.File.Path, b.File.Path),
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

    /// <summary>
    /// Compares two files the way <see cref="Rank"/> orders them, criterion by criterion. Used by the compare window to
    /// mark which of two photos is better on each detail, so it can never disagree with the keeper the ranking picked.
    /// </summary>
    /// <param name="context">
    /// What the group-relative keys are measured against. Pass the group's <see cref="PhotoGroup.RankContext"/> to get
    /// exactly the order the group was ranked in. <paramref name="a"/> and <paramref name="b"/> are added to it if missing.
    /// </param>
    public static KeeperComparison Compare(ScannedFile a, ScannedFile b, IReadOnlyCollection<ScannedFile>? context = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        IReadOnlyCollection<ScannedFile> all = context is null ? [a, b] : [.. context.Union([a, b])];
        var pair = Candidate.For([a, b], all).ToList();
        var (ca, cb) = (pair[0], pair[1]);

        var results = Criteria.ToDictionary(c => c.Id, c => Math.Sign(c.Compare(ca, cb)));
        var deciding = Criteria.FirstOrDefault(c => results[c.Id] != 0);
        if (deciding is null)
            return new KeeperComparison(0, null, IdenticalReason, results); // the same path twice

        var winner = results[deciding.Id];
        var reason = SameBytes(a, b) ? IdenticalReason
            : winner < 0 ? deciding.Explain(ca, cb) : deciding.Explain(cb, ca);
        return new KeeperComparison(winner, deciding.Id, reason, results);
    }

    /// <summary>"Canon EOS R5", "Apple iPhone 15", or null without make and model.</summary>
    public static string? CameraName(ImageDetails? details)
    {
        var (make, model) = (details?.CameraMake, details?.CameraModel);
        if (model is null) return make;
        if (make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model; // "Canon" + "Canon EOS R5"
        return $"{make} {model}";
    }

    public static bool LooksLikeCopy(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return CopyName().IsMatch(stem) || NumberedCopyName().IsMatch(stem);
    }

    // "(1)", "copy" as a whole word (so not "copyright" or "photocopy"; "_" counts as a separator), and
    // edit/export suffixes. Letters-only lookarounds instead of \b, because \b treats "_" as part of a word.
    [GeneratedRegex(
        @"\(\d+\)|(?<![a-z])(copy|edited|resized|compressed|scaled|thumb|thumbnail|export|exported)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyName();

    // "IMG_1234.0": some apps save a second copy with ".0"–".9" before the extension. Only when that is the stem's
    // only dot and the rest isn't just a number, so dotted dates and times ("2016-03-09 21.03.33", "14.07.2019"),
    // versions ("v1.2.3") and plain numbers ("3.5") don't count.
    [GeneratedRegex(@"^[^.]*[^\d.][^.]*\.\d$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedCopyName();

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

    /// <summary>
    /// "1.5 MB vs 200 KB", adding decimals (up to 3) until the two read differently, so a real difference never
    /// shows as "1.3 MB vs 1.3 MB". Exact bytes as a last resort.
    /// </summary>
    private static string Sizes(long a, long b)
    {
        for (var decimals = 1; decimals <= 3; decimals++)
        {
            var (x, y) = (Bytes(a, decimals), Bytes(b, decimals));
            if (x != y)
                return $"{x} vs {y}";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{a:N0} B vs {b:N0} B");
    }

    private static string Bytes(long bytes, int decimals)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal drops a trailing zero ("200 KB"); more keep them, so the two sizes line up ("1.30 vs 1.29").
        var format = decimals == 1 ? "0.#" : "F" + decimals;
        return unit == 0 ? $"{bytes} B" : value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    // Local time, as the user sees it in Explorer; add the time of day only when the dates alone look equal.
    private static string Dates(DateTime a, DateTime b)
    {
        var format = a.ToLocalTime().Date == b.ToLocalTime().Date ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd";
        return $"{a.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)} vs {b.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)}";
    }

    private sealed record Criterion(KeeperCriterion Id, Func<Candidate, Candidate, int> Compare, Func<Candidate, Candidate, string> Explain);

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

/// <summary>The ranking criteria, in the order <see cref="KeeperRanker"/> applies them.</summary>
public enum KeeperCriterion
{
    Resolution,
    CameraData,
    OriginalName,

    /// <summary>Larger file within the same format.</summary>
    FileSize,

    /// <summary>Older last-modified time, to the whole second.</summary>
    OlderCopy,

    /// <summary>Ordinal path; only a tie-breaker.</summary>
    Path,
}

/// <summary>The result of <see cref="KeeperRanker.Compare"/>.</summary>
/// <param name="Winner">-1 when A ranks first, 1 when B does, 0 only for the same path twice.</param>
/// <param name="DecidedBy">The first criterion that separates them, i.e. the one the ranking used.</param>
/// <param name="Reason">Why the winner ranks first, in the words the results page uses.</param>
/// <param name="ByCriterion">For each criterion on its own: -1 A is better, 1 B is better, 0 a tie.</param>
public sealed record KeeperComparison(
    int Winner,
    KeeperCriterion? DecidedBy,
    string Reason,
    IReadOnlyDictionary<KeeperCriterion, int> ByCriterion);
