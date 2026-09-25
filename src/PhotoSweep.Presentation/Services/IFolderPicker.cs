namespace PhotoSweep.Presentation.Services;

/// <summary>Asks the user to choose folders. Implemented in Desktop with the Windows folder dialog; faked in tests.</summary>
public interface IFolderPicker
{
    /// <summary>The chosen folders, or an empty list if the user cancelled.</summary>
    IReadOnlyList<string> PickFolders();
}
