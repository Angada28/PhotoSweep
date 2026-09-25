using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation;

/// <summary>One choice in the start screen's strictness list.</summary>
public sealed record StrictnessOption(MatchLevel Level, string Title, string Description)
{
    public static IReadOnlyList<StrictnessOption> All { get; } =
    [
        new(MatchLevel.Exact, "Exact copies", "Only files that are byte-for-byte identical."),
        new(MatchLevel.SamePhoto, "Same photo (recommended)", "Also the same photo resized, recompressed, converted or rotated."),
        new(MatchLevel.Similar, "Similar shots", "Also near-identical shots and heavier edits. Check these groups carefully."),
    ];

    public static StrictnessOption For(MatchLevel level) => All.Single(s => s.Level == level);
}
