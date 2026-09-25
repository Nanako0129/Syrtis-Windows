using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The Agent-limits card's Chart layout: layout parsing and the sparkline gate
// (port of AgentLimitsCard.swift's LimitsLayout and sparklineInterval).
public class LimitsChartFoldTests
{
    private const long Start = 0;
    private const long Hour = 3600 * 1000;
    private const long End = 5 * Hour;

    [Theory]
    [InlineData("full", LimitsLayout.Full)]
    [InlineData("classic", LimitsLayout.Classic)]
    [InlineData("chart", LimitsLayout.Chart)]
    [InlineData(null, LimitsLayout.Full)]
    [InlineData("", LimitsLayout.Full)]
    [InlineData("bogus", LimitsLayout.Full)]
    public void ParseLayoutFallsBackToFullForAnythingUnrecognised(string? raw, LimitsLayout expected)
    {
        Assert.Equal(expected, LimitsChartFold.ParseLayout(raw));
    }

    [Fact]
    public void TwoSamplesInsideTheWindowProduceAnInterval()
    {
        var interval = LimitsChartFold.SparklineInterval(
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: 2 * Hour,
            samples: [new QuotaSample(Hour, 30), new QuotaSample(2 * Hour, 40)]);

        Assert.Equal((Start, End), interval);
    }

    [Fact]
    public void OneSampleInsideTheWindowFallsBackToTheBar()
    {
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: 2 * Hour,
            samples: [new QuotaSample(Hour, 30)]));
    }

    // A series whose points all predate this window (a prior cycle's
    // readings, still on file) must not draw a flat line at whatever the last
    // one said — samples are counted only when they fall INSIDE
    // [windowStartMs, nowMs].
    [Fact]
    public void SamplesFromBeforeTheWindowStartDoNotCount()
    {
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: 2 * Hour,
            windowEndMs: End,
            nowMs: 3 * Hour,
            samples: [new QuotaSample(Hour, 90), new QuotaSample(2 * Hour - 1, 95)]));
    }

    [Fact]
    public void SamplesAfterNowDoNotCount()
    {
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: Hour,
            samples: [new QuotaSample(Hour, 20), new QuotaSample(3 * Hour, 40)]));
    }

    // A reset that has already passed: the curve is drawn only up to the
    // window end, so a sample after it must not make up the two points.
    [Fact]
    public void SamplesAfterAPassedWindowEndDoNotCount()
    {
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: Start,
            windowEndMs: End,
            nowMs: End + Hour,
            samples: [new QuotaSample(Hour, 20), new QuotaSample(End + Hour / 2, 40)]));
    }

    [Fact]
    public void AnUnplaceableWindowHasNoInterval()
    {
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: null,
            windowEndMs: End,
            nowMs: Hour,
            samples: [new QuotaSample(Hour, 20), new QuotaSample(2 * Hour, 40)]));
        Assert.Null(LimitsChartFold.SparklineInterval(
            windowStartMs: End,
            windowEndMs: Start,
            nowMs: Hour,
            samples: [new QuotaSample(Hour, 20), new QuotaSample(2 * Hour, 40)]));
    }
}
