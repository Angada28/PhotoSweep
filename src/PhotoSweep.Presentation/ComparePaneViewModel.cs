using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// One side of the compare window: which photo it shows, its preview, and its selection. The two panes stay the same
/// objects while the photos in them change, so the view binds once and a load in flight can be cancelled.
/// </summary>
/// <remarks>
/// Selection isn't copied: <see cref="Photo"/> is the very <see cref="PhotoViewModel"/> on the results page, and
/// toggling goes through its command, so the group's keep-one rule and the page's totals apply as if the tile was clicked.
/// </remarks>
public sealed partial class ComparePaneViewModel : ObservableObject
{
    private readonly CompareViewModel _owner;
    private PhotoViewModel? _photo;
    private CancellationTokenSource? _load;

    // The box of the image on screen (or loading), and whether that image is the whole photo, so a bigger box or
    // actual size wouldn't add anything. Together they decide whether a new pane size needs a new decode.
    private PreviewSize? _requested;
    private bool _isWholePhoto;

    internal ComparePaneViewModel(CompareViewModel owner, bool isLeft)
    {
        _owner = owner;
        IsLeft = isLeft;
    }

    public bool IsLeft { get; }

    public PhotoViewModel? Photo => _photo;

    /// <summary>"★ Suggested keeper", "Same photo", "Similar shot"…</summary>
    public string Heading => _photo?.MatchText ?? "";

