using CommunityToolkit.Mvvm.Input;
using PhotoSweep.Core.Grouping;

namespace PhotoSweep.Presentation;

/// <summary>
/// Placeholder for the results page, built properly in Phase 9. Shows totals only; it already holds the whole
/// <see cref="ScanOutcome"/> so the real page can be built on it.
/// </summary>
public sealed partial class ResultsViewModel(ScanOutcome outcome, Action goBack)
{
    public ScanOutcome Outcome { get; } = outcome;

    public int GroupCount => Outcome.Groups.Count;

    /// <summary>Every member except each group's keeper.</summary>
    public int ExtraCopies => Outcome.Groups.Sum(g => g.Members.Count - 1);

    public long ExtraBytes => Outcome.Groups.Sum(g => g.Members.Skip(1).Sum(m => m.File.SizeBytes));

    public string Summary => GroupCount == 0
        ? "No duplicates found."
        : $"{DisplayText.Count(GroupCount, "group", "groups")} with {DisplayText.Count(ExtraCopies, "extra copy", "extra copies")}";

    /// <summary>
    /// At Similar the groups include near-identical shots the user may want to keep (e.g. burst frames), so the
    /// space is a ceiling, not a promise.
    /// </summary>
    public string SpaceText => GroupCount == 0 ? ""
        : Outcome.Request.Level == MatchLevel.Similar ? $"Up to {DisplayText.Bytes(ExtraBytes)} in look-alike copies"
        : $"{DisplayText.Bytes(ExtraBytes)} in extra copies";

    public IReadOnlyList<string> UnreadableFolders => Outcome.UnreadableFolders;

    public bool HasUnreadableFolders => UnreadableFolders.Count > 0;

    [RelayCommand]
    private void Back() => goBack();
}
