using PhotoSweep.Presentation;

namespace PhotoSweep.Tests.Presentation;

public class FolderPathsTests
{
    [Theory]
    [InlineData(@"C:\Photos\", @"C:\Photos")]
    [InlineData(@"C:\Photos\Holiday\..", @"C:\Photos")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_gives_full_path_without_trailing_separator(string input, string expected) =>
        Assert.Equal(expected, FolderPaths.Normalize(input));

    [Theory]
    [InlineData(@"C:\Photos", @"C:\Photos", true)]
    [InlineData(@"c:\photos\", @"C:\Photos", true)]
    [InlineData(@"C:\Photos\2024\Summer", @"C:\Photos", true)]
    [InlineData(@"C:\Photos2", @"C:\Photos", false)]
    [InlineData(@"C:\Photo", @"C:\Photos", false)]
    [InlineData(@"C:\Photos", @"C:\Photos\2024", false)]
    [InlineData(@"C:\Photos", @"C:\", true)]
    [InlineData(@"D:\Photos", @"C:\", false)]
    public void IsSameOrInside(string path, string folder, bool expected) =>
        Assert.Equal(expected, FolderPaths.IsSameOrInside(path, folder));
}
