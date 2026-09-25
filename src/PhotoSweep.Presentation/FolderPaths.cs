namespace PhotoSweep.Presentation;

/// <summary>Path comparisons for the folder list. Windows paths are case-insensitive.</summary>
public static class FolderPaths
{
    /// <summary>Full path without a trailing separator, except for a drive root (<c>C:\</c> stays <c>C:\</c>).</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// True if <paramref name="path"/> is <paramref name="folder"/> or somewhere below it. Both are compared with a
    /// trailing separator, so <c>C:\Photos2</c> is not inside <c>C:\Photos</c>.
    /// </summary>
    public static bool IsSameOrInside(string path, string folder) =>
        WithSeparator(Normalize(path)).StartsWith(WithSeparator(Normalize(folder)), StringComparison.OrdinalIgnoreCase);

    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
