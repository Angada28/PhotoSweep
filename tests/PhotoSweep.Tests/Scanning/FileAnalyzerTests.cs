using System.Security.Cryptography;
using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;

namespace PhotoSweep.Tests.Scanning;

public class FileAnalyzerTests : IDisposable
{
    private readonly TempPhotoFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Task<ScannedFile> Analyze(string path, FileAnalyzer? analyzer = null) =>
        (analyzer ?? new FileAnalyzer()).AnalyzeAsync(FileCandidate.FromPath(path));

    [Fact]
    public async Task Photo_gets_sha256_and_the_same_fingerprint_as_Fingerprinter()
    {
        var path = _temp.AddPhoto("chelsea.jpg");

        var result = await Analyze(path);

        Assert.Equal(ScanStatus.Ok, result.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), result.Sha256);
        Assert.Equal(Fingerprinter.Compute(path), result.Fingerprint);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("garbage.jpg")]
    [InlineData("phone.heic")] // ImageSharp 3.1 has no HEIC decoder
    public async Task Undecodable_file_is_reported_as_unsupported_but_still_hashed(string name)
    {
        var path = _temp.AddBytes(name, "this is not an image"u8.ToArray());

        var result = await Analyze(path);

        Assert.Equal(ScanStatus.DecodeFailed, result.Status);
        Assert.Contains("Unsupported", result.Error);
        Assert.NotNull(result.Sha256);
        Assert.Null(result.Fingerprint);
    }

    // Cut off inside the headers: ImageSharp throws InvalidImageContentException. (Cut off later, e.g. half the
    // file, ImageSharp decodes what's there and pads with grey, so that isn't an error.)
    [Theory]
    [InlineData("astronaut.jpg", 2000)]
    [InlineData("coffee.png", 100)]
    public async Task File_truncated_in_its_headers_is_reported_as_corrupt(string photo, int keepBytes)
    {
        var path = _temp.AddBytes("broken" + Path.GetExtension(photo), TempPhotoFolder.ReadTestPhoto(photo)[..keepBytes]);

        var result = await Analyze(path);

        Assert.Equal(ScanStatus.DecodeFailed, result.Status);
        Assert.Contains("Corrupt", result.Error);
        Assert.NotNull(result.Sha256);
    }

    // Found while writing these tests: on this input (a JPEG quantisation table with an impossible length),
    // ImageSharp 3.1.12 throws NullReferenceException instead of an ImageFormatException. It must be recorded
    // as a per-file error, not crash the scan.
    [Fact]
    public async Task ImageSharp_bug_on_malformed_jpeg_is_an_unexpected_error_not_a_crash()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x02 }.Concat(Enumerable.Repeat((byte)0x5A, 500)).ToArray();
        var path = _temp.AddBytes("malformed.jpg", bytes);

        var result = await Analyze(path);

        Assert.True(result.IsError);
        Assert.NotNull(result.Sha256);
    }

    [Fact]
    public async Task Any_ImageFormatException_subclass_is_a_decode_failure()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        var analyzer = new FileAnalyzer(_ => throw new InvalidImageContentException("bad scan line"));

        var result = await Analyze(path, analyzer);

        Assert.Equal(ScanStatus.DecodeFailed, result.Status);
        Assert.Contains("bad scan line", result.Error);
    }

    [Fact]
    public async Task Locked_file_is_unreadable()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await Analyze(path);

        Assert.Equal(ScanStatus.Unreadable, result.Status);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task Unexpected_exception_becomes_a_per_file_error_naming_the_type()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        var analyzer = new FileAnalyzer(_ => throw new InvalidOperationException("boom"));

        var result = await Analyze(path, analyzer);

        Assert.Equal(ScanStatus.UnexpectedError, result.Status);
        Assert.Contains("Unexpected", result.Error);
        Assert.Contains(nameof(InvalidOperationException), result.Error);
        Assert.NotNull(result.Sha256); // hashing succeeded before the failure
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_an_error()
    {
        var path = _temp.AddPhoto("coffee.jpg");
        var analyzer = new FileAnalyzer(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyze(path, analyzer));
    }
}
