namespace PhotoSweep.Core.Scanning;

/// <summary>
/// Recognises cloud placeholder files (OneDrive "online-only" and other Windows Cloud Files providers) from the
/// attributes in the directory listing alone. Opening such a file, even just to read it, makes Windows download it.
/// </summary>
public static class CloudFileAttributes
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. Windows defines these but .NET's
    // FileAttributes enum doesn't name them, so they are declared here from the Win32 values.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private const FileAttributes OnlineOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    public static bool IsOnlineOnly(FileAttributes attributes) => (attributes & OnlineOnlyMask) != 0;

    /// <summary>
    /// Whether the file at <paramref name="path"/> can be read without downloading anything, checked now. Reads the
    /// attributes only (<see cref="File.GetAttributes(string)"/> doesn't open the file), so it's safe on a cloud file.
    /// </summary>
    public static FileAvailability Check(string path)
    {
        try
        {
            return IsOnlineOnly(File.GetAttributes(path)) ? FileAvailability.OnlineOnly : FileAvailability.Local;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Gone, or its attributes can't be read. Either way nobody can tell whether opening it would download
            // it, so callers must not open it.
            return FileAvailability.Unavailable;
        }
    }
}

public enum FileAvailability
{
    /// <summary>Stored on this PC; opening it reads the local copy.</summary>
    Local,

    /// <summary>A cloud placeholder: opening it would download it.</summary>
    OnlineOnly,

    /// <summary>Missing (moved or deleted since the scan) or its attributes can't be read.</summary>
    Unavailable,
}
