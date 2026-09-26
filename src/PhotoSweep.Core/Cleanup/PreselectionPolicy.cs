using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Core.Cleanup;

/// <summary>
/// Which photos the results screen ticks for removal before the user touches anything: only byte-identical copies of
/// the keeper, at every level. Look-alikes (SamePhoto and Similar members) are never pre-selected.
/// </summary>
/// <remarks>
/// Measured, not assumed (docs/decisions.md, 2026-09-26): on a hand-labelled sample of real look-alike pairs, even
/// those 0–2 bits from the keeper were only ~60% the same picture. The differences are ones a 64-bit hash can't see:
/// clock digits on screenshots, stickers, small edits, burst frames. A byte-identical copy is the only case where
/// removing it can't lose anything. The keeper is never suggested, so following the policy always leaves at least one
/// copy of every group.
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

        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level), level, null);

        // The level doesn't change the answer today; it stays a parameter so a later rule (e.g. per-level screenshot
        // handling) doesn't have to change every caller.
        return member.Kind == MatchKind.Identical;
    }
}
