using System.Windows;
using PhotoSweep.Desktop.Views;
using PhotoSweep.Presentation;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>Opens the compare window. One at a time: showing a new view-model replaces the old window.</summary>
public sealed class WpfWindowService : IWindowService
{
    private CompareWindow? _compare;

    public void ShowCompare(CompareViewModel viewModel)
    {
        if (_compare is { } open && ReferenceEquals(open.DataContext, viewModel))
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        _compare?.Close();

        // Owned by the main window: stays in front of it and closes with it.
        var window = new CompareWindow { DataContext = viewModel, Owner = Application.Current.MainWindow };
        void OnCloseRequested(object? sender, EventArgs e) => window.Close();
        viewModel.CloseRequested += OnCloseRequested;
        window.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Close(); // closed by the user: stop loading and following the results page (no-op if the VM asked)
            if (ReferenceEquals(_compare, window))
                _compare = null;
        };

        _compare = window;
        window.Show();
    }
}
