namespace TokenBar.Core.Tests;

// macOS truncates model names and account labels in the middle
// (.truncationMode(.middle), e.g. ModelsView.swift:123, AgentLimitsCard.swift:751).
// A character-count "fits" stands in for the WinUI width measure.
public class MiddleEllipsisTests
{
    private static Func<string, bool> AtMost(int length) => s => s.Length <= length;

    [Fact]
    public void TextThatFitsIsUnchanged() =>
        Assert.Equal("claude-sonnet-4-5", MiddleEllipsis.Fit("claude-sonnet-4-5", AtMost(17)));

    [Fact]
    public void LongModelIdKeepsItsHeadAndItsDistinguishingTail()
    {
        const string id = "anthropic.claude-sonnet-4-5-20250929-v1:0";
        var fitted = MiddleEllipsis.Fit(id, AtMost(25));
        Assert.Equal("anthropic.cl…0250929-v1:0", fitted);
        Assert.Equal(25, fitted.Length);

        // Two dated ids that end-trimming would render identically stay distinct.
        Assert.NotEqual(
            fitted, MiddleEllipsis.Fit("anthropic.claude-sonnet-4-6-20251120-v1:0", AtMost(25)));
    }

    [Fact]
    public void HeadTakesTheExtraElementWhenTheKeptCountIsOdd() =>
        Assert.Equal("abc…hi", MiddleEllipsis.Fit("abcdefghi", AtMost(6)));

    [Fact]
    public void ConfigDirectoryPathKeepsBothEnds() =>
        Assert.Equal(
            @"Claude · C:\…ents\team-b",
            MiddleEllipsis.Fit(@"Claude · C:\Users\nanako\work\clients\team-b", AtMost(24)));

    [Fact]
    public void NothingFitsLeavesOnlyTheEllipsis() =>
        Assert.Equal("…", MiddleEllipsis.Fit("abcdef", AtMost(0)));

    [Fact]
    public void ASurrogatePairIsNeverSplit() =>
        // "😀" is two UTF-16 units: the head keeps it whole rather than half.
        Assert.Equal("ab😀…gh", MiddleEllipsis.Fit("ab😀cdef😀gh", AtMost(7)));
}
