namespace PhotoSweep.Presentation.Services;

/// <summary>Hands things to Windows. Implemented in Desktop (Explorer); faked in tests.</summary>
public interface IShellService
{
    /// <summary>Opens the folder in Explorer. False if it doesn't exist or Explorer couldn't be started.</summary>
    bool OpenFolder(string path);

    /// <summary>
    /// Opens the file's folder in Explorer with the file selected. Selecting doesn't open the file, so an online-only
    /// file isn't downloaded. False if the file doesn't exist or Explorer couldn't be started.
    /// </summary>
    bool ShowInFolder(string filePath);
}
