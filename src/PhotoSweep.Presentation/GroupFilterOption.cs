using CommunityToolkit.Mvvm.ComponentModel;
using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation;

/// <summary>
/// One filter button above the results ("Bursts (12)"). The page keeps the same five objects for its lifetime and only
/// updates <see cref="Count"/>, so the selected button stays selected across moves, undos and strictness changes.
/// </summary>
/// <param name="kind">The kind of group it shows; null for All.</param>
public sealed partial class GroupFilterOption(GroupKind? kind, string name) : ObservableObject
{
    public GroupKind? Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>Groups of this kind on the page now (all groups, for All).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _count;

    public string Label => $"{Name} ({Count})";

    public bool Shows(GroupViewModel group) => Kind is not { } kind || group.Kind == kind;

    /// <summary>All, then one per <see cref="GroupKind"/>, in the order the buttons appear.</summary>
    internal static IReadOnlyList<GroupFilterOption> CreateAll() =>
    [
        new(null, "All"),
        new(GroupKind.Copies, "Copies"),
        new(GroupKind.Burst, "Bursts"),
        new(GroupKind.Screenshots, "Screenshots"),
        new(GroupKind.LookAlikes, "Look-alikes"),
    ];

    /// <summary>The badge on a group row: singular where it reads better ("Burst").</summary>
    public static string BadgeFor(GroupKind kind) => kind switch
    {
        GroupKind.Copies => "Copies",
        GroupKind.Burst => "Burst",
        GroupKind.Screenshots => "Screenshots",
        GroupKind.LookAlikes => "Look-alikes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
