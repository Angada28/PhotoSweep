using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Core.Cleanup;

/// <summary>
/// Which photos the results screen ticks for removal before the user touches anything. Kept in one place so the
/// threshold-tuning phase can change it together with the thresholds.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Exact and SamePhoto: every copy except the keeper. At these levels a group is the same picture.</item>
/// <item>Similar: only byte-identical copies of the keeper. The rest may be different shots (e.g. burst frames) the
/// user wants to keep, so they have to choose them one by one.</item>
/// </list>
/// The keeper is never suggested, so following the policy always leaves at least one copy of every group.
/// </remarks>
public static class PreselectionPolicy
{
    /// <param name="member">A group member, carrying how it matched the keeper.</param>
    /// <param name="level">The strictness the groups were built at.</param>
    public static bool IsSuggested(GroupMember member, MatchLevel level)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Kind == MatchKind.Keeper)
            return false;

        return level switch
        {
            MatchLevel.Exact or MatchLevel.SamePhoto => true,
            MatchLevel.Similar => member.Kind == MatchKind.Identical,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        };
    }
}
