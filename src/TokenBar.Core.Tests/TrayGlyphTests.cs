using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>The gauge and title decisions the tray renderer draws from,
/// against macOS TrayIcons.swift:85-157 (#419) and MenuBarTextColor.swift
/// <c>applyingStale</c> (#420). The pixels themselves (TrayIconRenderer)
/// compile under no test project and are checked visually.</summary>
public class TrayGlyphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoReadingDrawsTheNoReadingGlyphWhateverTheStamp(bool stale) =>
        // Pre-port Windows drew a full gauge here, which reads as 100% left.
        Assert.Equal(GaugeAppearance.NoReading, TrayGlyph.Gauge(null, stale));

    [Theory]
    [InlineData(57, false, GaugeAppearance.Live)]
    [InlineData(57, true, GaugeAppearance.Stale)]
    [InlineData(0, false, GaugeAppearance.Live)] // 0% is a reading: the bright empty track
    [InlineData(0, true, GaugeAppearance.Stale)]
    public void AReadingIsLiveOrStale(double remaining, bool stale, GaugeAppearance expected) =>
        Assert.Equal(expected, TrayGlyph.Gauge(remaining, stale));

    [Fact]
    public void AStaleQuotaLeftValueTakesTheStaleGrey() =>
        // Pre-port Windows kept the automatic or custom colour here.
        Assert.True(TrayGlyph.TitleIsStale(TrayMode.QuotaLeft, 12, stale: true));

    [Theory]
    [InlineData(TrayMode.QuotaLeft, null, true)] // nothing to be stale
    [InlineData(TrayMode.QuotaLeft, 12.0, false)] // fresh
    [InlineData(TrayMode.TodayTokens, 12.0, true)] // not a quota verdict
    [InlineData(TrayMode.TodayCost, 12.0, true)]
    public void OtherValuesKeepTheirColour(TrayMode mode, double? remaining, bool stale) =>
        Assert.False(TrayGlyph.TitleIsStale(mode, remaining, stale));

    [Fact]
    public void NoReadingGeometryMatchesMacOS()
    {
        Assert.Equal(0.3, TrayGlyph.NoReadingAlpha);
        Assert.Equal(1.5, TrayGlyph.NoReadingSlashWidth);
        Assert.Equal(3.4, TrayGlyph.NoReadingSlashGap);
        Assert.Equal(0.9, TrayGlyph.NoReadingSlashAlpha);
        Assert.Equal((2.5, 13.5), TrayGlyph.NoReadingSlashFrom);
        Assert.Equal((13.5, 2.5), TrayGlyph.NoReadingSlashTo);
    }
}
