using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Presentation.Services;

/// <summary>
/// Checks, right now, whether a photo is on this PC, only in the cloud or gone. The results page asks each time a
/// photo comes on screen, because OneDrive can free up a file after the scan. Faked in tests.
/// </summary>
public interface IFileAvailability
{
    FileAvailability Check(string path);
}

/// <summary>The real check: file attributes only, so it never downloads anything.</summary>
public sealed class DiskFileAvailability : IFileAvailability
{
    public FileAvailability Check(string path) => CloudFileAttributes.Check(path);
}
