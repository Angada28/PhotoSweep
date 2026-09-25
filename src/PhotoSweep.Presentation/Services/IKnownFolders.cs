namespace PhotoSweep.Presentation.Services;

/// <summary>Well-known photo locations on this PC. Each is null when it doesn't exist (e.g. OneDrive isn't set up).</summary>
public interface IKnownFolders
{
    string? Pictures { get; }

    string? OneDrive { get; }
}
