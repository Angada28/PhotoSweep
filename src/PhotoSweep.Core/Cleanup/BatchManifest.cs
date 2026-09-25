using System.Text.Json;

namespace PhotoSweep.Core.Cleanup;

/// <param name="RelativePath">Relative to the scanned root; the file lives at the same relative path inside the batch folder.</param>
/// <param name="SizeBytes">Size and last-write time let undo tell "never moved" (the original is still there, unchanged) from "gone".</param>
internal sealed record ManifestEntry(string RelativePath, long SizeBytes, DateTime LastWriteUtc);

/// <summary>
/// The record of one batch in one scanned root, stored at <c>&lt;root&gt;/_PhotoSweep Removed/&lt;batch&gt;/manifest.json</c>.
/// Paths are relative, and the root is worked out from where the manifest sits, so a batch still undoes correctly if
/// the drive letter changes (e.g. an external disk).
/// </summary>
/// <remarks>
/// Entries are written <i>before</i> each move (write-ahead). A crash can then only leave an entry whose file was never
/// moved, which undo detects and drops; it can never leave a moved file that nothing records.
/// </remarks>
internal sealed class BatchManifest
{
    public const string FileName = "manifest.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BatchManifest(string manifestPath, Guid id, DateTime createdUtc, List<ManifestEntry> entries)
    {
        ManifestPath = manifestPath;
        Id = id;
        CreatedUtc = createdUtc;
        Entries = entries;
    }

    public string ManifestPath { get; }

    public string BatchDirectory => Path.GetDirectoryName(ManifestPath)!;

    /// <summary>The <c>_PhotoSweep Removed</c> folder.</summary>
    public string ReviewDirectory => Path.GetDirectoryName(BatchDirectory)!;

    public string Root => Path.GetDirectoryName(ReviewDirectory)!;

    public Guid Id { get; }

    public DateTime CreatedUtc { get; }

    public List<ManifestEntry> Entries { get; }

    public string ReviewPathOf(ManifestEntry entry) => Path.Combine(BatchDirectory, entry.RelativePath);

    public string OriginalPathOf(ManifestEntry entry) => Path.Combine(Root, entry.RelativePath);

    /// <summary>Null if the manifest is missing, unreadable, corrupt or from another format version.</summary>
    public static BatchManifest? TryLoad(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var file = JsonSerializer.Deserialize<ManifestFile>(stream, JsonOptions);
            if (file is { Version: FormatVersion, Files: not null })
                return new BatchManifest(Path.GetFullPath(manifestPath), file.Id, file.CreatedUtc, file.Files);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Rewrites the whole manifest: written to a temp file and swapped in, so a crash mid-write leaves the previous
    /// version intact rather than a half-written file. Throws on I/O failure; callers decide what that means.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(BatchDirectory);
        var tempPath = ManifestPath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            var file = new ManifestFile { Version = FormatVersion, Id = Id, CreatedUtc = CreatedUtc, Files = Entries };
            JsonSerializer.Serialize(stream, file, JsonOptions);
        }

        File.Move(tempPath, ManifestPath, overwrite: true);
    }

    public bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class ManifestFile
    {
        public int Version { get; init; }
        public Guid Id { get; init; }
        public DateTime CreatedUtc { get; init; }
        public List<ManifestEntry>? Files { get; init; }
    }
}
