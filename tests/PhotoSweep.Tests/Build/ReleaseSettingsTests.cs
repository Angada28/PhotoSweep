using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhotoSweep.Core.Scanning;
using SixLabors.ImageSharp;

namespace PhotoSweep.Tests.Build;

// Guards the release settings the workflows rely on: one version number that reaches the assemblies, and an app icon
// that Windows can use. Reads the repo's files as text/bytes, so it doesn't need to reference the Desktop project.
public partial class ReleaseSettingsTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Version_is_major_minor_patch()
    {
        Assert.Matches(SemVer(), ReadVersion());
    }

    [Fact]
    public void Version_reaches_the_built_assemblies()
    {
        var expected = System.Version.Parse(ReadVersion());

        var actual = typeof(ScanOptions).Assembly.GetName().Version!;

        Assert.Equal((expected.Major, expected.Minor, expected.Build), (actual.Major, actual.Minor, actual.Build));
    }

    [Fact]
    public void Desktop_project_uses_the_icon_as_application_icon_and_window_resource()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "src", "PhotoSweep.Desktop", "PhotoSweep.Desktop.csproj"));

        Assert.Equal(@"Assets\PhotoSweep.ico", project.Descendants("ApplicationIcon").Single().Value);
        Assert.Contains(project.Descendants("Resource"), r => (string?)r.Attribute("Include") == @"Assets\PhotoSweep.ico");
    }

    [Fact]
    public void Icon_has_the_sizes_Windows_asks_for_and_every_entry_is_readable()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "src", "PhotoSweep.Desktop", "Assets", "PhotoSweep.ico"));

        // ICONDIR: reserved 0, type 1 (icon), then the entry count.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(6 + 16 * i, 16);
            var size = entry[0] == 0 ? 256 : entry[0]; // a width byte of 0 means 256
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            Assert.Equal(entry[0], entry[1]); // square
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..])); // 32-bit colour with alpha
            Assert.InRange(offset + (long)length, 0, bytes.Length);

            var image = bytes.AsSpan(offset, length);
            if (size == 256)
            {
                // Stored as a PNG; ImageSharp must decode it at full size.
                using var png = Image.Load(image);
                Assert.Equal((256, 256), (png.Width, png.Height));
            }
            else
            {
                // Stored as a BITMAPINFOHEADER bitmap: height is doubled (colour rows + mask rows).
                Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(image));
                Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(image[4..]));
                Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(image[8..]));
            }

            sizes.Add(size);
        }

        Assert.Superset(new HashSet<int> { 16, 24, 32, 48, 256 }, sizes.ToHashSet());
    }

    private static string ReadVersion()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        return props.Descendants("Version").Single().Value;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PhotoSweep.sln")) && File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Couldn't find the repository root above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();
}
