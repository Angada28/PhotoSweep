namespace PhotoSweep.Tests.Scanning;

/// <summary>
/// A throwaway folder under %TEMP% filled with copies of the TestData photos. The cache file lives next to
/// <see cref="Root"/>, not inside it, like the real cache in %LocalAppData%. Dispose removes the whole temp folder;
/// that's cleanup of our own test copies, not user photos.
/// </summary>
internal sealed class TempPhotoFolder : IDisposable
{
    private static readonly string PhotoDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Photos");

    private readonly string _base;

    public TempPhotoFolder()
    {
        _base = Path.Combine(Path.GetTempPath(), "PhotoSweepTests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_base, "photos");
        Outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        CachePath = Path.Combine(_base, "scan-cache.json");
    }

    /// <summary>The folder to scan.</summary>
    public string Root { get; }

    /// <summary>A sibling folder that is never scanned, for moving files "away".</summary>
    public string Outside { get; }

    public string CachePath { get; }

    /// <summary>Copies a TestData photo to <paramref name="relativePath"/> under <see cref="Root"/> (defaults to its own name).</summary>
    public string AddPhoto(string testPhoto, string? relativePath = null)
    {
        var target = PathFor(relativePath ?? testPhoto);
        File.Copy(Path.Combine(PhotoDir, testPhoto), target);
        return target;
    }

    public string AddBytes(string relativePath, byte[] bytes)
    {
        var target = PathFor(relativePath);
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public static byte[] ReadTestPhoto(string testPhoto) => File.ReadAllBytes(Path.Combine(PhotoDir, testPhoto));

    private string PathFor(string relativePath)
    {
        var target = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail a test.
        }
    }
}
