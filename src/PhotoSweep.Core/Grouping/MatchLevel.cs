using PhotoSweep.Core.Hashing;

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
    // PLACEHOLDERS, to be tuned from an evaluation set in a later phase. Current evidence (TestData variants):
    // edits of the same photo land at 0–3 bits, unrelated photos at 24+ (random pairs average 32).
    public static readonly MatchThresholds SamePhoto = new(PHashRadius: 8, DHashLimit: 8);
    public static readonly MatchThresholds Similar = new(PHashRadius: 14, DHashLimit: 14);

    /// <summary>Null for <see cref="MatchLevel.Exact"/>, which doesn't use perceptual hashes at all.</summary>
    public static MatchThresholds? For(MatchLevel level) => level switch
    {
        MatchLevel.Exact => null,
        MatchLevel.SamePhoto => SamePhoto,
        MatchLevel.Similar => Similar,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public bool Accepts(int pHashDistance, int dHashDistance) => pHashDistance <= PHashRadius && dHashDistance <= DHashLimit;
}
