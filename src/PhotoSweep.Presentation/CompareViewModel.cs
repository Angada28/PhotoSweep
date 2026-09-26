using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

public enum CompareSide
{
    None,
    Left,
    Right,
}

/// <summary>One line of details under the two photos.</summary>
/// <param name="Differs">The two values read differently, so the row is highlighted.</param>
/// <param name="Better">The side <see cref="KeeperRanker"/> prefers on this detail's own criterion, if any.</param>
public sealed record CompareDetailRow(string Label, string Left, string Right, bool Differs, CompareSide Better)
{
    public bool LeftBetter => Better == CompareSide.Left;

    public bool RightBetter => Better == CompareSide.Right;
}

/// <summary>
/// The compare window: a group's keeper on the left, one of its other photos on the right, bigger than on the results
/// page, with their details side by side. ←/→ step the right pane through the group; Page Up/Down move between groups.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Same objects as the results page.</b> The panes show the page's own <see cref="GroupViewModel"/> and
/// <see cref="PhotoViewModel"/>s, so selecting here is selecting there, with the same keep-one rule.</item>
/// <item><b>The page rebuilds its rows</b> after every move, undo and re-group. The window then finds its place again by
/// path (<see cref="Resync"/>), moving on or closing rather than showing photos that are no longer there.</item>
/// <item><b>Better is the ranker's call.</b> Which side wins on a detail comes from <see cref="KeeperRanker.Compare"/>,
/// measured against the group's <see cref="PhotoGroup.RankContext"/>, so it can't disagree with the keeper.</item>
/// <item><b>Threading:</b> UI thread only, like the results page; decoding happens inside <see cref="IPreviewLoader"/>.</item>
/// </list>
/// </remarks>
public sealed partial class CompareViewModel : ObservableObject
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 8;

    private static readonly double[] ZoomLevels = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8];

    private readonly ResultsViewModel _results;
    private readonly IShellService _shell;
    private readonly TimeZoneInfo _localZone;
    private GroupViewModel _group;
    private int _groupIndex;
    private PreviewSize? _paneSize;
    private CancellationTokenSource? _prefetch;
    private bool _isActualSize;
    private double _zoom = 1;

    internal CompareViewModel(
        ResultsViewModel results,
        GroupViewModel group,
        PhotoViewModel? photo,
        IFileAvailability availability,
        IPreviewLoader loader,
        IShellService shell,
        TimeZoneInfo localZone)
    {
        _results = results;
        _shell = shell;
        _localZone = localZone;
        Availability = availability;
        Loader = loader;
        Left = new ComparePaneViewModel(this, isLeft: true);
        Right = new ComparePaneViewModel(this, isLeft: false);
        _group = group;
        results.PropertyChanged += OnResultsChanged;
        Open(group, photo);
    }

    /// <summary>Asks the window to close: the user pressed Esc, or there's nothing left to compare.</summary>
    public event EventHandler? CloseRequested;

    public bool IsClosed { get; private set; }

    /// <summary>The group's keeper (or the best copy left, if the keeper was moved).</summary>
    public ComparePaneViewModel Left { get; }

    public ComparePaneViewModel Right { get; }

    public GroupViewModel Group => _group;

    [ObservableProperty]
    private IReadOnlyList<CompareDetailRow> _details = [];

    /// <summary>Which photo the ranking prefers and why, e.g. "PhotoSweep prefers the left photo: Highest resolution (…)".</summary>
    [ObservableProperty]
    private string _verdict = "";

    [ObservableProperty]
    private string _errorMessage = "";

    public string PhotoPosition => $"Photo {RightIndex + 1} of {Others.Count} besides the keeper";

    public string GroupPosition => $"Group {_groupIndex + 1} of {_results.Groups.Count}";

    /// <summary>The results page is moving, undoing or re-grouping; selection waits until it's done.</summary>
    public bool IsBusy => _results.IsBusy;

    public string BusyText => _results.BusyText;

    /// <summary>Both photos at one image pixel per screen pixel (times <see cref="Zoom"/>), instead of fitted to the panes.</summary>
    public bool IsActualSize
    {
        get => _isActualSize;
        set
        {
            if (!SetProperty(ref _isActualSize, value))
                return;

            Zoom = 1;
            ZoomInCommand.NotifyCanExecuteChanged();
            ZoomOutCommand.NotifyCanExecuteChanged();
            RefreshPreviews();
        }
    }

    /// <summary>In actual-size mode: 1 is actual size. Kept between <see cref="MinZoom"/> and <see cref="MaxZoom"/>.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!SetProperty(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom)))
                return;

            OnPropertyChanged(nameof(ZoomText));
            ZoomInCommand.NotifyCanExecuteChanged();
            ZoomOutCommand.NotifyCanExecuteChanged();
        }
    }

    public string ZoomText => string.Create(CultureInfo.InvariantCulture, $"{Zoom * 100:0}%");

    /// <summary>
    /// The spot both panes are centred on in actual-size mode, as a fraction (0–1) of each photo's width and height.
    /// A fraction rather than pixels, so two photos of different resolutions still show the same part of the picture.
    /// </summary>
    [ObservableProperty]
    private double _panX = 0.5;

    [ObservableProperty]
    private double _panY = 0.5;

    internal IFileAvailability Availability { get; }

    internal IPreviewLoader Loader { get; }

    private IReadOnlyList<PhotoViewModel> Others => _group.Photos.Skip(1).ToList();

    private int RightIndex => IndexOf(Others, Right.Photo);

    private PreviewSize? Box => IsActualSize ? PreviewSize.ActualSize : _paneSize;

    /// <summary>
    /// Called by the view with a pane's size in screen pixels (both panes are the same size). Previews are decoded to
    /// fit it, so nothing is loaded until the first call.
    /// </summary>
    public void SetPaneSize(int width, int height)
    {
        if (width < 1 || height < 1 || IsClosed || _paneSize == new PreviewSize(width, height))
            return;

        _paneSize = new PreviewSize(width, height);
        RefreshPreviews();
    }

    /// <summary>
    /// Points the window at <paramref name="group"/>, with <paramref name="photo"/> on the right. The keeper, or no photo,
    /// means the first photo after the keeper.
    /// </summary>
    internal void Open(GroupViewModel group, PhotoViewModel? photo)
    {
        var others = group.Photos.Skip(1).ToList();
        Show(group, photo is not null && others.Contains(photo) ? photo : others[0]);
    }

    /// <summary>Closes the window for good: stops loading and stops following the results page. Safe to call twice.</summary>
    [RelayCommand]
    public void Close()
    {
        if (IsClosed)
            return;

        IsClosed = true;
        _results.PropertyChanged -= OnResultsChanged;
        _prefetch?.Cancel();
        Left.Detach();
        Right.Detach();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowInFolder(PhotoViewModel photo) =>
        ErrorMessage = _shell.ShowInFolder(photo.File.Path)
            ? ""
            : $"Couldn't show {photo.FileName} in Explorer. It may have been moved or deleted since the scan.";

    private bool HasPreviousPhoto => RightIndex > 0;

    [RelayCommand(CanExecute = nameof(HasPreviousPhoto))]
    private void PreviousPhoto()
    {
        if (HasPreviousPhoto)
            Show(_group, Others[RightIndex - 1]);
    }

    private bool HasNextPhoto => RightIndex < Others.Count - 1;

    [RelayCommand(CanExecute = nameof(HasNextPhoto))]
    private void NextPhoto()
    {
        if (HasNextPhoto)
            Show(_group, Others[RightIndex + 1]);
    }

    private bool HasPreviousGroup => _groupIndex > 0;

    [RelayCommand(CanExecute = nameof(HasPreviousGroup))]
    private void PreviousGroup()
    {
        if (HasPreviousGroup)
            Open(_results.Groups[_groupIndex - 1], null);
    }

    private bool HasNextGroup => _groupIndex < _results.Groups.Count - 1;

    [RelayCommand(CanExecute = nameof(HasNextGroup))]
    private void NextGroup()
    {
        if (HasNextGroup)
            Open(_results.Groups[_groupIndex + 1], null);
    }

    [RelayCommand]
    private void ToggleActualSize() => IsActualSize = !IsActualSize;

    private bool CanZoomIn => IsActualSize && Zoom < MaxZoom;

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn()
    {
        if (CanZoomIn)
            Zoom = ZoomLevels.First(z => z > Zoom + 1e-9);
    }

    private bool CanZoomOut => IsActualSize && Zoom > MinZoom;

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut()
    {
        if (CanZoomOut)
            Zoom = ZoomLevels.Last(z => z < Zoom - 1e-9);
    }

    private void Show(GroupViewModel group, PhotoViewModel right)
    {
        _group = group;
        _groupIndex = Math.Max(0, IndexOf(_results.Groups, group));
        Left.Show(group.Photos[0]);
        Right.Show(right);
        ErrorMessage = "";
        UpdateDetails();

        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(PhotoPosition));
        OnPropertyChanged(nameof(GroupPosition));
        PreviousPhotoCommand.NotifyCanExecuteChanged();
        NextPhotoCommand.NotifyCanExecuteChanged();
        PreviousGroupCommand.NotifyCanExecuteChanged();
        NextGroupCommand.NotifyCanExecuteChanged();
        RefreshPreviews();
    }

    private void RefreshPreviews()
    {
        if (IsClosed)
            return;

        Left.Refresh(Box);
        Right.Refresh(Box);
        Prefetch();
    }

    /// <summary>
    /// Starts decoding, at fit size, the photos ←/→ and Page Down would show next, so stepping through feels instant.
    /// The loader caches them. Cancelled on every move, and never for online-only or missing files. Never at actual
    /// size: one decoded 48 MP photo is about 190 MB.
    /// </summary>
    private void Prefetch()
    {
        _prefetch?.Cancel();
        _prefetch = null;
        if (IsActualSize || _paneSize is not { } box)
            return;

        var cts = new CancellationTokenSource();
        _prefetch = cts;
        foreach (var photo in Neighbours())
        {
            if (Availability.Check(photo.File.Path) == FileAvailability.Local)
                _ = PrefetchAsync(photo, box, cts.Token);
        }
    }

    private IEnumerable<PhotoViewModel> Neighbours()
    {
        var (others, index) = (Others, RightIndex);
        if (index > 0)
            yield return others[index - 1];
        if (index < others.Count - 1)
            yield return others[index + 1];
        if (HasNextGroup && _results.Groups[_groupIndex + 1] is { } next)
        {
            yield return next.Photos[0];
            yield return next.Photos[1];
        }
    }

    private async Task PrefetchAsync(PhotoViewModel photo, PreviewSize box, CancellationToken ct)
    {
        try
        {
            await Loader.LoadAsync(photo.File.Path, photo.File.LastWriteUtc, box, ct);
        }
        catch (Exception)
        {
            // Best effort: cancelled, or it'll fail again (and be reported) if the user actually goes there.
        }
    }

    private void OnResultsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ResultsViewModel.Groups):
                Resync();
                break;
            case nameof(ResultsViewModel.IsBusy) or nameof(ResultsViewModel.BusyText):
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyText));
                Left.NotifyCanToggleChanged();
                Right.NotifyCanToggleChanged();
                break;
        }
    }

    /// <summary>
    /// The results page replaced its rows. Finds where the window was, in order of preference:
    /// <list type="number">
    /// <item>the right photo, wherever it is now;</item>
    /// <item>failing that (it was moved), its group, at the photo that took its place;</item>
    /// <item>failing that (the group is gone), the group now at the same position in the list.</item>
    /// </list>
    /// With no groups left, the window closes.
    /// </summary>
    private void Resync()
    {
        var groups = _results.Groups;
        if (groups.Count == 0)
        {
            Close();
            return;
        }

        var rightPath = Right.Photo!.File.Path;
        foreach (var group in groups)
        {
            var index = IndexOf(group.Photos, p => SamePath(p.File.Path, rightPath));
            if (index > 0)
            {
                Show(group, group.Photos[index]);
                return;
            }

            if (index == 0)
            {
                Open(group, null); // it's that group's keeper now (the old keeper was moved): compare with the next best
                return;
            }
        }

        var oldIndex = RightIndex;
        var oldPaths = _group.Photos.Select(p => p.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Photos.Any(p => oldPaths.Contains(p.File.Path)))
            {
                var others = group.Photos.Skip(1).ToList();
                Show(group, others[Math.Clamp(oldIndex, 0, others.Count - 1)]);
                return;
            }
        }

        Open(groups[Math.Min(_groupIndex, groups.Count - 1)], null);
    }

    private void UpdateDetails()
    {
        var (left, right) = (Left.Photo!, Right.Photo!);
        var (l, r) = (left.File, right.File);
        var comparison = KeeperRanker.Compare(l, r, _group.Group.RankContext);

        CompareSide On(KeeperCriterion criterion) => comparison.ByCriterion[criterion] switch
        {
            < 0 => CompareSide.Left,
            > 0 => CompareSide.Right,
            _ => CompareSide.None,
        };

        // Camera data counts make, model and date taken together; the mark goes on the rows where the winner has a value.
        var camera = On(KeeperCriterion.CameraData);
        CompareSide IfWinnerHas(string? leftValue, string? rightValue) =>
            (camera == CompareSide.Left && leftValue is not null) || (camera == CompareSide.Right && rightValue is not null) ? camera : CompareSide.None;

        var (leftTaken, rightTaken) = (Taken(l), Taken(r));
        var (leftCamera, rightCamera) = (KeeperRanker.CameraName(l.Details), KeeperRanker.CameraName(r.Details));
        var (leftSize, rightSize) = Sizes(l.SizeBytes, r.SizeBytes);

        Details =
        [
            Row("File name", left.FileName, right.FileName, On(KeeperCriterion.OriginalName)),
            Row("Folder", left.Folder, right.Folder, CompareSide.None),
            Row("Resolution", left.Resolution, right.Resolution, On(KeeperCriterion.Resolution)),
            Row("File size", leftSize, rightSize, On(KeeperCriterion.FileSize)),
            Row("Format", Format(l.Path), Format(r.Path), CompareSide.None),
            Row("Date taken", leftTaken ?? NotRecorded, rightTaken ?? NotRecorded, IfWinnerHas(leftTaken, rightTaken)),
            Row("Camera", leftCamera ?? NotRecorded, rightCamera ?? NotRecorded, IfWinnerHas(leftCamera, rightCamera)),
            Row("Modified", Modified(l), Modified(r), On(KeeperCriterion.OlderCopy)),
        ];

        Verdict = comparison.Reason == KeeperRanker.IdenticalReason
            ? "Identical copies: the two files are the same, byte for byte."
            : $"PhotoSweep prefers the {(comparison.Winner <= 0 ? "left" : "right")} photo: {comparison.Reason}";
    }

    private const string NotRecorded = "Not recorded";

    private static CompareDetailRow Row(string label, string left, string right, CompareSide better) =>
        new(label, left, right, !string.Equals(left, right, StringComparison.OrdinalIgnoreCase), better);

    private static string? Taken(ScannedFile file) =>
        file.Details?.DateTaken?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private string Modified(ScannedFile file) =>
        TimeZoneInfo.ConvertTimeFromUtc(file.LastWriteUtc, _localZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Rounded sizes, or exact bytes when rounding would make two different sizes read the same.</summary>
    private static (string Left, string Right) Sizes(long left, long right)
    {
        var (l, r) = (DisplayText.Bytes(left), DisplayText.Bytes(right));
        return l != r || left == right
            ? (l, r)
            : (string.Create(CultureInfo.InvariantCulture, $"{left:N0} bytes"), string.Create(CultureInfo.InvariantCulture, $"{right:N0} bytes"));
    }

    /// <summary>"JPEG", "PNG"…, with the spellings of one format shown as one, so .jpg vs .jpeg isn't flagged as a difference.</summary>
    private static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
    {
        "JPG" or "JPEG" or "JPE" => "JPEG",
        "TIF" or "TIFF" => "TIFF",
        "HEIC" or "HEIF" => "HEIC",
        "" => "Unknown",
        var ext => ext,
    };

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf<T>(IReadOnlyList<T> list, T? item) where T : class => IndexOf(list, x => ReferenceEquals(x, item));

    private static int IndexOf<T>(IReadOnlyList<T> list, Func<T, bool> match)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (match(list[i]))
                return i;
        }

        return -1;
    }
}
