namespace PhotoSweep.Presentation.Services;

/// <summary>Opens the app's extra windows. Implemented in Desktop (WPF); faked in tests.</summary>
public interface IWindowService
{
    /// <summary>
    /// Shows the compare window for <paramref name="viewModel"/>, or brings it to the front if it's already showing.
    /// The window closes when the view-model raises <see cref="CompareViewModel.CloseRequested"/>, and calls
    /// <see cref="CompareViewModel.Close"/> when the user closes it.
    /// </summary>
    void ShowCompare(CompareViewModel viewModel);
}
