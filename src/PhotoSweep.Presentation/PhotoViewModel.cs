using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>What a photo's tile shows where the thumbnail goes.</summary>
public enum PreviewState
{
    /// <summary>Not loaded yet (or loading): an empty tile.</summary>
    Loading,

    Shown,

    /// <summary>Only in the cloud: a cloud placeholder, and the file is never opened.</summary>
    OnlineOnly,

    /// <summary>Moved or deleted since the scan, or can't be read.</summary>
    Unavailable,

    /// <summary>On disk but can't be decoded (e.g. HEIC).</summary>
    NoPreview,
}

/// <summary>
/// One photo's tile on the results page. Selection goes through its <see cref="GroupViewModel"/>, which owns the
/// rule that at least one photo per group stays unselected.
/// </summary>
public sealed partial class PhotoViewModel : ObservableObject
{
    private readonly GroupViewModel _group;
    private readonly IFileAvailability _availability;
    private bool _isSelected;
    private PreviewState _preview;

    internal PhotoViewModel(GroupMember member, GroupViewModel group, IFileAvailability availability, TimeZoneInfo localZone)
    {
        Member = member;
        _group = group;
        _availability = availability;

        var file = member.File;
        FileName = Path.GetFileName(file.Path);
        Folder = Path.GetDirectoryName(file.Path) ?? "";
        Resolution = file.Details is { } d
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Width}×{d.Height}")
            : "Unknown size";
        Size = DisplayText.Bytes(file.SizeBytes);
        // EXIF dates are camera-local with no zone, so they're shown as recorded. The file time is UTC.
        Date = file.Details?.DateTaken is { } taken
            ? "Taken " + taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Modified " + TimeZoneInfo.ConvertTimeFromUtc(file.LastWriteUtc, localZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        MatchText = member.Kind switch
        {
            MatchKind.Keeper => "★ Suggested keeper",
            MatchKind.Identical => "Identical copy",
            MatchKind.SamePhoto => "Same photo",
            _ => "Similar shot",
        };
    }

    public GroupMember Member { get; }

    /// <summary>The row this photo is on. Its selection rules apply here and in the compare window alike.</summary>
    internal GroupViewModel Group => _group;

    public ScannedFile File => Member.File;

    public bool IsKeeper => Member.Kind == MatchKind.Keeper;

    public string FileName { get; }

    public string Folder { get; }

    public string Resolution { get; }

    public string Size { get; }

    public string Date { get; }

    public string MatchText { get; }

    /// <summary>Ticked to be moved to the review folder. Set only by the group, which enforces the keep-one rule.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public PreviewState Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(ShowCloudPlaceholder));
                OnPropertyChanged(nameof(ShowUnavailablePlaceholder));
                OnPropertyChanged(nameof(ShowNoPreviewPlaceholder));
            }
        }
    }

    public bool ShowCloudPlaceholder => Preview == PreviewState.OnlineOnly;

    public bool ShowUnavailablePlaceholder => Preview == PreviewState.Unavailable;

    public bool ShowNoPreviewPlaceholder => Preview == PreviewState.NoPreview;

    /// <summary>Tooltip explaining what a click does, or why it does nothing.</summary>
    public string Hint => IsSelected ? "Selected to move to the review folder. Click to keep it."
        : CanToggle ? "Click to select it for moving to the review folder."
        : "This is the last photo left in the group. Keep at least one copy.";

    /// <summary>
    /// Called by the view each time the tile comes on screen, so a file freed up by OneDrive after the scan still
    /// gets the cloud placeholder. Returns true only if the thumbnail may be loaded, i.e. the file is on this PC.
    /// </summary>
    public bool RefreshAvailability()
    {
        Preview = _availability.Check(File.Path) switch
        {
            FileAvailability.OnlineOnly => PreviewState.OnlineOnly,
            FileAvailability.Unavailable => PreviewState.Unavailable,
            _ => PreviewState.Loading,
        };
        return Preview == PreviewState.Loading;
    }

    /// <summary>Called by the view when the thumbnail load finishes.</summary>
    public void PreviewLoaded(ThumbnailStatus status) => Preview = status switch
    {
        ThumbnailStatus.Ok => PreviewState.Shown,
        ThumbnailStatus.OnlineOnly => PreviewState.OnlineOnly,
        ThumbnailStatus.Unavailable => PreviewState.Unavailable,
        _ => PreviewState.NoPreview,
    };

    /// <summary>Deselecting is always allowed; selecting only while another photo in the group stays unselected.</summary>
    public bool CanToggle => IsSelected || _group.CanSelectAnother;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle() => _group.Toggle(this);

    /// <summary>Opens the compare window on this photo. Never changes the selection.</summary>
    [RelayCommand]
    private void Compare() => _group.Compare(this);

    internal void NotifyCanToggleChanged()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Hint));
    }
}
