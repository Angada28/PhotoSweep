using CommunityToolkit.Mvvm.ComponentModel;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// The app shell. <see cref="CurrentPage"/> is the page view-model on screen; the window picks the matching view with
/// a DataTemplate keyed on its type. Pages navigate through the callbacks passed to them here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly ICleanupService _cleanup;
    private readonly IShellService _shell;
    private readonly IFileAvailability _availability;
    private readonly IWindowService _windows;
    private readonly IPreviewLoader _previews;
    private readonly TimeProvider _time;

    public MainViewModel(
        IFolderPicker folderPicker,
        IKnownFolders knownFolders,
        IScanService scanService,
        ICleanupService cleanup,
        IShellService shell,
        IFileAvailability availability,
        IWindowService windows,
        IPreviewLoader previews,
        TimeProvider? time = null)
    {
        _scanService = scanService;
        _cleanup = cleanup;
        _shell = shell;
        _availability = availability;
        _windows = windows;
        _previews = previews;
        _time = time ?? TimeProvider.System;
        Start = new StartViewModel(folderPicker, knownFolders, StartScan);
        _currentPage = Start;
    }

    /// <summary>Kept for the app's lifetime, so going back shows the same folders and strictness.</summary>
    public StartViewModel Start { get; }

    [ObservableProperty]
    private object _currentPage;

    /// <summary>
    /// Called when the window is closing. Completes straight away unless something is running: a scan is cancelled and
    /// awaited until it has saved its cache; a move or undo is awaited until it finishes (it isn't stopped half-way).
    /// </summary>
    public Task PrepareToCloseAsync() => CurrentPage switch
    {
        ScanningViewModel scanning => scanning.CancelAndWaitAsync(),
        ResultsViewModel results => results.WaitForCleanupAsync(),
        _ => Task.CompletedTask,
    };

    private void StartScan(ScanRequest request)
    {
        var scanning = new ScanningViewModel(request, _scanService, GoToStart, ShowResults, _time);
        CurrentPage = scanning;
        _ = scanning.RunAsync(); // never throws: every outcome becomes a page state or a navigation
    }

    private void ShowResults(ScanOutcome outcome) => CurrentPage = new ResultsViewModel(outcome, _scanService, _cleanup, _shell, _availability, _windows, _previews, GoToStart, _time);

    private void GoToStart() => CurrentPage = Start;
}
