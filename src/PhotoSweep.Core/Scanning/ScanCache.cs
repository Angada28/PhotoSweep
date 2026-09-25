using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoSweep.Core.Hashing;

namespace PhotoSweep.Core.Scanning;

/// <summary>
/// Remembers scan results in a JSON file, keyed by full path. An entry is only reused when the file's size and
/// last-write time still match, so edited files are re-read and unchanged ones are skipped.
/// </summary>
public sealed class ScanCache
{
    /// <summary>Bump when the entry format or the hash algorithm changes; older cache files are then ignored.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _filePath;

    // Parallel workers read and write this at the same time. Paths are case-insensitive on Windows.
    private readonly ConcurrentDictionary<string, CacheEntry> _entries;

    private ScanCache(string filePath, IEnumerable<KeyValuePair<string, CacheEntry>> entries)
    {
        _filePath = filePath;
        _entries = new ConcurrentDictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoSweep", "scan-cache.json");

    public int Count => _entries.Count;

    /// <summary>Loads the cache, or starts empty if the file is missing, unreadable, corrupt or from another version.</summary>
    public static ScanCache Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                using var stream = File.OpenRead(filePath);
                var file = JsonSerializer.Deserialize<CacheFile>(stream, JsonOptions);
                if (file is { Version: FormatVersion, Entries: not null })
                    return new ScanCache(filePath, file.Entries);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A cache is only an optimisation: losing it costs time, never correctness.
        }

        return new ScanCache(filePath, []);
    }

    public bool TryGet(FileCandidate file, out ScannedFile result)
    {
        if (_entries.TryGetValue(file.Path, out var e) && e.Size == file.Size && e.LastWriteUtcTicks == file.LastWriteUtc.Ticks)
        {
            result = ScannedFile.From(file) with
            {
                Status = e.Status,
                Sha256 = e.Sha256,
                Fingerprint = e is { PHash: { } p, DHash: { } d } ? new ImageFingerprint(p, d) : null,
                Error = e.Error,
            };
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Stores results that depend only on the file's bytes: <see cref="ScanStatus.Ok"/> and
    /// <see cref="ScanStatus.DecodeFailed"/>. Unreadable (e.g. locked) and unexpected errors may be temporary,
    /// so they are retried next scan; online-only files were never read.
    /// </summary>
    public void Put(ScannedFile file)
    {
        if (file.Status is not (ScanStatus.Ok or ScanStatus.DecodeFailed))
            return;

        _entries[file.Path] = new CacheEntry
        {
            Size = file.SizeBytes,
            LastWriteUtcTicks = file.LastWriteUtc.Ticks,
            Status = file.Status,
            Sha256 = file.Sha256,
            PHash = file.Fingerprint?.PHash,
            DHash = file.Fingerprint?.DHash,
            Error = file.Error,
        };
    }

    /// <summary>
    /// Drops entries for files that were inside the scanned folders but weren't found this time (deleted, moved,
    /// now hidden). Entries for other folders are kept. Only call after a scan that ran to completion.
    /// </summary>
    public void Prune(IReadOnlyList<string> roots, bool recursive, IReadOnlySet<string> seen)
    {
        foreach (var path in _entries.Keys)
        {
            if (!seen.Contains(path) && roots.Any(root => IsInside(path, root, recursive)))
                _entries.TryRemove(path, out _);
        }
    }

    /// <summary>
    /// Writes to a temp file and then swaps it in, so a crash mid-save can't leave a half-written cache.
    /// Returns false instead of throwing on I/O failure: failing to save a cache shouldn't fail a scan.
    /// </summary>
    public bool TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tempPath = _filePath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                var file = new CacheFile { Version = FormatVersion, Entries = new Dictionary<string, CacheEntry>(_entries) };
                JsonSerializer.Serialize(stream, file, JsonOptions);
            }

            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInside(string path, string root, bool recursive)
    {
        if (!recursive)
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CacheFile
    {
        public int Version { get; init; }
        public Dictionary<string, CacheEntry>? Entries { get; init; }
    }

    private sealed class CacheEntry
    {
        public long Size { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public ScanStatus Status { get; init; }
        public string? Sha256 { get; init; }
        public ulong? PHash { get; init; }
        public ulong? DHash { get; init; }
        public string? Error { get; init; }
    }
}
