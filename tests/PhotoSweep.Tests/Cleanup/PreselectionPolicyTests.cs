using PhotoSweep.Core.Cleanup;
using PhotoSweep.Core.Grouping;
using PhotoSweep.Core.Scanning;

namespace PhotoSweep.Tests.Cleanup;

public class PreselectionPolicyTests
{
    private static GroupMember Member(MatchKind kind) =>
        new(new ScannedFile(@"C:\Photos\a.jpg", 1000, default), kind, null, null);

    [Theory]
    [InlineData(MatchLevel.Exact)]
    [InlineData(MatchLevel.SamePhoto)]
    [InlineData(MatchLevel.Similar)]
    public void The_keeper_is_never_suggested(MatchLevel level) =>
        Assert.False(PreselectionPolicy.IsSuggested(Member(MatchKind.Keeper), level));

    [Theory]
    [InlineData(MatchLevel.Exact)]
    [InlineData(MatchLevel.SamePhoto)]
    [InlineData(MatchLevel.Similar)]
    public void Byte_identical_copies_are_suggested_at_every_level(MatchLevel level) =>
        Assert.True(PreselectionPolicy.IsSuggested(Member(MatchKind.Identical), level));

    // Even 0–2-bit look-alikes were only ~60% the same picture in a hand-labelled sample (docs/decisions.md).
    [Theory]
    [InlineData(MatchLevel.SamePhoto, MatchKind.SamePhoto)]
    [InlineData(MatchLevel.Similar, MatchKind.SamePhoto)]
    [InlineData(MatchLevel.Similar, MatchKind.Similar)]
    public void Look_alikes_are_never_suggested(MatchLevel level, MatchKind kind) =>
        Assert.False(PreselectionPolicy.IsSuggested(Member(kind), level));

    [Fact]
    public void An_unknown_level_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PreselectionPolicy.IsSuggested(Member(MatchKind.Identical), (MatchLevel)99));
}
