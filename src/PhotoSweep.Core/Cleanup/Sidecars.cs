namespace PhotoSweep.Core.Cleanup;

/// <summary>
/// Google Takeout exports each photo's metadata (album, description, location, people) as a JSON file next to it:
/// <c>IMG_1.jpg.json</c>, or in newer exports <c>IMG_1.jpg.supplemental-metadata.json</c>. Clean-up moves these
/// with their photo, so a photo put in the review folder doesn't leave an orphaned sidecar behind in the library.
/// </summary>
/// <remarks>
/// Only these exact names count, each belonging to exactly one photo, so a sidecar can never be claimed by two photos.
/// Takeout's other quirks (names truncated at 51 characters, "IMG_1(1).jpg.json" for "IMG_1.jpg(1)") are left alone:
/// guessing wrong would move metadata belonging to a photo that stays. Existence is checked from directory metadata
/// only, so an online-only sidecar is never downloaded.
/// </remarks>
public static class Sidecars
{
    /// <summary>What's appended to the photo's full file name, extension included.</summary>
    public static readonly IReadOnlyList<string> Suffixes = [".json", ".supplemental-metadata.json"];

    /// <summary>The sidecars of <paramref name="photoPath"/> that exist right now.</summary>
    public static IReadOnlyList<string> Find(string photoPath) =>
        Suffixes.Select(suffix => photoPath + suffix).Where(File.Exists).ToList();
}
