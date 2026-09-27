using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Grouping;

/// <summary>How alike files must be to be grouped. Each level includes everything the stricter levels find.</summary>
public enum MatchLevel
{
    /// <summary>Byte-identical files only (same SHA-256).</summary>
    Exact,

    /// <summary>Plus the same photo resized, recompressed, converted or rotated.</summary>
    SamePhoto,

    /// <summary>Plus looser look-alikes, e.g. heavier edits or near-identical shots.</summary>
    Similar,
}

/// <summary>
/// Perceptual-hash limits for one <see cref="MatchLevel"/>. Two images match only if BOTH distances are within
/// their limits: pHash (frequency content) and dHash (gradients) fail in different ways, so requiring agreement
/// cuts false matches.
/// </summary>
public readonly record struct MatchThresholds(int PHashRadius, int DHashLimit)
{
    /// <summary>
    /// Also require the EXIF capture times to agree when both files have one. Two shots taken a second or more apart
    /// are two photos, however alike they hash (burst frames; see docs/decisions.md, 2026-09-26 capture-time rule).
    /// </summary>
    public bool RequireSameCaptureTime { get; init; }

    // Measured with tools/PhotoSweep.Eval (docs/decisions.md, 2026-09-26). SamePhoto 4/5: 99.3% of resized,
    // recompressed, converted and rotated copies match, with no more false positives than 4/4. dHash gets the extra
    // bit because it limits recall; pHash distances are always even (32 bits set above the median), so pHash 5 would
    // behave exactly like 4. Similar stays at 14/14 until it gets its own measurement.
    // It also requires matching capture times: a re-save keeps the original's EXIF time, a second shot doesn't.
    public static readonly MatchThresholds SamePhoto = new(PHashRadius: 4, DHashLimit: 5) { RequireSameCaptureTime = true };
    public static readonly MatchThresholds Similar = new(PHashRadius: 14, DHashLimit: 14);

    /// <summary>Null for <see cref="MatchLevel.Exact"/>, which doesn't use perceptual hashes at all.</summary>
    public static MatchThresholds? For(MatchLevel level) => level switch
    {
        MatchLevel.Exact => null,
        MatchLevel.SamePhoto => SamePhoto,
        MatchLevel.Similar => Similar,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>The hash test alone: pHash within the radius and dHash within the limit.</summary>
    public bool Accepts(int pHashDistance, int dHashDistance) => pHashDistance <= PHashRadius && dHashDistance <= DHashLimit;

    /// <summary>The full test for two files: the hash distances, plus the capture times when <see cref="RequireSameCaptureTime"/>.</summary>
    public bool Accepts(int pHashDistance, int dHashDistance, ScannedFile a, ScannedFile b) =>
        Accepts(pHashDistance, dHashDistance) && !(RequireSameCaptureTime && CaptureTimesDiffer(a.Details?.DateTaken, b.Details?.DateTaken));

    /// <summary>
    /// True only when both times are known and differ. EXIF DateTimeOriginal has whole seconds, so "differ" means
    /// "1 second or more apart". A missing time (screenshots, messaging-app re-saves) says nothing either way.
    /// </summary>
    public static bool CaptureTimesDiffer(DateTime? a, DateTime? b) => a is { } x && b is { } y && x != y;
}
