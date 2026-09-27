using System.Text.RegularExpressions;
using PhotoSweep.Core.Imaging;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Core.Grouping;

/// <summary>What sort of group it is, so the results page can label it and filter by it.</summary>
public enum GroupKind
{
    /// <summary>Every member is byte-identical: removing all but one can't lose anything.</summary>
    Copies,

    /// <summary>Shots taken moments apart (different EXIF capture times within a few seconds, or burst-style names).</summary>
    Burst,

    /// <summary>Screenshots: screenshot-style names, or no camera make/model at a screen's resolution.</summary>
    Screenshots,

    /// <summary>Anything else that matched by appearance.</summary>
    LookAlikes,
}

/// <summary>
/// Labels a group from facts the scan already has (paths, SHA-256, EXIF capture time, camera data, dimensions), so it
/// reads no files and costs nothing next to grouping. The first rule that fits wins, in the order of <see cref="GroupKind"/>
/// except that screenshots are checked before bursts: a screenshot has no capture time, so only its name could make
/// it look like a burst.
/// </summary>
/// <remarks>
/// Pure and based on the files alone, not on <see cref="GroupMember.Kind"/>: the members' kinds describe the scan-time
/// keeper, which may since have been moved, and the page re-labels groups after every move and undo.
/// </remarks>
public static partial class GroupKindClassifier
{
    /// <summary>Frames of one burst are taken within a few seconds; a retake a minute later is just a look-alike.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);

    // Exact desktop and laptop screen sizes (landscape). Checked in both orientations.
    private static readonly HashSet<(int, int)> DesktopScreens =
    [
        (1280, 720), (1280, 800), (1280, 1024), (1366, 768), (1440, 900), (1536, 864), (1600, 900), (1680, 1050),
        (1920, 1080), (1920, 1200), (2560, 1080), (2560, 1440), (2560, 1600), (2880, 1800), (3440, 1440), (3840, 2160),
    ];

    // Phone screen widths (the short side). A phone screenshot is also at least 16:9, which camera photos rarely are.
    private static readonly HashSet<int> PhoneShortSides = [720, 750, 828, 1080, 1125, 1170, 1179, 1242, 1284, 1290, 1440];

    public static GroupKind Classify(PhotoGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Classify(group.Members.Select(m => m.File).ToList());
    }

    /// <param name="files">The group's files, keeper first.</param>
    public static GroupKind Classify(IReadOnlyList<ScannedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A group has at least one file.", nameof(files));

        if (files[0].Sha256 is { } sha && files.All(f => f.Sha256 == sha))
            return GroupKind.Copies;
        if (IsScreenshotGroup(files))
            return GroupKind.Screenshots;
        if (IsBurst(files))
            return GroupKind.Burst;
        return GroupKind.LookAlikes;
    }

    /// <summary>
    /// No member names a camera, and the keeper (the best-resolution copy, i.e. the original screen-sized one) has a
    /// screenshot name or a screen's size. A camera make or model on any member means a camera was involved.
    /// </summary>
    /// <remarks>
    /// Only make and model count, not the capture time (unlike <see cref="ImageDetails.HasCameraData"/>): Takeout fix-up
    /// tools write Google's timestamp into every file's EXIF, screenshots included (seen on sweep-test).
    /// </remarks>
    private static bool IsScreenshotGroup(IReadOnlyList<ScannedFile> files) =>
        !files.Any(f => f.Details is { CameraMake: not null } or { CameraModel: not null })
        && (LooksLikeScreenshotName(files[0].Path) || IsScreenSize(files[0].Details));

    public static bool LooksLikeScreenshotName(string path) => ScreenshotName().IsMatch(Path.GetFileNameWithoutExtension(path));

    /// <summary>A known desktop resolution, or a phone's: a known screen width and 16:9 or taller.</summary>
    public static bool IsScreenSize(ImageDetails? details)
    {
        if (details is not { Width: > 0, Height: > 0 })
            return false;

        var (longSide, shortSide) = (Math.Max(details.Width, details.Height), Math.Min(details.Width, details.Height));
        return DesktopScreens.Contains((longSide, shortSide))
            || (PhoneShortSides.Contains(shortSide) && longSide * 9 >= shortSide * 16);
    }

    private static bool IsBurst(IReadOnlyList<ScannedFile> files)
    {
        var times = files.Select(f => f.Details?.DateTaken).OfType<DateTime>().Distinct().ToList();
        if (times.Count >= 2 && times.Max() - times.Min() <= BurstWindow)
            return true;

        if (files.Any(f => BurstName().IsMatch(Path.GetFileNameWithoutExtension(f.Path))))
            return true;

        // "20160604_162017" and "20160604_162017_001": the same stem with a numbered suffix on at least one of them.
        return files
            .Select(f => SplitSequence(Path.GetFileNameWithoutExtension(f.Path)))
            .GroupBy(x => x.Stem, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() >= 2 && g.Any(x => x.Numbered));
    }

    private static (string Stem, bool Numbered) SplitSequence(string name) =>
        SequenceSuffix().Match(name) is { Success: true } m ? (m.Groups["stem"].Value, true) : (name, false);

    // "Screenshot_20171106-152712", "Screen Shot 2017-11-06 at 12.41.31 PM", "Screen_Recording", "Capture d'écran",
    // "Bildschirmfoto". "screenshot" may be joined to other words ("MyScreenshot"), so no word boundaries.
    [GeneratedRegex(@"screen[\s_-]?shot|screen[\s_-]?recording|screencap|capture d.écran|bildschirmfoto",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotName();

    // Google camera ("00000IMG_00000_BURST20180912134826_COVER"), Samsung ("…_Burst01").
    [GeneratedRegex("burst", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BurstName();

    [GeneratedRegex(@"^(?<stem>.+)_\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex SequenceSuffix();
}
