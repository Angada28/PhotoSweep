using System.Collections.Concurrent;
using System.Diagnostics;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoSweep.Eval.Precision;

/// <summary>
/// <c>review &lt;folder&gt; --out &lt;dir&gt; [--p 8] [--d 8] [--per-bucket 30] [--seed 20260926] [--cache file]</c>: scans and
/// groups the folder, draws a stratified sample of pairs, and writes &lt;out&gt;/review.html plus its thumbnails.
/// </summary>
public static class ReviewCommand
{
    public const int DefaultLimit = 8;
    public const int DefaultPerBucket = 30;
    public const int DefaultSeed = 20260926;
    private const int ThumbSize = 840; // shown at up to 420 px, so sharp on a 200% display

    public static async Task<int> RunAsync(CommandLine cl)
    {
        var folder = Path.GetFullPath(cl.Positional(0, "folder"));
        if (!Directory.Exists(folder))
            throw new UsageException($"Folder not found: {folder}");

        var output = OutputFolder.Prepare(folder, cl.RequiredOption("out"));
        var limits = new MatchThresholds(cl.Int("p", DefaultLimit), cl.Int("d", DefaultLimit));
        var perBucket = cl.Int("per-bucket", DefaultPerBucket);
        var seed = cl.Int("seed", DefaultSeed);
        // The tool's own cache, never the app's in %LocalAppData%. It must not sit in the photo folder either.
        var cachePath = Path.GetFullPath(cl.Option("cache") ?? Path.Combine(output, "scan-cache.json"));
        if (OutputFolder.IsSameOrInside(cachePath, folder))
            throw new UsageException("--cache must be outside the photo folder.");
        if (perBucket < 1)
            throw new UsageException("--per-bucket must be at least 1.");

        var clock = Stopwatch.StartNew();
        Console.WriteLine($"Scanning {folder} (cache: {cachePath}; online-only files are skipped, never downloaded)…");
        var scan = await new PhotoScanner(ScanCache.Load(cachePath)).ScanAsync(new ScanOptions { Folders = [folder] });
        var groups = DuplicateGrouper.Group(scan.Files, limits);
        Console.WriteLine($"{scan.Files.Count} files ({scan.CacheHits} from cache) in {clock.Elapsed.TotalSeconds:0}s; {groups.Count} groups at {limits.PHashRadius}/{limits.DHashLimit}.");

        var sample = PairSampler.Stratify(PairSampler.FromGroups(groups), perBucket, seed);
        foreach (var b in sample.Buckets)
            Console.WriteLine($"  bucket {b.Name}: {b.Population} pairs, {sample.Pairs.Count(p => p.Bucket == b)} sampled");
        if (sample.OutsideBuckets > 0)
            Console.WriteLine($"  {sample.OutsideBuckets} pairs are beyond the last bucket and not sampled.");

        var labels = new LabelFile
        {
            Folder = folder,
            Seed = seed,
            GroupedAtPHash = limits.PHashRadius,
            GroupedAtDHash = limits.DHashLimit,
            PairsPerBucket = perBucket,
            CreatedUtc = DateTime.UtcNow,
            Buckets = sample.Buckets,
            Pairs = sample.Pairs.Select((s, i) => new LabelledPair
            {
                Id = i + 1,
                Bucket = s.Bucket.Name,
                KeeperPath = s.Pair.Keeper.Path,
                MemberPath = s.Pair.Member.Path,
                KeeperSha256 = s.Pair.Keeper.Sha256,
                MemberSha256 = s.Pair.Member.Sha256,
                PHash = s.Pair.PHash,
                DHash = s.Pair.DHash,
            }).ToList(),
        };

        var thumbs = await WriteThumbnailsAsync(sample.Pairs.SelectMany(s => new[] { s.Pair.Keeper.Path, s.Pair.Member.Path }), Path.Combine(output, "review-thumbs"));
        var pagePath = Path.Combine(output, "review.html");
        await File.WriteAllTextAsync(pagePath, ReviewPage.Render(labels, sample.Pairs, thumbs));
        Console.WriteLine($"Wrote {pagePath} ({labels.Pairs.Count} pairs). Open it in a browser, label, then Export labels and run:");
        Console.WriteLine($"  dotnet run --project tools/PhotoSweep.Eval -- precision <labels.json>");
        return 0;
    }

    /// <summary>
    /// JPEG thumbnails via Core's <see cref="Thumbnail"/>, which re-checks for online-only files right before opening
    /// and returns a status instead of throwing, so a cloud or broken file becomes a placeholder, not a download.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, ThumbInfo>> WriteThumbnailsAsync(IEnumerable<string> paths, string folder)
    {
        Directory.CreateDirectory(folder);
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new ConcurrentDictionary<string, ThumbInfo>(StringComparer.OrdinalIgnoreCase);
        var encoder = new JpegEncoder { Quality = 85 };

        await Parallel.ForEachAsync(distinct.Select((path, i) => (path, i)), async (item, ct) =>
        {
            var thumb = await Thumbnail.LoadAsync(item.path, ThumbSize, ct);
            if (thumb is not { Status: ThumbnailStatus.Ok, Bgra: { } pixels })
            {
                result[item.path] = new ThumbInfo(null, thumb.Status switch
                {
                    ThumbnailStatus.OnlineOnly => "online-only, not downloaded",
                    ThumbnailStatus.CannotDecode => "can't decode",
                    _ => "file unavailable",
                });
                return;
            }

            var name = $"{item.i}.jpg";
            using (var image = Image.LoadPixelData<Bgra32>(pixels, thumb.Width, thumb.Height))
                await image.SaveAsJpegAsync(Path.Combine(folder, name), encoder, ct);
            result[item.path] = new ThumbInfo($"review-thumbs/{name}", null);
        });

        return result;
    }
}