    public string FilePath => _photo?.File.Path ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudPlaceholder), nameof(ShowUnavailablePlaceholder), nameof(ShowNoPreviewPlaceholder), nameof(IsLoading))]
    private PreviewState _preview;

    /// <summary>The decoded picture from <see cref="IPreviewLoader"/> (a WPF image at runtime), or null.</summary>
    [ObservableProperty]
    private object? _image;

    public bool ShowCloudPlaceholder => Preview == PreviewState.OnlineOnly;

    public bool ShowUnavailablePlaceholder => Preview == PreviewState.Unavailable;

    public bool ShowNoPreviewPlaceholder => Preview == PreviewState.NoPreview;

    public bool IsLoading => Preview == PreviewState.Loading;

    public bool IsSelected => _photo?.IsSelected == true;

    public string SelectionText => IsSelected ? "✓ Selected to move to the review folder" : "Kept";

    public string ToggleText => IsSelected ? "Keep this photo" : "Select to move";

    /// <summary>What the toggle does, or why it can't.</summary>
    public string ToggleHint => _owner.IsBusy ? _owner.BusyText : _photo?.Hint ?? "";

    private bool CanToggle => _photo is { CanToggle: true } && !_owner.IsBusy && !_owner.IsClosed;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle()
    {
        if (CanToggle)
            _photo!.ToggleCommand.Execute(null);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (_photo is not null)
            _owner.ShowInFolder(_photo);
    }

    /// <summary>
    /// Puts <paramref name="photo"/> in the pane. A new view-model for the same file (the results page rebuilds its rows
    /// after every move) keeps the preview; a different file starts from an empty pane.
    /// </summary>
    internal void Show(PhotoViewModel photo)
    {
        var sameFile = _photo is { } old
            && string.Equals(old.File.Path, photo.File.Path, StringComparison.OrdinalIgnoreCase)
            && old.File.LastWriteUtc == photo.File.LastWriteUtc;

        Unsubscribe();
        _photo = photo;
        photo.PropertyChanged += OnPhotoChanged;

        if (!sameFile)
        {
            CancelLoad();
            Image = null;
            Preview = PreviewState.Loading;
        }

        OnPropertyChanged(nameof(Photo));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(FilePath));
        NotifySelectionChanged();
    }

    /// <summary>
    /// Shows a placeholder if the photo is online-only or gone (never opening it), otherwise loads a preview for
    /// <paramref name="box"/> unless the one on screen already serves it. A null box means the pane's size isn't known yet.
    /// </summary>
    internal void Refresh(PreviewSize? box)
    {
        if (_photo is not { } photo)
            return;

        var availability = _owner.Availability.Check(photo.File.Path);
        if (availability != FileAvailability.Local)
        {
            CancelLoad();
            Image = null;
            Preview = availability == FileAvailability.OnlineOnly ? PreviewState.OnlineOnly : PreviewState.Unavailable;
            return;
        }

        if (box is not { } size)
        {
            if (Image is null)
                Preview = PreviewState.Loading;
            return;
        }

        if (Covers(size))
            return;

        CancelLoad();
        if (Image is null)
            Preview = PreviewState.Loading; // otherwise the old image stays up until the sharper one arrives
        _ = LoadAsync(photo, size); // never throws
    }

    /// <summary>The window closed: stop loading, let go of the pictures and of the results page's view-models.</summary>
    internal void Detach()
    {
        CancelLoad();
        Unsubscribe();
        Image = null;
        ToggleCommand.NotifyCanExecuteChanged();
    }

    internal void NotifyCanToggleChanged()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ToggleHint));
    }

    private bool Covers(PreviewSize wanted)
    {
        if (_requested is not { } have)
            return false;

        // Back to fit: reload, so a huge actual-size decode isn't kept for a small pane. To actual size: a fitted
        // preview that's already the whole photo (a small one) is the same thing.
        if (have.IsActualSize != wanted.IsActualSize)
            return !have.IsActualSize && _isWholePhoto;
        if (have.IsActualSize || _isWholePhoto)
            return true; // no box, however big, gets more pixels out of the file

        // Growing a little doesn't need a sharper decode, and shrinking never does (the view scales down).
        return wanted.Width <= have.Width * 1.1 && wanted.Height <= have.Height * 1.1;
    }

    private async Task LoadAsync(PhotoViewModel photo, PreviewSize size)
    {
        var cts = new CancellationTokenSource();
        _load = cts;
        _requested = size;
        _isWholePhoto = false;
        try
        {
            var result = await _owner.Loader.LoadAsync(photo.File.Path, photo.File.LastWriteUtc, size, cts.Token);
            if (!ReferenceEquals(_load, cts))
                return; // moved on to another photo or size meanwhile: never show a stale picture

            Image = result.Status == ThumbnailStatus.Ok ? result.Image : null;
            // The loader never enlarges, so an image smaller than the box on both sides is the whole photo.
            _isWholePhoto = result.Status == ThumbnailStatus.Ok
                && (size.IsActualSize || (result.Width < size.Width && result.Height < size.Height));
            Preview = result.Status switch
            {
                ThumbnailStatus.Ok => PreviewState.Shown,
                ThumbnailStatus.OnlineOnly => PreviewState.OnlineOnly,
                ThumbnailStatus.Unavailable => PreviewState.Unavailable,
                _ => PreviewState.NoPreview,
            };
            if (result.Status is ThumbnailStatus.OnlineOnly or ThumbnailStatus.Unavailable)
                _requested = null; // that can change at any moment, so check again next time
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // E.g. out of memory for a huge photo at actual size: a placeholder, not a crash (this task is never awaited).
            if (ReferenceEquals(_load, cts))
            {
                _requested = null;
                Image = null;
                Preview = PreviewState.NoPreview;
            }
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
                _load = null;
            cts.Dispose();
        }
    }

    private void CancelLoad()
    {
        _load?.Cancel();
        _load = null;
        _requested = null;
        _isWholePhoto = false;
    }

    private void Unsubscribe()
    {
        if (_photo is not null)
            _photo.PropertyChanged -= OnPhotoChanged;
    }

    // IsSelected changes when either window toggles; Hint changes whenever CanToggle may have (see GroupViewModel.Set).
    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoViewModel.IsSelected) or nameof(PhotoViewModel.Hint))
            NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(ToggleText));
        NotifyCanToggleChanged();
    }
}
