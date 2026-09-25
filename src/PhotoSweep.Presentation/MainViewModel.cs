using CommunityToolkit.Mvvm.ComponentModel;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// The app shell. <see cref="CurrentPage"/> is the page view-model on screen; the window picks the matching view with
/// a DataTemplate keyed on its type. Pages navigate through the callbacks passed to them here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(IFolderPicker folderPicker, IKnownFolders knownFolders)
    {
        Start = new StartViewModel(folderPicker, knownFolders, request => CurrentPage = new ScanningViewModel(request, GoToStart));
        _currentPage = Start;
    }

    /// <summary>Kept for the app's lifetime, so going back shows the same folders and strictness.</summary>
    public StartViewModel Start { get; }

    [ObservableProperty]
    private object _currentPage;

    private void GoToStart() => CurrentPage = Start;
}
