using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Grouping;

/// <summary>How a group member relates to the group's keeper.</summary>
public enum MatchKind
{
    Keeper,

    /// <summary>Byte-for-byte the same file as the keeper.</summary>
    Identical,

    /// <summary>Within the <see cref="MatchThresholds.SamePhoto"/> limits of the keeper.</summary>
    SamePhoto,

    /// <summary>Within the <see cref="MatchThresholds.Similar"/> limits of the keeper but not the SamePhoto ones.</summary>
    Similar,
}

/// <param name="PHashDistance">Distance to the keeper's pHash; null unless both have fingerprints.</param>
/// <param name="DHashDistance">Distance to the keeper's dHash; null unless both have fingerprints.</param>
public sealed record GroupMember(ScannedFile File, MatchKind Kind, int? PHashDistance, int? DHashDistance);

/// <summary>Two or more files that look like copies of each other, best copy first.</summary>
public sealed class PhotoGroup(IReadOnlyList<GroupMember> members, string keeperReason)
{
    /// <summary>Ranked best-first; <c>Members[0]</c> is the suggested keeper.</summary>
    public IReadOnlyList<GroupMember> Members { get; } = members;

    public GroupMember Keeper => Members[0];

    /// <summary>Short, human-readable reason the keeper was suggested, e.g. "Highest resolution (4032×3024 vs 1600×1200)".</summary>
    public string KeeperReason { get; } = keeperReason;
}
