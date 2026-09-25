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
    private readonly IFileAvailability _availability;
    private readonly TimeProvider _time;

    public MainViewModel(
        IFolderPicker folderPicker,
        IKnownFolders knownFolders,
        IScanService scanService,
        IFileAvailability availability,
        TimeProvider? time = null)
    {
        _scanService = scanService;
        _availability = availability;
        _time = time ?? TimeProvider.System;
        Start = new StartViewModel(folderPicker, knownFolders, StartScan);
        _currentPage = Start;
    }

    /// <summary>Kept for the app's lifetime, so going back shows the same folders and strictness.</summary>
    public StartViewModel Start { get; }

    [ObservableProperty]
    private object _currentPage;

    /// <summary>
    /// Called when the window is closing. Completes straight away unless a scan is running; then it cancels the scan
    /// and completes once the scan has stopped and saved its cache.
    /// </summary>
    public Task PrepareToCloseAsync() =>
        CurrentPage is ScanningViewModel scanning ? scanning.CancelAndWaitAsync() : Task.CompletedTask;

    private void StartScan(ScanRequest request)
    {
        var scanning = new ScanningViewModel(request, _scanService, GoToStart, ShowResults, _time);
        CurrentPage = scanning;
        _ = scanning.RunAsync(); // never throws: every outcome becomes a page state or a navigation
    }

    private void ShowResults(ScanOutcome outcome) => CurrentPage = new ResultsViewModel(outcome, _scanService, _availability, GoToStart, _time);

    private void GoToStart() => CurrentPage = Start;
}
