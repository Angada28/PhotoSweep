namespace PhotoSweep.Eval;

/// <summary>Where the tool writes. Guards the rule that it never writes into the photo folder it measures.</summary>
public static class OutputFolder
{
    /// <summary>
    /// Returns the full output path, created if needed. Refuses an output folder that is the source folder or inside
    /// it, so generated variants and thumbnails can never land among the user's photos (and be scanned next time).
    /// </summary>
    public static string Prepare(string sourceFolder, string outputFolder)
    {
        var source = Path.GetFullPath(sourceFolder);
        var output = Path.GetFullPath(outputFolder);
        if (IsSameOrInside(output, source))
            throw new UsageException($"--out must be outside the photo folder: {output} is inside {source}.");

        Directory.CreateDirectory(output);
        return output;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        // GetRelativePath compares case-insensitively on Windows and handles "C:\a" vs "C:\ab" correctly,
        // which a plain StartsWith would not.
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        var outside = relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative); // another drive
        return !outside;
    }
}
