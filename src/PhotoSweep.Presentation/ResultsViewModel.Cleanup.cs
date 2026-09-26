using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation;

/// <summary>A heading and its lines in the page's report panel, e.g. the photos that couldn't be moved and why.</summary>
public sealed record ReportSection(string Heading, IReadOnlyList<string> Lines);

// Moving the selected photos to the review folder, and undo. A separate file for readability, but the same class: a move
// changes the groups, totals and selection that the rest of the page shows.
public sealed partial class ResultsViewModel
{
    private static readonly string ReviewFolderName = ScanOptions.ReviewFolderName;

    // Scan paths of photos this page moved to the review folder and hasn't put back. RemainingGroups leaves them out.
    private readonly HashSet<string> _movedOut = new(StringComparer.OrdinalIgnoreCase);

    // Scan path → current path, for photos undo put back under a new name ("a (2).jpg") because the old one was taken.
    private readonly Dictionary<string, string> _renamed = new(StringComparer.OrdinalIgnoreCase);

    // Undo stack: the undo bar offers the last one. Only lives as long as this page (see docs/decisions.md).
    private readonly List<MovedBatch> _batches = [];

    private CleanupPlan? _pendingPlan;
    private Task _cleanupRun = Task.CompletedTask;
    private string _workingText = "";

    /// <summary>The in-page "Move N photos…?" panel is showing. In-page rather than a dialog, so tests can drive it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmMoveCommand))]
    private bool _isConfirming;

    [ObservableProperty]
    private string _confirmText = "";

    [ObservableProperty]
    private string _confirmFoldersHeading = "";

    /// <summary>The review folder in each scanned root the photos will go to; usually one.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _confirmFolders = [];

