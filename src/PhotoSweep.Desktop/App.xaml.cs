using System.Windows;
using PhotoSweep.Desktop.Services;
using PhotoSweep.Presentation;

namespace PhotoSweep.Desktop;

/// <summary>
/// Composition root: the one place that picks the Windows implementations of Presentation's service interfaces and
/// hands them to the view-models. Wired by hand; there are few enough services that a DI container adds nothing yet.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var main = new MainViewModel(new WpfFolderPicker(), new WindowsKnownFolders());
        new MainWindow { DataContext = main }.Show();
    }
}
