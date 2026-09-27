using System.Collections.Concurrent;
using System.Diagnostics;
using PhotoSweep.Core.Hashing;
using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;

namespace PhotoSweep.Eval.Synthetic;

/// <summary>
/// <c>synthetic &lt;folder&gt; --out &lt;dir&gt; [--count 300] [--seed 20260926]</c>: samples originals, writes their variants
/// to &lt;out&gt;/variants, hashes everything the way the app does, and writes &lt;out&gt;/synthetic.md.
/// </summary>
public static class SyntheticCommand
{
    public const int DefaultCount = 300;
    public const int DefaultSeed = 20260926;

    private sealed record Sample(int DrawIndex, string Path, ImageFingerprint Fingerprint, IReadOnlyList<(VariantKind Kind, ImageFingerprint Fingerprint)> Variants);

    public static int Run(CommandLine cl)
    {
        var folder = Path.GetFullPath(cl.Positional(0, "folder"));
        if (!Directory.Exists(folder))
            throw new UsageException($"Folder not found: {folder}");

        var output = OutputFolder.Prepare(folder, cl.RequiredOption("out"));
        var count = cl.Int("count", DefaultCount);
        var seed = cl.Int("seed", DefaultSeed);
        if (count < 2)
            throw new UsageException("--count must be at least 2.");

        var clock = Stopwatch.StartNew();
        var order = PhotoSampler.Order(PhotoSampler.List(folder), seed);
        Console.WriteLine($"{order.Files.Count} local candidates ({order.OnlineOnlySkipped} online-only skipped). Sampling {count}, seed {seed}.");

        var variantFolder = Path.Combine(output, "variants");
        var samples = new ConcurrentBag<Sample>();
        var failed = new ConcurrentBag<string>();
        var next = 0;

        // Take files in draw order until `count` have decoded. Each round tries exactly as many as are still missing,
        // so which files end up in the sample depends only on the order and on which files decode: deterministic.
        while (samples.Count < count && next < order.Files.Count)
        {
            var batch = order.Files.Skip(next).Take(count - samples.Count).Select((f, i) => (File: f, DrawIndex: next + i)).ToList();
            next += batch.Count;
            Parallel.ForEach(batch, item =>
            {
                try
                {
                    samples.Add(Measure(item.File.Path, item.DrawIndex, variantFolder));
                    if (samples.Count % 25 == 0)
                        Console.WriteLine($"  {samples.Count}/{count} originals done ({clock.Elapsed.TotalSeconds:0}s)");
                }
                catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{item.File.Path}: {ex.Message}");
                }
            });
        }

        var ordered = samples.OrderBy(s => s.DrawIndex).ToList();
        if (ordered.Count < 2)
            throw new UsageException($"Only {ordered.Count} photos decoded; need at least 2.");

        var grid = BuildGrid(ordered);
        var run = new SyntheticRun(folder, seed, ordered.Select(s => s.Path).ToList(), order.OnlineOnlySkipped, failed.Count, grid);
        var markdown = SyntheticReport.ToMarkdown(run);
        var reportPath = Path.Combine(output, "synthetic.md");
        File.WriteAllText(reportPath, markdown);
        if (!failed.IsEmpty)
            File.WriteAllLines(Path.Combine(output, "synthetic-skipped.txt"), failed.Order());

        Console.WriteLine($"Done in {clock.Elapsed.TotalSeconds:0}s: {ordered.Count} originals, {failed.Count} failed to decode. Report: {reportPath}");
        return 0;
    }

    private static Sample Measure(string path, int drawIndex, string variantFolder)
    {
        // Re-checked right before the first open: the listing the order came from may be stale (CLAUDE.md rule 6).
        if (CloudFileAttributes.Check(path) != FileAvailability.Local)
            throw new IOException("not stored locally (online-only or gone), so not opened");

        var original = Fingerprinter.Compute(path); // the file as the user has it, exactly as the scanner hashes it
        var variants = VariantGenerator.Generate(path, variantFolder, drawIndex.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
        return new Sample(drawIndex, path, original, variants.Select(v => (v.Kind, Fingerprinter.Compute(v.Path))).ToList());
    }

    private static ThresholdGrid BuildGrid(IReadOnlyList<Sample> samples)
    {
        var variants = new List<VariantDistance>();
        for (var i = 0; i < samples.Count; i++)
        {
            foreach (var (kind, fp) in samples[i].Variants)
            {
                var (p, d) = Distance(samples[i].Fingerprint, fp);
                variants.Add(new VariantDistance(i, kind, p, d));
            }
        }

        var unrelated = new List<PairDistance>(samples.Count * (samples.Count - 1) / 2);
        for (var a = 0; a < samples.Count; a++)
        {
            for (var b = a + 1; b < samples.Count; b++)
            {
                var (p, d) = Distance(samples[a].Fingerprint, samples[b].Fingerprint);
                unrelated.Add(new PairDistance(a, b, p, d));
            }
        }

        return new ThresholdGrid(variants, unrelated);
    }

    private static (int P, int D) Distance(ImageFingerprint a, ImageFingerprint b) =>
        (Hamming.Distance(a.PHash, b.PHash), Hamming.Distance(a.DHash, b.DHash));
}
