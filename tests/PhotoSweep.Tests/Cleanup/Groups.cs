using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Tests.Cleanup;

internal static class Groups
{
    /// <summary>A group with the first file as keeper. Clean-up only cares about membership, not how members matched.</summary>
    public static PhotoGroup Of(params ScannedFile[] files) =>
        new(files.Select((f, i) => new GroupMember(f, i == 0 ? MatchKind.Keeper : MatchKind.Identical, null, null)).ToList(), "test");

    /// <summary>"Scans" a real file: records its size and last-write time as they are now, like the scanner would.</summary>
    public static ScannedFile Scanned(string path)
    {
        var info = new FileInfo(path);
        return new ScannedFile(info.FullName, info.Length, info.LastWriteTimeUtc) { Status = ScanStatus.Ok };
    }
}

/// <summary>A clock stuck at one moment, in UTC, so batch folder names are predictable.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
