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
public sealed class GroupViewModel
{
    private readonly Action<int, long> _selectionChanged;
    private readonly HashSet<PhotoViewModel> _suggested;

    internal GroupViewModel(PhotoGroup group, MatchLevel level, IFileAvailability availability, TimeZoneInfo localZone, Action<int, long> selectionChanged)
    {
        Group = group;
        _selectionChanged = selectionChanged;
        Photos = group.Members.Select(m => new PhotoViewModel(m, this, availability, localZone)).ToList();
        _suggested = Photos.Where(p => PreselectionPolicy.IsSuggested(p.Member, level)).ToHashSet();
        FreeableBytes = group.Members.Skip(1).Sum(m => m.File.SizeBytes);
        SizeText = $"{DisplayText.Count(Photos.Count, "photo", "photos")} · {DisplayText.Bytes(FreeableBytes)} besides the keeper";
        SelectSuggested();
    }

    public PhotoGroup Group { get; }

    public string KeeperReason => Group.KeeperReason;

    /// <summary>Keeper first, then the rest in rank order.</summary>
    public IReadOnlyList<PhotoViewModel> Photos { get; }

    /// <summary>Space freed by moving every photo except the keeper. The page sorts groups by it.</summary>
    public long FreeableBytes { get; }

    public string SizeText { get; }

    public int SelectedCount { get; private set; }

    public long SelectedBytes { get; private set; }

    /// <summary>False when the policy suggests nothing here, e.g. a group of look-alikes at Similar.</summary>
    public bool HasSuggestions => _suggested.Count > 0;

    /// <summary>True while at least two photos are unselected, so one more can be selected and one still stays.</summary>
    internal bool CanSelectAnother => Photos.Count - SelectedCount >= 2;

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
