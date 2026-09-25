using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Tests.Scanning;

// Real OneDrive placeholders can't be created in a test, so the attribute check is tested directly.
// The scanner tests then use the Offline attribute, the one flag NTFS lets us set on an ordinary file.
public class CloudFileAttributesTests
{
    private const FileAttributes Pinned = (FileAttributes)0x80000;   // "Always keep on this device"
    private const FileAttributes Unpinned = (FileAttributes)0x100000;

    public static TheoryData<FileAttributes> OnlineOnly() =>
    [
        FileAttributes.Offline,
        CloudFileAttributes.RecallOnOpen,
        CloudFileAttributes.RecallOnDataAccess,
        // What an online-only OneDrive file typically looks like:
        FileAttributes.Archive | FileAttributes.ReparsePoint | Unpinned | CloudFileAttributes.RecallOnDataAccess,
    ];

    public static TheoryData<FileAttributes> Local() =>
    [
        FileAttributes.Normal,
        FileAttributes.Archive | FileAttributes.ReadOnly,
        // Downloaded OneDrive files are still reparse points, just without the recall flags:
        FileAttributes.Archive | FileAttributes.ReparsePoint,
        FileAttributes.Archive | FileAttributes.ReparsePoint | Pinned,
    ];

    [Theory]
    [MemberData(nameof(OnlineOnly))]
    public void Recall_or_offline_flags_mean_online_only(FileAttributes attributes) =>
        Assert.True(CloudFileAttributes.IsOnlineOnly(attributes));

    [Theory]
    [MemberData(nameof(Local))]
    public void Files_without_those_flags_are_local(FileAttributes attributes) =>
        Assert.False(CloudFileAttributes.IsOnlineOnly(attributes));

    [Fact]
    public void Flag_values_match_the_Win32_constants()
    {
        Assert.Equal(0x40000, (int)CloudFileAttributes.RecallOnOpen);
        Assert.Equal(0x400000, (int)CloudFileAttributes.RecallOnDataAccess);
        Assert.Equal(0x1000, (int)FileAttributes.Offline);
    }

    [Fact]
    public void Check_reads_the_file_attributes_as_they_are_now()
    {
        using var folder = new TempPhotoFolder();
        var path = folder.AddPhoto("coffee.jpg");
        Assert.Equal(FileAvailability.Local, CloudFileAttributes.Check(path));

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline); // like "Free up space"
        try
        {
            Assert.Equal(FileAvailability.OnlineOnly, CloudFileAttributes.Check(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Check_reports_a_missing_file_as_unavailable()
    {
        using var folder = new TempPhotoFolder();

        Assert.Equal(FileAvailability.Unavailable, CloudFileAttributes.Check(Path.Combine(folder.Root, "gone.jpg")));
        Assert.Equal(FileAvailability.Unavailable, CloudFileAttributes.Check(Path.Combine(folder.Root, "no-such-dir", "gone.jpg")));
    }
}
