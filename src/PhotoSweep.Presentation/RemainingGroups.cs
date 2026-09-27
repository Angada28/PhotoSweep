using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation;

/// <param name="KeeperMoved">The scan's keeper was moved out, so the best copy left stands in and nothing is suggested.</param>
internal sealed record ShownGroup(PhotoGroup Group, bool KeeperMoved);

/// <summary>
/// What the results page shows after clean-ups: the scan's groups minus the photos moved out, with renamed photos at
/// their new paths. A pure function of (groups, moved out, renamed), so every batch, undo and strictness change is
/// worked out the same way instead of by editing rows in place.
/// </summary>
internal static class RemainingGroups
{
    public const string KeeperMovedReason = "The suggested keeper was moved; this is the best copy left";

    /// <param name="movedOut">Scan paths of photos that are in the review folder.</param>
    /// <param name="renamed">Scan path → current path, for photos an undo put back under a new name.</param>
    public static IReadOnlyList<ShownGroup> Apply(
        IReadOnlyList<PhotoGroup> groups, IReadOnlySet<string> movedOut, IReadOnlyDictionary<string, string> renamed)
    {
        if (movedOut.Count == 0 && renamed.Count == 0)
            return groups.Select(g => new ShownGroup(g, KeeperMoved: false)).ToList();

        var shown = new List<ShownGroup>();
        foreach (var group in groups)
        {
            var members = group.Members
                .Where(m => !movedOut.Contains(m.File.Path))
                .Select(m => renamed.TryGetValue(m.File.Path, out var now) ? m with { File = m.File with { Path = now } } : m)
                .ToList();
            if (members.Count < 2)
                continue; // nothing left to compare against

            if (members.Count == group.Members.Count && !group.Members.Any(m => renamed.ContainsKey(m.File.Path)))
            {
                shown.Add(new ShownGroup(group, KeeperMoved: false)); // untouched
                continue;
            }

            var keeperMoved = movedOut.Contains(group.Keeper.File.Path);
            if (keeperMoved)
            {
                // Members stay in rank order, so the first one left is the best-ranked copy. Its distances were to the
                // old keeper, so they're cleared rather than shown as if measured against itself.
                members[0] = members[0] with { Kind = MatchKind.Keeper, PHashDistance = null, DHashDistance = null };
            }

            shown.Add(new ShownGroup(new PhotoGroup(members, keeperMoved ? KeeperMovedReason : group.KeeperReason, group.RankContext), keeperMoved));
        }

        return shown;
    }
}
