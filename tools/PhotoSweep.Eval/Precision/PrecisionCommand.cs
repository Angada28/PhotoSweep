namespace PhotoSweep.Eval.Precision;

/// <summary>
/// <c>precision &lt;labels.json&gt; [--threshold T] [--rule capture-time] [--out dir]</c>: prints (and optionally writes) the
/// precision tables. With <c>--rule</c>, prints them before and after the rule (reading the labelled photos' EXIF).
/// </summary>
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

        string markdown;
        switch (cl.Option("rule"))
        {
            case null:
                markdown = PrecisionCalculator.ToMarkdown(file, thresholds);
                break;
            case CaptureTimeRule.Name:
                // Reads only the labelled photos' headers (about two per pair), never online-only ones.
                var times = CaptureTimeRule.ReadTimes(file.Pairs.SelectMany(p => new[] { p.KeeperPath, p.MemberPath }), out var unreadable);
                markdown = CaptureTimeRule.ToMarkdown(file, CaptureTimeRule.Apply(file, times, unreadable), thresholds);
                break;
            case var other:
                throw new UsageException($"Unknown --rule \"{other}\"; the only rule is \"{CaptureTimeRule.Name}\".");
        }

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
