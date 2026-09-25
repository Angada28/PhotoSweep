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
}
