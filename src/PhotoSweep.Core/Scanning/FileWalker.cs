using System.IO.Enumeration;

namespace PhotoSweep.Core.Scanning;

/// <summary>A file found by <see cref="FileWalker"/>, with the metadata the directory listing already provided.</summary>
public readonly record struct FileCandidate(string Path, long Size, DateTime LastWriteUtc, FileAttributes Attributes)
{
    public static FileCandidate FromPath(string path)
    {
        var info = new FileInfo(path);
        return new FileCandidate(info.FullName, info.Length, info.LastWriteTimeUtc, info.Attributes);
    }
}

/// <summary>Lists the photo files under a folder, skipping hidden/system files, the review folder and folder links.</summary>
public static class FileWalker
{
    /// <remarks>
    /// Uses <see cref="FileSystemEnumerable{TResult}"/>, the lower-level API behind Directory.EnumerateFiles, because:
    /// <list type="bullet">
    /// <item><c>ShouldRecursePredicate</c> stops us descending into the review folder at all, instead of walking it and
    /// filtering afterwards.</item>
    /// <item>Each <see cref="FileSystemEntry"/> already carries size, last-write time and attributes from the directory
    /// listing, so the cache check and the online-only check need no extra per-file disk call (and never open the file).</item>
    /// </list>
    /// </remarks>
    public static IEnumerable<FileCandidate> Enumerate(string root, ScanOptions options)
    {
        var extensions = new HashSet<string>(options.Extensions, StringComparer.OrdinalIgnoreCase);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = options.Recursive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, // applies to folders too
            IgnoreInaccessible = true,
        };

        return new FileSystemEnumerable<FileCandidate>(
            root,
            (ref FileSystemEntry e) => new FileCandidate(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes),
            enumerationOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && extensions.Contains(Path.GetExtension(e.FileName).ToString()),
            ShouldRecursePredicate = (ref FileSystemEntry e) => ShouldEnter(ref e),
        };
    }

    private static bool ShouldEnter(ref FileSystemEntry dir)
    {
        if (dir.FileName.Equals(ScanOptions.ReviewFolderName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Symlinks and junctions can point back up the tree (infinite loop) or at folders scanned anyway. But OneDrive
        // folders are reparse points too, and must be entered. Only real links have a LinkTarget, so check that;
        // the extra call only happens for the rare reparse-point folder.
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            return new DirectoryInfo(dir.ToFullPath()).LinkTarget is null;

        return true;
    }
}
