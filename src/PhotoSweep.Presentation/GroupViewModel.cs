using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// One row on the results page: a <see cref="PhotoGroup"/>, keeper first. It owns the "never select every photo"
/// rule, so it holds however selection is changed (click, Select suggested, Clear). <see cref="CleanupPlan"/>
/// checks the same rule again before anything moves.
/// </summary>
/// <remarks>
/// Each change is reported to the page as a delta (±1 photo, ±bytes), so the totals update in constant time
/// instead of re-adding thousands of groups on every click.
/// </remarks>
public sealed partial class GroupViewModel
{
    private readonly Action<int, long> _selectionChanged;
    private readonly Action<GroupViewModel, PhotoViewModel?> _compare;
    private readonly HashSet<PhotoViewModel> _suggested;

    /// <param name="suggestNothing">True when the scan's keeper was moved: the others' match kinds describe the old keeper, so none is suggested.</param>
    /// <param name="selection">Paths to start selected, carried over from before a move or undo. Null means the policy's suggestions.</param>
    internal GroupViewModel(
        PhotoGroup group,
        MatchLevel level,
        bool suggestNothing,
        IReadOnlySet<string>? selection,
        IFileAvailability availability,
        TimeZoneInfo localZone,
        Action<int, long> selectionChanged,
        Action<GroupViewModel, PhotoViewModel?> compare)
    {
        Group = group;
        _selectionChanged = selectionChanged;
        _compare = compare;
        Photos = group.Members.Select(m => new PhotoViewModel(m, this, availability, localZone)).ToList();
        _suggested = suggestNothing ? [] : Photos.Where(p => PreselectionPolicy.IsSuggested(p.Member, level)).ToHashSet();
        FreeableBytes = group.Members.Skip(1).Sum(m => m.File.SizeBytes);
        Kind = GroupKindClassifier.Classify(group); // from the members left, so a burst whose frames were moved can become Copies
        SizeText = $"{DisplayText.Count(Photos.Count, "photo", "photos")} · {DisplayText.Bytes(FreeableBytes)} besides the keeper";

        if (selection is null)
        {
            SelectSuggested();
            return;
        }

        foreach (var photo in Photos)
        {
            if (selection.Contains(photo.File.Path) && CanSelectAnother) // the keep-one rule holds for carried-over selections too
                Set(photo, true);
        }
    }

    public PhotoGroup Group { get; }

    public string KeeperReason => Group.KeeperReason;

    /// <summary>What sort of group this is; the page filters by it.</summary>
    public GroupKind Kind { get; }

    /// <summary>The badge on the row: "Copies", "Burst", "Screenshots" or "Look-alikes".</summary>
    public string KindLabel => GroupFilterOption.BadgeFor(Kind);

    /// <summary>Keeper first, then the rest in rank order.</summary>
    public IReadOnlyList<PhotoViewModel> Photos { get; }

    /// <summary>Space freed by moving every photo except the keeper. The page sorts groups by it.</summary>
    public long FreeableBytes { get; }

    public string SizeText { get; }

    public int SelectedCount { get; private set; }

    public long SelectedBytes { get; private set; }

    /// <summary>Shown on groups with look-alikes, which <see cref="PreselectionPolicy"/> never selects.</summary>
    public const string LookAlikeNote = "Look-alikes aren't selected automatically. Compare them and choose.";

    /// <summary>False when the policy suggests nothing here, e.g. a group with no byte-identical copies.</summary>
    public bool HasSuggestions => _suggested.Count > 0;

    /// <summary>True when any member matched by appearance rather than bytes, so the row shows <see cref="LookAlikeNote"/>.</summary>
    public bool HasLookAlikes => Group.Members.Any(m => m.Kind is MatchKind.SamePhoto or MatchKind.Similar);

    /// <summary>True while at least two photos are unselected, so one more can be selected and one still stays.</summary>
    internal bool CanSelectAnother => Photos.Count - SelectedCount >= 2;

    /// <summary>Opens the compare window on this group, with the first photo after the keeper on the right.</summary>
    [RelayCommand]
    private void Compare() => _compare(this, null);

    /// <summary>Opens the compare window with <paramref name="photo"/> on the right (or the first other photo, for the keeper).</summary>
    internal void Compare(PhotoViewModel photo) => _compare(this, photo);

    internal void Toggle(PhotoViewModel photo)
    {
        if (photo.IsSelected)
            Set(photo, false);
        else if (CanSelectAnother)
            Set(photo, true);
    }

    /// <summary>Resets to <see cref="PreselectionPolicy"/>, which never suggests the keeper.</summary>
    internal void SelectSuggested()
    {
        foreach (var photo in Photos)
            Set(photo, _suggested.Contains(photo));
    }

    internal void Clear()
    {
        foreach (var photo in Photos)
            Set(photo, false);
    }

    private void Set(PhotoViewModel photo, bool selected)
    {
        if (photo.IsSelected == selected)
            return;

        photo.IsSelected = selected;
        var (count, bytes) = selected ? (1, photo.File.SizeBytes) : (-1, -photo.File.SizeBytes);
        SelectedCount += count;
        SelectedBytes += bytes;
        _selectionChanged(count, bytes);

        // Whether the others can be selected may have just changed. Groups are small, so tell them all.
        foreach (var p in Photos)
            p.NotifyCanToggleChanged();
    }
}
