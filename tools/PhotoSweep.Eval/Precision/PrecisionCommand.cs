namespace PhotoSweep.Eval.Precision;

/// <summary><c>precision &lt;labels.json&gt; [--threshold T] [--out dir]</c>: prints (and optionally writes) the precision tables.</summary>
public static class PrecisionCommand
{
    public static int Run(CommandLine cl)
    {
        var path = Path.GetFullPath(cl.Positional(0, "labels.json"));
        if (!File.Exists(path))
            throw new UsageException($"File not found: {path}");

        var file = LabelFile.Load(path);
        var thresholds = cl.Option("threshold") is null
            ? file.Buckets.Select(b => b.Max).ToList() // every bucket edge
            : [cl.Int("threshold", 0)];
        if (thresholds.Any(t => file.Buckets.All(b => b.Max != t)))
            throw new UsageException($"--threshold must be one of {string.Join(", ", file.Buckets.Select(b => b.Max))}.");

        var markdown = PrecisionCalculator.ToMarkdown(file, thresholds);
        Console.WriteLine(markdown);

        if (cl.Option("out") is { } outDir)
        {
            // Labels hold only paths, so there's no photo folder to protect here, but still never write into it.
            var output = OutputFolder.Prepare(file.Folder, outDir);
            var reportPath = Path.Combine(output, "precision.md");
            File.WriteAllText(reportPath, markdown);
            Console.WriteLine($"Wrote {reportPath}");
        }

        return 0;
    }
}
