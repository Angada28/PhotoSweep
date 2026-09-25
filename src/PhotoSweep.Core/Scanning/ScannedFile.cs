using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Core.Scanning;

public enum ScanStatus
{
    /// <summary>Hashed and fingerprinted.</summary>
    Ok,

    /// <summary>Read and SHA-256 hashed, but ImageSharp couldn't decode it (unsupported format or corrupt).</summary>
    DecodeFailed,

    /// <summary>Couldn't be read at all (locked, access denied, folder missing).</summary>
    Unreadable,

    /// <summary>Some other exception. Recorded per file so one bad file can't stop the scan; likely a bug worth reporting.</summary>
    UnexpectedError,

    /// <summary>Cloud file that isn't stored locally; not opened because that would download it.</summary>
    OnlineOnlySkipped,
}

/// <summary>The outcome of scanning one file.</summary>
public sealed record ScannedFile(string Path, long SizeBytes, DateTime LastWriteUtc)
{
    public ScanStatus Status { get; init; }

    /// <summary>Upper-case hex SHA-256 of the file's bytes. Equal hashes mean byte-identical files.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Perceptual fingerprint, present only when the image decoded.</summary>
    public ImageFingerprint? Fingerprint { get; init; }

    public string? Error { get; init; }

    public bool IsError => Status is ScanStatus.DecodeFailed or ScanStatus.Unreadable or ScanStatus.UnexpectedError;

    internal static ScannedFile From(FileCandidate file) => new(file.Path, file.Size, file.LastWriteUtc);
}