    /// <summary>A move or undo is running. The page is locked meanwhile, so the groups can't change under it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(BusyText), nameof(CanChangeStrictness))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand), nameof(ConfirmMoveCommand), nameof(UndoCommand), nameof(OpenReviewFolderCommand),
        nameof(BackCommand), nameof(LeaveCommand), nameof(SelectSuggestedCommand), nameof(ClearSelectionCommand))]
    private bool _isWorking;

    /// <summary>Title of the report panel (outcome of the last move, undo or failed check). Empty hides the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportTitle = "";

    [ObservableProperty]
    private IReadOnlyList<ReportSection> _reportSections = [];

    /// <summary>Back was clicked with batches that could still be undone; the page asks before leaving.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeaveText))]
    private bool _isConfirmingLeave;

    public bool IsBusy => IsWorking || IsRegrouping;

    public string BusyText => IsWorking ? _workingText : "Grouping photos…";

    public bool CanChangeStrictness => !IsWorking;

    public bool HasReport => ReportTitle.Length > 0;

    public bool HasUndo => _batches.Count > 0;

    public string UndoText => HasUndo ? $"Moved {PhotosAndSize(_batches[^1])} to the review folder." : "";

    public string EarlierBatchesText => _batches.Count > 1
        ? $"{DisplayText.Count(_batches.Count - 1, "earlier clean-up", "earlier clean-ups")} can be undone after this one."
        : "";

    public string UndoNote => $"Undo is available until you leave this page. The files stay in the {ReviewFolderName} folder either way.";

    public string DeleteNote => $"PhotoSweep never deletes photos. When you're sure, you can delete the {ReviewFolderName} folder yourself.";

    public string LeaveText => $"{DisplayText.Count(_batches.Count, "clean-up", "clean-ups")} can still be undone. {UndoNote}";

    /// <summary>
    /// Completes once any move or undo has finished; never throws. The window waits on it before closing, so the
    /// process doesn't exit half-way through a batch.
    /// </summary>
    public Task WaitForCleanupAsync() => _cleanupRun;

    private bool CanMove => SelectedCount > 0 && !IsWorking && !IsRegrouping;

    /// <summary>Checks the selection and, if it's valid, shows the confirmation. Nothing moves yet.</summary>
    [RelayCommand(CanExecute = nameof(CanMove))]
    private void Move()
    {
        ClearReport();
        IsConfirmingLeave = false;
        ErrorMessage = "";

        CleanupValidation validation;
        try
        {
            validation = _cleanup.Validate(Groups.Select(g => g.Group).ToList(), SelectedPaths(), Outcome.Request.Options.Folders);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't check the selection: {ex.Message}";
            return;
        }

        if (!validation.IsValid)
        {
            ShowReport("Nothing was moved",
                new ReportSection("Change the selection first:", validation.Problems.Select(p => $"{p.Path}: {p.Message}").ToList()));
            return;
        }

        var files = validation.Plan.Groups.SelectMany(g => g.Remove).ToList();
        ConfirmText = $"Move {DisplayText.Count(files.Count, "photo", "photos")} ({DisplayText.Bytes(files.Sum(m => m.File.SizeBytes))}) "
            + $"to the {ReviewFolderName} folder? Nothing is deleted, and you can undo this.";
        ConfirmFoldersHeading = $"{(files.Count == 1 ? "It" : "They")} will go into a dated folder, keeping their subfolders, inside:";
        ConfirmFolders = files.Select(m => m.Root).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, ReviewFolderName)).ToList();
        _pendingPlan = validation.Plan;
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelMove() => CloseConfirmation();

    private bool CanConfirmMove => IsConfirming && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanConfirmMove))]
    private Task ConfirmMoveAsync()
    {
        // The async command already refuses while it's running, but Execute() doesn't check CanExecute, so a view (or a
        // double-click racing the button disabling) could still get here. IsWorking is set before the first await.
        if (IsWorking || _pendingPlan is not { } plan)
            return Task.CompletedTask;

        CloseConfirmation();
        StartWork("Moving photos to the review folder…");
        return _cleanupRun = RunMoveAsync(plan);
    }

    private bool CanUndo => HasUndo && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync()
    {
        if (IsWorking || !HasUndo)
            return Task.CompletedTask;

        StartWork("Putting photos back…");
        return _cleanupRun = RunUndoAsync(_batches[^1]);
    }

    private bool CanOpenReviewFolder => HasUndo && !IsWorking;

    /// <summary>Opens the latest batch's folder in each root it touched (usually one).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenReviewFolder))]
    private void OpenReviewFolder()
    {
        var failed = _batches[^1].Batch.ManifestPaths
            .Select(manifest => Path.GetDirectoryName(manifest)!)
            .Where(folder => !_shell.OpenFolder(folder))
            .ToList();
        ErrorMessage = failed.Count > 0 ? $"Couldn't open {string.Join(", ", failed)}." : "";
    }

    [RelayCommand]
    private void DismissReport() => ClearReport();

    private bool CanLeave => !IsWorking;

    /// <summary>Leaves straight away, unless a batch could still be undone: then the page reminds the user first.</summary>
    [RelayCommand(CanExecute = nameof(CanLeave))]
    private void Back()
    {
        if (!HasUndo)
        {
            Leave();
            return;
        }

        CloseConfirmation();
        IsConfirmingLeave = true;
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private void Leave()
    {
        IsConfirmingLeave = false;
        _regrouping?.Cancel();
        _compare?.Close(); // it shows this page's groups, which are about to go
        _goBack();
    }

    [RelayCommand]
    private void Stay() => IsConfirmingLeave = false;

    private async Task RunMoveAsync(CleanupPlan plan)
    {
        try
        {
            var result = await _cleanup.MoveAsync(plan);
            ApplyMove(plan, result);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Something went wrong while moving photos: {ex.Message} Some may already be in the review folder; "
                + "scan again to see where everything is.";
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void ApplyMove(CleanupPlan plan, CleanupResult result)
    {
        var sizes = plan.Groups.SelectMany(g => g.Remove)
            .ToDictionary(m => m.File.Path, m => m.File.SizeBytes, StringComparer.OrdinalIgnoreCase);
        var scanPaths = ScanPathsOfShownPhotos();
        var moved = result.Moved.ToDictionary(
            m => m.OriginalPath,
            m => new MovedPhoto(scanPaths.GetValueOrDefault(m.OriginalPath, m.OriginalPath), sizes.GetValueOrDefault(m.OriginalPath)),
            StringComparer.OrdinalIgnoreCase);

        _movedOut.UnionWith(moved.Values.Select(p => p.ScanPath));
        if (moved.Count > 0)
            _batches.Add(new MovedBatch(result.Batch, moved));

        // Only "in use" is worth retrying as it stands. The rest changed since the scan (or can't be moved at all), so
        // they're deselected rather than left to fail the same way next time.
        var selection = SelectedPaths();
        selection.ExceptWith(moved.Keys);
        selection.ExceptWith(result.Failures.Where(f => f.Reason != CleanupFailureReason.InUse).Select(f => f.Path));
        Show(Level, selection);
        NotifyUndoChanged();

        if (result.Failures.Count > 0)
            ReportMoveFailures(result.Failures);
    }

    private void ReportMoveFailures(IReadOnlyList<CleanupFailure> failures)
    {
        var sections = new List<ReportSection>();
        AddSection(sections,
            "Open in another app, so Windows wouldn't move them. They're still selected: close that app and click Move again.",
            failures.Where(f => f.Reason == CleanupFailureReason.InUse).Select(f => f.Path));
        AddSection(sections,
            "Changed or missing since the scan, so they were left alone and deselected. Scan again to review them.",
            failures.Where(f => f.Reason is CleanupFailureReason.ChangedSinceScan or CleanupFailureReason.NotFound or CleanupFailureReason.KeptCopyMissing)
                .Select(f => $"{f.Path}: {RescanReason(f.Reason)}"));
        AddSection(sections,
            "Couldn't be moved, and were deselected:",
            failures.Where(f => f.Reason == CleanupFailureReason.IoError).Select(f => $"{f.Path}: {f.Message}"));

        ShowReport($"{DisplayText.Count(failures.Count, "photo", "photos")} couldn't be moved", [.. sections]);
    }

    private static string RescanReason(CleanupFailureReason reason) => reason switch
    {
        CleanupFailureReason.ChangedSinceScan => "it has changed since the scan.",
        CleanupFailureReason.NotFound => "it's no longer there.",
        _ => "the copy being kept is missing or has changed, so moving this one could lose the photo.",
    };

    private async Task RunUndoAsync(MovedBatch batch)
    {
        try
        {
            var result = await _cleanup.UndoAsync(batch.Batch);
            ApplyUndo(batch, result);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Something went wrong while putting photos back: {ex.Message} Click Undo to try again.";
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void ApplyUndo(MovedBatch batch, UndoResult result)
    {
        var selection = SelectedPaths();
        var newNames = new List<string>();
        foreach (var restored in result.Restored)
        {
            if (!batch.Files.Remove(restored.OriginalPath, out var photo))
                continue;

            PutBack(photo.ScanPath, restored.RestoredPath);
            selection.Add(restored.RestoredPath); // back as it was: selected for moving
            if (restored.AlternateName)
                newNames.Add($"{restored.OriginalPath} → {Path.GetFileName(restored.RestoredPath)}");
        }

        // In use or another I/O error: still in the review folder and still in the manifest, so Undo can try again.
        var retry = result.Failures.Where(f => f.Reason is CleanupFailureReason.InUse or CleanupFailureReason.IoError).ToList();
        var lost = result.Failures.Except(retry).ToList();
        var retryPaths = retry.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lostPaths = lost.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (original, photo) in batch.Files.ToList())
        {
            if (retryPaths.Contains(original))
                continue;

            batch.Files.Remove(original);
            // Neither restored nor reported: undo found the original already back in place (e.g. moved back by hand in
            // Explorer) and dropped it from the manifest. A missing manifest reports only its own path, so check the disk.
            // A file reported lost, or not at its original path, stays out of the groups.
            if (!lostPaths.Contains(original) && _availability.Check(original) != FileAvailability.Unavailable)
                PutBack(photo.ScanPath, original);
        }

        if (batch.Files.Count == 0)
            _batches.Remove(batch);

        Show(Level, selection);
        NotifyUndoChanged();

        var sections = new List<ReportSection>();
        AddSection(sections, "Put back under a new name, because another file is now at the original place:", newNames);
        AddSection(sections, "Couldn't be put back yet. They're still in the review folder; click Undo to try again:",
            retry.Select(f => $"{f.Path}: {(f.Reason == CleanupFailureReason.InUse ? "it's open in another app." : f.Message)}"));
        AddSection(sections, "No longer in the review folder, so they couldn't be put back:",
            lost.Select(f => $"{f.Path}: {f.Message}"));
        ShowReport(result.Restored.Count > 0 ? $"Put back {DisplayText.Count(result.Restored.Count, "photo", "photos")}." : "Nothing was put back.",
            [.. sections]);
    }

    private void PutBack(string scanPath, string currentPath)
    {
        _movedOut.Remove(scanPath);
        if (string.Equals(scanPath, currentPath, StringComparison.OrdinalIgnoreCase))
            _renamed.Remove(scanPath);
        else
            _renamed[scanPath] = currentPath;
    }

    /// <summary>Current path → scan path for photos on screen that undo renamed. Everything else is at its scan path.</summary>
    private Dictionary<string, string> ScanPathsOfShownPhotos() =>
        _renamed.Where(r => !_movedOut.Contains(r.Key)).ToDictionary(r => r.Value, r => r.Key, StringComparer.OrdinalIgnoreCase);

    private HashSet<string> SelectedPaths() =>
        Groups.SelectMany(g => g.Photos).Where(p => p.IsSelected).Select(p => p.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void StartWork(string text)
    {
        _workingText = text;
        ErrorMessage = "";
        ClearReport();
        IsConfirmingLeave = false;
        IsWorking = true;
    }

    private void CloseConfirmation()
    {
        _pendingPlan = null;
        IsConfirming = false;
    }

    private void ShowReport(string title, params ReportSection[] sections)
    {
        ReportSections = sections;
        ReportTitle = title;
    }

    private void ClearReport() => ShowReport("");

    private static void AddSection(List<ReportSection> sections, string heading, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count > 0)
            sections.Add(new ReportSection(heading, list));
    }

    private void NotifyUndoChanged()
    {
        OnPropertyChanged(nameof(HasUndo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(EarlierBatchesText));
        OnPropertyChanged(nameof(LeaveText));
        UndoCommand.NotifyCanExecuteChanged();
        OpenReviewFolderCommand.NotifyCanExecuteChanged();
    }

    private static string PhotosAndSize(MovedBatch batch) =>
        $"{DisplayText.Count(batch.Files.Count, "photo", "photos")} ({DisplayText.Bytes(batch.Files.Values.Sum(p => p.SizeBytes))})";

    private sealed record MovedPhoto(string ScanPath, long SizeBytes);

    /// <param name="Files">Keyed by the path each file was moved from, which is what undo reports back. Shrinks as files are put back.</param>
    private sealed record MovedBatch(CleanupBatch Batch, Dictionary<string, MovedPhoto> Files);
}
