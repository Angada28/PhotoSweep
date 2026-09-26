using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Eval.Synthetic;

/// <summary>The originals' draw order, the result of <see cref="PhotoSampler.Order"/>.</summary>
public sealed record SampleOrder(IReadOnlyList<FileCandidate> Files, int OnlineOnlySkipped);

/// <summary>Picks the originals for the synthetic set: a seeded shuffle, so the same folder and seed give the same photos.</summary>
public static class PhotoSampler
{
    /// <summary>
    /// Still-image formats ImageSharp decodes. GIF is left out (burst covers and animations aren't "photos" to
    /// re-save), and HEIC because ImageSharp 3.1 can't decode it.
    /// </summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp",
    };

    /// <summary>Lists candidate originals with the app's own walker (same skips: hidden files, review folder, links).</summary>
    public static IEnumerable<FileCandidate> List(string folder) =>
        FileWalker.Enumerate(folder, new ScanOptions { Folders = [folder], Extensions = Extensions });

    /// <summary>
    /// Sorted by path, then shuffled with <paramref name="seed"/>: sorting first makes the order independent of how the
    /// file system happened to list the files. Online-only files are dropped here, from the listing's attributes, so
    /// they are never opened (CLAUDE.md rule 6). The caller takes files in this order until it has enough that decode.
    /// </summary>
    public static SampleOrder Order(IEnumerable<FileCandidate> files, int seed)
    {
        var all = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var local = all.Where(f => !CloudFileAttributes.IsOnlineOnly(f.Attributes)).ToArray();
        new Random(seed).Shuffle(local);
        return new SampleOrder(local, all.Count - local.Length);
    }
}
