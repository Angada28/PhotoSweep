using System.Security.Cryptography;
using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Imaging;
using SixLabors.ImageSharp;

namespace PhotoSweep.Core.Scanning;

/// <summary>
/// Reads one file: SHA-256 of its bytes (exact copies), then the perceptual fingerprint (look-alikes), then the
/// header details (resolution, camera EXIF) used to pick which copy to keep.
/// Never throws for a bad file; every failure except cancellation becomes a per-file error in the result.
/// </summary>
/// <param name="fingerprint">
/// How to fingerprint a decoded stream. Defaults to <see cref="Fingerprinter.Compute(Stream)"/>; tests pass a
/// throwing function to exercise the error paths.
/// </param>
public sealed class FileAnalyzer(Func<Stream, ImageFingerprint>? fingerprint = null)
{
    private readonly Func<Stream, ImageFingerprint> _fingerprint = fingerprint ?? Fingerprinter.Compute;

    public async Task<ScannedFile> AnalyzeAsync(FileCandidate file, CancellationToken ct = default)
    {
        var result = ScannedFile.From(file);
        string? sha = null;
        try
        {
            // One open, three reads: hash the whole file, rewind and decode, rewind and read the header for size and
            // EXIF. The later reads are served from the OS cache.
            await using var stream = new FileStream(file.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            });

            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            stream.Position = 0;
            var fp = _fingerprint(stream);
            stream.Position = 0;
            var details = ImageDetails.Read(stream);

            return result with { Status = ScanStatus.Ok, Sha256 = sha, Fingerprint = fp, Details = details };
        }
        catch (ImageFormatException ex)
        {
            // Base class of UnknownImageFormatException and InvalidImageContentException. The SHA is kept, so two
            // identical copies of an undecodable file (e.g. HEIC) are still found as exact duplicates.
            var error = ex is UnknownImageFormatException ? "Unsupported image format" : $"Corrupt image: {ex.Message}";
            return result with { Status = ScanStatus.DecodeFailed, Sha256 = sha, Error = error };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result with { Status = ScanStatus.Unreadable, Sha256 = sha, Error = $"Can't read file: {ex.Message}" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else is probably a bug, but one odd file mustn't stop a scan of thousands.
            // Cancellation is excluded so it still propagates and stops the scan.
            return result with
            {
                Status = ScanStatus.UnexpectedError,
                Sha256 = sha,
                Error = $"Unexpected error ({ex.GetType().Name}): {ex.Message}",
            };
        }
    }
}
