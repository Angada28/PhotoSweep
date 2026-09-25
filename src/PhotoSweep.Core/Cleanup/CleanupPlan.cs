using System.Diagnostics.CodeAnalysis;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Cleanup;

public enum CleanupProblemKind
{
    /// <summary>Every member of a group is selected, so no copy of that photo would be left.</summary>
    RemovesEveryCopy,

    /// <summary>The path isn't a member of any group, so nothing vouches that another copy exists.</summary>
    NotInAnyGroup,

    /// <summary>The path isn't inside any scanned folder, so there's no root to put its review folder in.</summary>
    OutsideScannedFolders,

    /// <summary>The file is already in a review folder.</summary>
    InsideReviewFolder,
}

public sealed record CleanupProblem(CleanupProblemKind Kind, string Path, string Message);

/// <param name="Root">The innermost scanned folder containing the file; its review folder is created there.</param>
/// <param name="RelativePath">Path under <paramref name="Root"/>, kept as-is inside the review folder.</param>
public sealed record PlannedMove(ScannedFile File, string Root, string RelativePath);

/// <param name="Keep">Members not selected. At least one must still be on disk, unchanged, when the moves run.</param>
public sealed record GroupCleanup(PhotoGroup Group, IReadOnlyList<PlannedMove> Remove, IReadOnlyList<ScannedFile> Keep);

public sealed record CleanupValidation(CleanupPlan? Plan, IReadOnlyList<CleanupProblem> Problems)
{
    [MemberNotNullWhen(true, nameof(Plan))]
    public bool IsValid => Plan is not null;
}

/// <summary>
/// A clean-up that has passed validation. The constructor is private, so the only way to get one is through
/// <see cref="Validate"/>, and <see cref="ReviewFolder.MoveToReview"/> can't be handed an unchecked selection.
/// </summary>
public sealed class CleanupPlan
{
    private CleanupPlan(IReadOnlyList<GroupCleanup> groups) => Groups = groups;

    public IReadOnlyList<GroupCleanup> Groups { get; }

    public int FileCount => Groups.Sum(g => g.Remove.Count);

    /// <summary>
    /// Checks a selection against the groups it came from. Any problem refuses the whole clean-up, not just the
    /// offending files: the user asked for one action, so it happens as asked or not at all.
    /// </summary>
    /// <param name="roots">The scanned folders.</param>
    public static CleanupValidation Validate(IReadOnlyList<PhotoGroup> groups, IEnumerable<string> pathsToRemove, IReadOnlyList<string> roots)
    {
        var selected = new HashSet<string>(pathsToRemove.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var fullRoots = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();
        var problems = new List<CleanupProblem>();

        var inGroups = new HashSet<string>(
            groups.SelectMany(g => g.Members).Select(m => Path.GetFullPath(m.File.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in selected.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsInsideReviewFolder(path))
                problems.Add(new(CleanupProblemKind.InsideReviewFolder, path, "Already in a review folder."));
            else if (!inGroups.Contains(path))
                problems.Add(new(CleanupProblemKind.NotInAnyGroup, path, "Not part of any duplicate group."));
            else if (InnermostRoot(path, fullRoots) is null)
                problems.Add(new(CleanupProblemKind.OutsideScannedFolders, path, "Not inside any scanned folder."));
        }

        var plan = new List<GroupCleanup>();
        foreach (var group in groups)
        {
            var remove = new List<PlannedMove>();
            var keep = new List<ScannedFile>();
            foreach (var member in group.Members)
            {
                var path = Path.GetFullPath(member.File.Path);
                if (!selected.Contains(path))
                {
                    keep.Add(member.File);
                }
                else if (InnermostRoot(path, fullRoots) is { } root)
                {
                    remove.Add(new PlannedMove(member.File, root, Path.GetRelativePath(root, path)));
                }
            }

            if (remove.Count == 0)
                continue;
            if (keep.Count == 0)
            {
                problems.Add(new(CleanupProblemKind.RemovesEveryCopy, group.Keeper.File.Path,
                    $"All {group.Members.Count} copies of {Path.GetFileName(group.Keeper.File.Path)} are selected; keep at least one."));
            }

            plan.Add(new GroupCleanup(group, remove, keep));
        }

        return problems.Count > 0 ? new CleanupValidation(null, problems) : new CleanupValidation(new CleanupPlan(plan), []);
    }

    private static bool IsInsideReviewFolder(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(ScanOptions.ReviewFolderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest root containing the path, so nested roots keep files in the nearest review folder.</summary>
    private static string? InnermostRoot(string path, List<string> roots) =>
        roots.Where(root => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .MaxBy(root => root.Length);
}
