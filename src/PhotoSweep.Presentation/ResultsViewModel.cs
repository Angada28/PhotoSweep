using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// The results page: the duplicate groups, largest saving first, with the photos to remove selected. The strictness
/// can be changed here; that re-groups the finished scan (no re-scan) and resets the selection to the policy.
/// Moving to the review folder and undo live in <c>ResultsViewModel.Cleanup.cs</c>.
/// </summary>
/// <remarks>
/// The kind filter only changes what's shown. <see cref="AllGroups"/> is what the totals, the selection and Move work
/// on; <see cref="Groups"/> is the filtered view the list and the compare window use. A photo selected in a group the
/// filter hides is still moved, and the selection bar and the confirmation say how many of those there are.
/// <para>
/// Threading: like <see cref="ScanningViewModel"/>, this class runs on the UI thread only. Grouping runs on the thread
/// pool inside <see cref="IScanService"/>, and each <c>await</c> resumes back on the UI thread.
/// </para>
/// </remarks>
public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly ICleanupService _cleanup;
    private readonly IShellService _shell;
    private readonly IFileAvailability _availability;
    private readonly IWindowService _windows;
    private readonly IPreviewLoader _previews;
    private readonly Action _goBack;
    private readonly TimeZoneInfo _localZone;

    // Groups already built per level, so switching back to a level is instant.
    private readonly Dictionary<MatchLevel, IReadOnlyList<PhotoGroup>> _groupsByLevel = [];

    private StrictnessOption _selectedStrictness;
    private GroupFilterOption _selectedFilter;
    private CancellationTokenSource? _regrouping;
    private bool _bulkChange;
    private CompareViewModel? _compare;

    public ResultsViewModel(
        ScanOutcome outcome,
        IScanService scanService,
        ICleanupService cleanup,
        IShellService shell,
        IFileAvailability availability,
        IWindowService windows,
        IPreviewLoader previews,
        Action goBack,
        TimeProvider? time = null)
    {
        Outcome = outcome;
        _scanService = scanService;
        _cleanup = cleanup;
        _shell = shell;
        _availability = availability;
        _windows = windows;
        _previews = previews;
        _goBack = goBack;
        _localZone = (time ?? TimeProvider.System).LocalTimeZone;
        _groupsByLevel[outcome.Request.Level] = outcome.Groups;
        _selectedStrictness = StrictnessOption.For(outcome.Request.Level);
        _selectedFilter = Filters[0];
        Show(outcome.Request.Level, selection: null);
    }

    public ScanOutcome Outcome { get; }

    /// <summary>The level the groups on screen were built at. Lags behind <see cref="SelectedStrictness"/> while re-grouping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceText))]
    private MatchLevel _level;

    /// <summary>Every group at this level, whatever the filter. Largest <see cref="GroupViewModel.FreeableBytes"/> first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupCount), nameof(ExtraCopies), nameof(ExtraBytes), nameof(Summary), nameof(SpaceText))]
    [NotifyCanExecuteChangedFor(nameof(SelectSuggestedCommand))]
    private IReadOnlyList<GroupViewModel> _allGroups = [];

    /// <summary>The groups the selected filter shows, in the same order. The list and the compare window use these.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private IReadOnlyList<GroupViewModel> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(BusyText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private bool _isRegrouping;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand), nameof(MoveCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private long _selectedBytes;

    public IReadOnlyList<StrictnessOption> Strictness => StrictnessOption.All;

    /// <summary>All / Copies / Bursts / Screenshots / Look-alikes, with counts.</summary>
    public IReadOnlyList<GroupFilterOption> Filters { get; } = GroupFilterOption.CreateAll();

    /// <summary>The filter button that's pressed. Changing it only changes <see cref="Groups"/>; the selection stays.</summary>
    public GroupFilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedFilter, value))
                ApplyFilter();
        }
    }

    /// <summary>Selected photos in groups the filter hides. Move still moves them.</summary>
    public int HiddenSelectedCount => AllGroups.Where(g => !SelectedFilter.Shows(g)).Sum(g => g.SelectedCount);

    /// <summary>Changing it re-groups the existing scan. Written by hand (not [ObservableProperty]) so a failed re-group can put it back without starting another one.</summary>
    public StrictnessOption SelectedStrictness
    {
        get => _selectedStrictness;
        set
        {
            if (SetProperty(ref _selectedStrictness, value))
                _ = RegroupAsync(value.Level); // never throws: failures become ErrorMessage
        }
    }

    public int GroupCount => AllGroups.Count;

    public string EmptyText =>
        AllGroups.Count > 0 ? $"No groups in \"{SelectedFilter.Name}\"{(_movedOut.Count > 0 ? " left" : "")} at this strictness."
        : _movedOut.Count > 0 ? "No duplicates left at this strictness." : "No duplicates found at this strictness.";

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Every member except each group's keeper.</summary>
    public int ExtraCopies => AllGroups.Sum(g => g.Photos.Count - 1);

    public long ExtraBytes => AllGroups.Sum(g => g.FreeableBytes);

    public string Summary => GroupCount == 0
        ? (_movedOut.Count > 0 ? "No duplicates left." : "No duplicates found.")
        : $"{DisplayText.Count(GroupCount, "group", "groups")} with {DisplayText.Count(ExtraCopies, "extra copy", "extra copies")}";

    /// <summary>
    /// At Similar the groups include near-identical shots the user may want to keep (e.g. burst frames), so the
    /// space is a ceiling, not a promise.
    /// </summary>
    public string SpaceText => GroupCount == 0 ? ""
        : Level == MatchLevel.Similar ? $"Up to {DisplayText.Bytes(ExtraBytes)} in look-alike copies"
        : $"{DisplayText.Bytes(ExtraBytes)} in extra copies";

    public string SelectionText => SelectedCount == 0
        ? "No photos selected"
        : $"{DisplayText.Count(SelectedCount, "photo", "photos")} selected · {DisplayText.Bytes(SelectedBytes)}"
          + (HiddenSelectedCount is var hidden and > 0 ? $" ({hidden} not shown)" : "");

    public IReadOnlyList<string> UnreadableFolders => Outcome.UnreadableFolders;

    public bool HasUnreadableFolders => UnreadableFolders.Count > 0;

    /// <summary>Set when the user chose "Continue without them" on the scanning page.</summary>
    public string CloudSkippedText => Outcome.Scan.OnlineOnlySkipped.Count is var n and > 0
        ? $"{DisplayText.Count(n, "cloud-only photo", "cloud-only photos")} ({DisplayText.Bytes(Outcome.Scan.OnlineOnlyBytes)}) "
          + (n == 1 ? "wasn't scanned." : "weren't scanned.")
        : "";

    public bool HasCloudSkipped => CloudSkippedText.Length > 0;

    public bool HasNotIncluded => HasUnreadableFolders || HasCloudSkipped;

    private bool CanSelectSuggested => !IsWorking && AllGroups.Any(g => g.HasSuggestions);

    [RelayCommand(CanExecute = nameof(CanSelectSuggested))]
    private void SelectSuggested() => ChangeAll(g => g.SelectSuggested());

    private bool CanClearSelection => !IsWorking && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection() => ChangeAll(g => g.Clear());

    private async Task RegroupAsync(MatchLevel level)
    {
        _regrouping?.Cancel(); // a re-group still running for a level the user has already moved away from
        _regrouping = null;
        ErrorMessage = "";

        if (_groupsByLevel.ContainsKey(level))
        {
            IsRegrouping = false;
            Show(level, selection: null);
            return;
        }

        var cts = new CancellationTokenSource();
        _regrouping = cts;
        IsRegrouping = true;
        try
        {
            var groups = await _scanService.GroupAsync(Outcome.Scan, level, cts.Token);
            if (cts.IsCancellationRequested)
                return; // superseded while grouping; a late result must not replace the newer one

            _groupsByLevel[level] = groups;
            Show(level, selection: null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep the groups on screen and put the dropdown back to match them.
            ErrorMessage = $"Couldn't re-group the photos: {ex.Message}";
            _selectedStrictness = StrictnessOption.For(Level);
            OnPropertyChanged(nameof(SelectedStrictness));
        }
        finally
        {
            if (ReferenceEquals(_regrouping, cts))
            {
                _regrouping = null;
                IsRegrouping = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Builds the rows for the level's groups, leaving out photos moved to the review folder. With a null
    /// <paramref name="selection"/> every group starts at the policy's suggestions; otherwise those paths are selected.
    /// </summary>
    private void Show(MatchLevel level, IReadOnlySet<string>? selection)
    {
        _bulkChange = true;
        var rows = RemainingGroups.Apply(_groupsByLevel[level], _movedOut, _renamed)
            .Select(g => new GroupViewModel(g.Group, level, g.KeeperMoved, selection, _availability, _localZone, OnSelectionChanged, OpenCompare))
            .OrderByDescending(g => g.FreeableBytes)
            .ThenBy(g => g.Group.Keeper.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _bulkChange = false;

        Level = level;
        AllGroups = rows;
        foreach (var filter in Filters)
            filter.Count = rows.Count(filter.Shows);
        ApplyFilter();
    }

    /// <summary>Shows the groups the selected filter lets through. The selection isn't touched, only recounted.</summary>
    private void ApplyFilter()
    {
        Groups = SelectedFilter.Kind is null ? AllGroups : AllGroups.Where(SelectedFilter.Shows).ToList();
        OnPropertyChanged(nameof(EmptyText)); // also depends on the filter's name
        RecountSelection();
    }

    /// <summary>
    /// Opens the compare window on <paramref name="photo"/> (null: the group's first photo after the keeper). There's
    /// one compare window: opening again re-points it instead of stacking windows. Only navigates; never selects.
    /// </summary>
    private void OpenCompare(GroupViewModel group, PhotoViewModel? photo)
    {
        if (_compare is { IsClosed: false } open)
            open.Open(group, photo);
        else
            _compare = new CompareViewModel(this, group, photo, _availability, _previews, _shell, _localZone);

        _windows.ShowCompare(_compare);
    }

    /// <summary>A bulk change adds up the totals once at the end instead of raising a change per photo.</summary>
    private void ChangeAll(Action<GroupViewModel> change)
    {
        _bulkChange = true;
        foreach (var group in AllGroups)
            change(group);
        _bulkChange = false;
        RecountSelection();
    }

    private void OnSelectionChanged(int count, long bytes)
    {
        if (_bulkChange)
            return;

        CloseConfirmation();
        SelectedCount += count;
        SelectedBytes += bytes;
    }

    private void RecountSelection()
    {
        CloseConfirmation(); // its numbers describe the old selection
        SelectedCount = AllGroups.Sum(g => g.SelectedCount);
        SelectedBytes = AllGroups.Sum(g => g.SelectedBytes);
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(SelectionText)); // the hidden count may change without the totals changing
    }
}
