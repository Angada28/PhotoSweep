using System.IO;
using PhotoSweep.Presentation.Services;

namespace PhotoSweep.Desktop.Services;

/// <summary>
/// Pictures from the shell's known folder. OneDrive from the environment variables the OneDrive client sets when it's
/// signed in (<c>OneDrive</c>, or <c>OneDriveConsumer</c>/<c>OneDriveCommercial</c> for personal/work accounts).
/// Read once at startup; a folder that doesn't exist counts as not set up.
/// </summary>
public sealed class WindowsKnownFolders : IKnownFolders
{
    public string? Pictures { get; } = ExistingOrNull(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

    public string? OneDrive { get; } =
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(name => ExistingOrNull(Environment.GetEnvironmentVariable(name)))
            .FirstOrDefault(path => path is not null);

    private static string? ExistingOrNull(string? path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
}
