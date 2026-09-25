using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Presentation;

/// <summary>
/// The start screen: which folders to scan and how strict to be. Every way of adding folders (picker, quick buttons,
/// drag and drop) goes through <see cref="AddFolders"/>, so the no-duplicates / no-nesting rule lives in one place.
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    private readonly IFolderPicker _folderPicker;
    private readonly IKnownFolders _knownFolders;
    private readonly Action<ScanRequest> _startScan;

    public StartViewModel(IFolderPicker folderPicker, IKnownFolders knownFolders, Action<ScanRequest> startScan)
    {
        _folderPicker = folderPicker;
        _knownFolders = knownFolders;
        _startScan = startScan;
        _selectedStrictness = StrictnessOption.For(MatchLevel.SamePhoto);
        Folders.CollectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Chosen folders, normalized. None is inside another.</summary>
    public ObservableCollection<string> Folders { get; } = [];

    public IReadOnlyList<StrictnessOption> Strictness => StrictnessOption.All;

    [ObservableProperty]
    private StrictnessOption _selectedStrictness;

    public bool HasPictures => _knownFolders.Pictures is not null;

    public bool HasOneDrive => _knownFolders.OneDrive is not null;

    [RelayCommand]
    private void AddFolder() => AddFolders(_folderPicker.PickFolders());

    [RelayCommand(CanExecute = nameof(HasPictures))]
    private void AddPictures() => AddFolders([_knownFolders.Pictures!]);

    [RelayCommand(CanExecute = nameof(HasOneDrive))]
    private void AddOneDrive() => AddFolders([_knownFolders.OneDrive!]);

    /// <summary>Paths dropped on the window. Files and paths that don't exist are ignored; only folders are added.</summary>
    [RelayCommand]
    private void DropFolders(IReadOnlyList<string>? paths) => AddFolders((paths ?? []).Where(Directory.Exists));

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder is not null)
            Folders.Remove(folder);
    }

    private bool CanScan => Folders.Count > 0;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan() =>
        _startScan(new ScanRequest(new ScanOptions { Folders = [.. Folders] }, SelectedStrictness.Level));

    /// <summary>
    /// Adds each folder unless a chosen folder already covers it. A new folder that contains chosen ones replaces
    /// them: scanning is recursive, so they would otherwise be listed twice for nothing.
    /// </summary>
    private void AddFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(FolderPaths.Normalize))
        {
            if (Folders.Any(chosen => FolderPaths.IsSameOrInside(path, chosen)))
                continue;

            foreach (var inner in Folders.Where(chosen => FolderPaths.IsSameOrInside(chosen, path)).ToList())
                Folders.Remove(inner);

            Folders.Add(path);
        }
    }
}
