using System.Text;
using PhotoSweep.Eval;
using PhotoSweep.Eval.Precision;
using PhotoSweep.Eval.Synthetic;

// Measures how well the perceptual-hash thresholds separate "same photo" from "different photo". See README.md.
Console.OutputEncoding = Encoding.UTF8; // "≤", "–", "×" survive redirection to a file

const string Usage = """
    usage:
      synthetic <folder> --out <dir> [--count 300] [--seed 20260926]
          Recall and false positives over a pHash/dHash grid, from resized/recompressed/rotated copies of sampled photos.
      review <folder> --out <dir> [--p 8] [--d 8] [--per-bucket 30] [--seed 20260926] [--cache <file>]
          Groups the folder and writes review.html to label a stratified sample of keeper–member pairs.
      precision <labels.json> [--threshold 2|4|6|8] [--rule capture-time] [--out <dir>]
          Precision per distance bucket, and overall (population-weighted) per threshold, from exported labels.
          --rule capture-time: before/after the SamePhoto capture-time rule (reads the labelled photos' EXIF).
    The photo folder is only read. --out must be outside it.
    """;

try
{
    var cl = CommandLine.Parse(args);
    return cl.Command switch
    {
        "synthetic" => SyntheticCommand.Run(cl),
        "review" => await ReviewCommand.RunAsync(cl),
        "precision" => PrecisionCommand.Run(cl),
        _ => throw new UsageException($"Unknown command \"{cl.Command}\"."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
