using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Tests.Grouping;

/// <summary>Builds made-up <see cref="ScannedFile"/>s, so grouping can be tested without real photos.</summary>
internal static class Files
{
    public static readonly DateTime DefaultTime = new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public static ScannedFile Photo(
        string name,
        ulong pHash = 0,
        ulong dHash = 0,
        string? sha = null,
        int width = 1000,
        int height = 750,
        long size = 100_000,
        DateTime? modified = null,
        string? camera = null) =>
        new(Path.Combine(@"C:\Photos", name), size, modified ?? DefaultTime)
        {
            Status = ScanStatus.Ok,
            Sha256 = sha ?? "SHA-" + name, // distinct per file unless a test says otherwise
            Fingerprint = new ImageFingerprint(pHash, dHash),
            Details = new ImageDetails(width, height) { CameraModel = camera },
        };

    /// <summary>A file that was hashed but couldn't be decoded, e.g. HEIC.</summary>
    public static ScannedFile Undecodable(string name, string sha, long size = 100_000) =>
        new(Path.Combine(@"C:\Photos", name), size, DefaultTime) { Status = ScanStatus.DecodeFailed, Sha256 = sha };

    /// <summary>A hash with the lowest <paramref name="count"/> bits set: exactly <paramref name="count"/> bits from 0.</summary>
    public static ulong Bits(int count) => count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
}
