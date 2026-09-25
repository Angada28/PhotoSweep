using System.ComponentModel;
using System.Windows;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Desktop.Services;
using PhotoSweep.Presentation;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop;

/// <summary>
/// Composition root: the one place that picks the Windows implementations of Presentation's service interfaces and
/// hands them to the view-models. Wired by hand; there are few enough services that a DI container adds nothing yet.
/// </summary>
public partial class App : Application
{
    private MainViewModel? _main;
    private bool _readyToClose;
    private bool _waitingToClose;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _main = new MainViewModel(
            new WpfFolderPicker(),
            new WindowsKnownFolders(),
            new ScanService(ScanCache.DefaultPath),
            new CleanupService(),
            new ExplorerShellService(),
            new DiskFileAvailability());
        var window = new MainWindow { DataContext = _main };
        window.Closing += OnMainWindowClosing;
        window.Show();
    }

    /// <summary>
    /// Closing mid-scan (or mid-move): hold the window open, let the view-model cancel the scan and wait for it to save
    /// its cache (or let the move finish), then close for real. The page shows "Cancelling…" or "Moving…" meanwhile. Without this, the process could exit while the
    /// scan's worker threads are still running and lose the work since the last save.
    /// </summary>
    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose || _main is null)
            return;

        var pending = _main.PrepareToCloseAsync();
        if (pending.IsCompleted)
            return;

        e.Cancel = true;
        if (_waitingToClose)
            return; // closed again while already waiting

        _waitingToClose = true;
        await pending; // never throws: the pages turn every outcome into a state
        _readyToClose = true;
        ((Window)sender!).Close();
    }
}
