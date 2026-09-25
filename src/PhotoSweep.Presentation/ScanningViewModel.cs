using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

public enum ScanState
{
    /// <summary>Scanning or grouping.</summary>
    Running,

    /// <summary>Cancel was pressed; waiting for the scan to stop and save the cache.</summary>
    Cancelling,

    /// <summary>The scan skipped online-only files and is waiting for the user to decide about them.</summary>
    OnlineOnlyChoice,

    /// <summary>Stopped with a message; only Back is offered.</summary>
    Failed,
}

public enum ScanStage
{
    /// <summary>The folder walk is still running (files are checked as they're found), so the total isn't known.</summary>
    FindingPhotos,

    /// <summary>Every file has been found; the rest are being checked.</summary>
    CheckingPhotos,

    Grouping,
}

/// <summary>
/// The scanning page: runs the scan, then the grouping, and shows live progress. Every step's outcome
/// (finished, cancelled, failed, waiting for a choice) is a <see cref="State"/> the view switches on.
/// </summary>
/// <remarks>
/// Threading: the work runs on the thread pool inside <see cref="IScanService"/>. This class only runs on the UI
/// thread: <see cref="Progress{T}"/> is created here, so it captures the UI's SynchronizationContext and posts each
/// report back to it, and every <c>await</c> resumes on the UI thread for the same reason.
/// </remarks>
public sealed partial class ScanningViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly Action _goBack;
    private readonly Action<ScanOutcome> _showResults;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();

    // A pausable stopwatch: it runs while work runs, and stops while the page waits for the user.
    private TimeSpan _elapsedBefore;
    private long? _runningSince;
    private PeriodicTimer? _ticker;

    private Task _work = Task.CompletedTask;
    private ScanResult? _pendingScan;
    private IReadOnlyList<string> _pendingUnreadable = [];

    public ScanningViewModel(ScanRequest request, IScanService scanService, Action goBack, Action<ScanOutcome> showResults, TimeProvider? time = null)
    {
        Request = request;
        _scanService = scanService;
        _goBack = goBack;
        _showResults = showResults;
        _time = time ?? TimeProvider.System;
    }

    public ScanRequest Request { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(ShowProgress), nameof(ShowOnlineOnlyChoice), nameof(ShowError))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(ContinueWithoutCommand), nameof(DownloadAndScanCommand))]
    private ScanState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private ScanStage _stage;

    [ObservableProperty]
    private int _found;

    [ObservableProperty]
    private int _checked;

    [ObservableProperty]
    private int _fromCache;

    [ObservableProperty]
    private int _errors;

    /// <summary>True until the total is known (and while grouping), so the bar animates instead of showing a fake percentage.</summary>
    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _onlineOnlyMessage = "";

    public string Heading => State switch
    {
        ScanState.Cancelling => "Cancelling…",
        ScanState.Failed => "The scan stopped",
        ScanState.OnlineOnlyChoice => "Some photos are only in the cloud",
        _ => Stage switch
        {
            ScanStage.FindingPhotos => "Finding photos…",
            ScanStage.CheckingPhotos => "Checking photos…",
            _ => "Grouping photos…",
        },
    };

    public bool ShowProgress => State is ScanState.Running or ScanState.Cancelling;

    public bool ShowOnlineOnlyChoice => State == ScanState.OnlineOnlyChoice;

    public bool ShowError => State == ScanState.Failed;

    public TimeSpan Elapsed => _elapsedBefore + (_runningSince is { } since ? _time.GetElapsedTime(since) : TimeSpan.Zero);

    public string ElapsedText => DisplayText.Duration(Elapsed);

    /// <summary>Starts the scan. Called once, by <see cref="MainViewModel"/>, after the page is on screen. Never throws.</summary>
    public Task RunAsync() => Begin(() => ScanAsync(Request.Options));

    /// <summary>For closing the window mid-scan: cancels, and completes once the scan has stopped and saved its cache.</summary>
    public Task CancelAndWaitAsync()
    {
        if (CancelCommand.CanExecute(null))
            CancelCommand.Execute(null);
        return _work;
    }

    private bool CanCancel => State is ScanState.Running or ScanState.OnlineOnlyChoice;

    /// <summary>
    /// Only requests cancellation. Going back happens when the scan task actually ends (see <see cref="RunGuardedAsync"/>),
    /// which is after the scanner has saved its cache, so a new scan can't overlap that save.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        var waitingForUser = State == ScanState.OnlineOnlyChoice;
        State = ScanState.Cancelling;
        _cts.Cancel();
        if (waitingForUser)
            _goBack(); // nothing is running, so there's nothing to wait for
    }

    private bool IsChoosing => State == ScanState.OnlineOnlyChoice;

    /// <summary>Groups what was scanned. Deliberately reuses the finished scan and never touches the options.</summary>
    [RelayCommand(CanExecute = nameof(IsChoosing))]
    private void ContinueWithout() => _ = Begin(() => GroupAsync(_pendingScan!, _pendingUnreadable));

    /// <summary>
    /// The only place <see cref="ScanOptions.IncludeOnlineOnlyFiles"/> is set, and only from this button (CLAUDE.md
    /// rule 6). Files already scanned come from the cache, so in practice only the cloud files are read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsChoosing))]
    private void DownloadAndScan() => _ = Begin(() => ScanAsync(Request.Options with { IncludeOnlineOnlyFiles = true }));

    /// <summary>Shown with an error.</summary>
    [RelayCommand]
    private void Back() => _goBack();

    private Task Begin(Func<Task> work) => _work = RunGuardedAsync(work);

    /// <summary>Turns every way a step can end into a state or a navigation, so nothing escapes to crash the app.</summary>
    private async Task RunGuardedAsync(Func<Task> work)
    {
        State = ScanState.Running;
        StartClock();
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _goBack();
        }
        catch (Exception ex)
        {
            Fail($"The scan stopped because of an unexpected error:\n{ex.Message}");
        }
        finally
        {
            StopClock();
        }
    }

    private async Task ScanAsync(ScanOptions options)
    {
        Stage = ScanStage.FindingPhotos;
        Apply(new ScanProgress(0, 0, 0, 0, 0, 0, EnumerationComplete: false));

        var scan = await _scanService.ScanAsync(options, new Progress<ScanProgress>(Apply), _cts.Token);

        var unreadable = UnreadableFolders(scan);
        if (unreadable.Count > 0 && unreadable.Count == Request.Options.Folders.Count)
        {
            Fail(AllFoldersUnreadableMessage(scan, unreadable));
            return;
        }

        if (scan.OnlineOnlySkipped.Count > 0)
        {
            _pendingScan = scan;
            _pendingUnreadable = unreadable;
            var n = scan.OnlineOnlySkipped.Count;
            OnlineOnlyMessage = $"{DisplayText.Count(n, "photo", "photos")} ({DisplayText.Bytes(scan.OnlineOnlyBytes)}) "
                + (n == 1 ? "is only in the cloud and was skipped." : "are only in the cloud and were skipped.");
            State = ScanState.OnlineOnlyChoice;
            return;
        }

        await GroupAsync(scan, unreadable);
    }

    private async Task GroupAsync(ScanResult scan, IReadOnlyList<string> unreadable)
    {
        Stage = ScanStage.Grouping;
        IsIndeterminate = true;

        var groups = await _scanService.GroupAsync(scan, Request.Level, _cts.Token);

        _cts.Token.ThrowIfCancellationRequested(); // Cancel pressed just as grouping finished: still go back
        _showResults(new ScanOutcome(Request, scan, groups, unreadable));
    }

    private void Apply(ScanProgress p)
    {
        if (Stage == ScanStage.Grouping)
            return; // a report that arrived after the scan finished

        Found = p.Found;
        Checked = p.Processed;
        FromCache = p.CacheHits;
        Errors = p.Errors;
        IsIndeterminate = !p.EnumerationComplete;
        Stage = p.EnumerationComplete ? ScanStage.CheckingPhotos : ScanStage.FindingPhotos;
        Percent = p.EnumerationComplete && p.Found > 0 ? 100.0 * p.Processed / p.Found : 0;
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        State = ScanState.Failed;
    }

    /// <summary>
    /// Chosen folders the scanner couldn't open. It records those as an Unreadable entry whose path is the folder
    /// itself (then carries on with the others), and a folder path can never be a photo's path.
    /// </summary>
    private List<string> UnreadableFolders(ScanResult scan) =>
        Request.Options.Folders.Where(folder => FolderError(scan, folder) is not null).ToList();

    private static ScannedFile? FolderError(ScanResult scan, string folder) =>
        scan.Errors.FirstOrDefault(e => e.Status == ScanStatus.Unreadable
            && string.Equals(FolderPaths.Normalize(e.Path), FolderPaths.Normalize(folder), StringComparison.OrdinalIgnoreCase));

    private static string AllFoldersUnreadableMessage(ScanResult scan, List<string> folders)
    {
        var header = folders.Count == 1
            ? "The folder couldn't be read. It may have been moved, renamed or disconnected."
            : "None of the folders could be read. They may have been moved, renamed or disconnected.";
        return header + "\n\n" + string.Join("\n", folders.Select(f => $"{f}\n    {FolderError(scan, f)?.Error}"));
    }

    private void StartClock()
    {
        if (_runningSince is not null)
            return;

        _runningSince = _time.GetTimestamp();
        // Given the TimeProvider so a fake clock in tests drives the ticks too. Each tick only refreshes the text;
        // the time itself is always read from the clock, so a late or missed tick can't make it drift.
        _ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        _ = TickAsync(_ticker);
    }

    private void StopClock()
    {
        if (_runningSince is not { } since)
            return;

        _elapsedBefore += _time.GetElapsedTime(since);
        _runningSince = null;
        _ticker?.Dispose(); // ends TickAsync: WaitForNextTickAsync returns false once disposed
        _ticker = null;
        OnPropertyChanged(nameof(ElapsedText));
    }

    private async Task TickAsync(PeriodicTimer ticker)
    {
        while (await ticker.WaitForNextTickAsync())
            OnPropertyChanged(nameof(ElapsedText));
    }
}
