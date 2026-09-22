using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// CostPlausibility.ImplausibleRatio: a client-reported cost judged against the
// local pricing table's estimate for the same tokens.
public class CostPlausibilityTests
{
    private static ModelReportEntry Row(double cost, double? estimate) =>
        new("opencode", "m", "deepseek", 1, 0, 0, 0, 0, 1, 1, cost, CostEstimate: estimate);

    // Strictly above the threshold, the ratio is returned as the multiple the
    // warning names. At it, and below, the row is not flagged.
    [Theory]
    [InlineData(308.0, 1.0, 308.0)]   // the macOS report's order of magnitude
    [InlineData(50.01, 1.0, 50.01)]   // just over
    [InlineData(50.0, 1.0, null)]     // exactly at the threshold: not flagged
    [InlineData(0.3, 1.0, null)]      // a healthy row
    public void TheRatioIsReturnedOnlyAboveTheThreshold(double cost, double estimate, double? expected)
    {
        var ratio = CostPlausibility.ImplausibleRatio(Row(cost, estimate));
        if (expected is { } e)
        {
            Assert.NotNull(ratio);
            Assert.Equal(e, ratio!.Value, 6);
        }
        else
        {
            Assert.Null(ratio);
        }
    }

    // No usable estimate means the table could not price the tokens, which is
    // no evidence about the reported figure; and a zero or non-finite cost is
    // not a claim to check. None of these may produce a warning.
    [Theory]
    [InlineData(1000.0, null)]
    [InlineData(1000.0, 0.0)]
    [InlineData(1000.0, -1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void ARowThatCannotBeJudgedIsNotFlagged(double cost, double? estimate) =>
        Assert.Null(CostPlausibility.ImplausibleRatio(Row(cost, estimate)));

    [Fact]
    public void TheThresholdIsFifty() => Assert.Equal(50, CostPlausibility.Threshold);
}
