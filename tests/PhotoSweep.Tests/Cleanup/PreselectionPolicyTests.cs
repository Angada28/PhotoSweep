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
    [InlineData(MatchLevel.Exact, MatchKind.Identical)]
    [InlineData(MatchLevel.SamePhoto, MatchKind.Identical)]
    [InlineData(MatchLevel.SamePhoto, MatchKind.SamePhoto)]
    public void Every_other_copy_is_suggested_at_Exact_and_SamePhoto(MatchLevel level, MatchKind kind) =>
        Assert.True(PreselectionPolicy.IsSuggested(Member(kind), level));

    [Theory]
    [InlineData(MatchKind.Identical, true)]
    [InlineData(MatchKind.SamePhoto, false)]
    [InlineData(MatchKind.Similar, false)]
    public void At_Similar_only_byte_identical_copies_are_suggested(MatchKind kind, bool expected) =>
        Assert.Equal(expected, PreselectionPolicy.IsSuggested(Member(kind), MatchLevel.Similar));
}
