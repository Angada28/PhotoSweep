namespace PhotoSweep.Core.Scanning;

/// <summary>What to scan and how. Immutable, so one scan's settings can't change while it runs.</summary>
public sealed record ScanOptions
{
    /// <summary>Folder that clean-up moves files into. Never scanned, or removed photos would show up again as duplicates.</summary>
    public const string ReviewFolderName = "_PhotoSweep Removed";

    /// <summary>
    /// Formats ImageSharp decodes, plus HEIC/HEIF. ImageSharp 3.1 can't decode HEIC, but those files are still
    /// SHA-256 hashed (so exact copies are found) and reported as unsupported rather than silently ignored.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif",
    };

    public required IReadOnlyList<string> Folders { get; init; }

    public bool Recursive { get; init; } = true;

    /// <summary>
    /// When false (the default), cloud files that aren't stored locally (e.g. OneDrive "online-only") are not opened,
    /// because opening one downloads it. They are reported as <see cref="ScanStatus.OnlineOnlySkipped"/> instead.
    /// Downloading must always be an explicit user choice.
    /// </summary>
    public bool IncludeOnlineOnlyFiles { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>How many found-but-not-yet-analysed files may queue up before the folder walk pauses.</summary>
    public int ChannelCapacity { get; init; } = 256;

    public IReadOnlySet<string> Extensions { get; init; } = DefaultExtensions;
}
