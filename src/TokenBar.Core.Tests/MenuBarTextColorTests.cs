using Xunit;

namespace TokenBar.Core.Tests;

public class MenuBarTextColorTests
{
    [Fact]
    public void ParseModeRoundTrips()
    {
        Assert.Equal(TrayTextColorMode.Automatic, MenuBarTextColor.ParseMode(null));
        Assert.Equal(TrayTextColorMode.Automatic, MenuBarTextColor.ParseMode("bogus"));
        Assert.Equal(TrayTextColorMode.Custom, MenuBarTextColor.ParseMode("custom"));
        foreach (var mode in new[] { TrayTextColorMode.Automatic, TrayTextColorMode.Custom })
        {
            Assert.Equal(mode, MenuBarTextColor.ParseMode(mode.RawValue()));
        }
    }

    [Theory]
    [InlineData(null, QuotaColorLevel.Normal)]
    [InlineData(100.0, QuotaColorLevel.Normal)]
    [InlineData(25.1, QuotaColorLevel.Normal)]
    [InlineData(25.0, QuotaColorLevel.Warning)] // exactly 25: warning, not normal
    [InlineData(10.1, QuotaColorLevel.Warning)]
    [InlineData(10.0, QuotaColorLevel.Critical)] // exactly 10: critical, not warning
    [InlineData(0.0, QuotaColorLevel.Critical)]
    [InlineData(-5.0, QuotaColorLevel.Critical)]
    public void LevelForMatchesMacOsBoundaries(double? remaining, QuotaColorLevel expected)
    {
        Assert.Equal(expected, MenuBarTextColor.LevelFor(remaining));
    }

    [Theory]
    [InlineData("#000000", "#000000")]
    [InlineData("#ffffff", "#FFFFFF")] // lowercase normalizes to uppercase
    [InlineData("21c55e", "#21C55E")] // missing "#" is accepted
    [InlineData("  #21C55E  ", "#21C55E")] // surrounding whitespace trimmed
    [InlineData("#21C55", null)] // too short
    [InlineData("#21C55EE", null)] // too long
    [InlineData("#21C55G", null)] // non-hex character
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeHexValidatesAndCanonicalizes(string? input, string? expected)
    {
        Assert.Equal(expected, MenuBarTextColor.NormalizeHex(input));
    }

    [Fact]
    public void ResolveFallsBackToAutomaticWhenModeIsAutomatic()
    {
        Assert.Null(MenuBarTextColor.Resolve("automatic", "#000000"));
        Assert.Null(MenuBarTextColor.Resolve(null, "#000000"));
    }

    [Fact]
    public void ResolveFallsBackToAutomaticWhenCustomHexIsInvalid()
    {
        // A hand-edited settings file can leave garbage in the hex key; the
        // caller's automatic color, not garbage, has to win.
        Assert.Null(MenuBarTextColor.Resolve("custom", "not-a-color"));
        Assert.Null(MenuBarTextColor.Resolve("custom", null));
    }

    [Fact]
    public void ResolveReturnsNormalizedHexWhenCustomAndValid()
    {
        Assert.Equal("#21C55E", MenuBarTextColor.Resolve("custom", "21c55e"));
    }

    [Fact]
    public void StorageKeysAndDefaultsAreDistinctPerLevel()
    {
        Assert.Equal(MenuBarTextColor.CustomColorKey, QuotaColorLevel.Normal.StorageKeyFor());
        Assert.Equal(MenuBarTextColor.WarningColorKey, QuotaColorLevel.Warning.StorageKeyFor());
        Assert.Equal(MenuBarTextColor.CriticalColorKey, QuotaColorLevel.Critical.StorageKeyFor());

        Assert.Equal(MenuBarTextColor.DefaultHex, QuotaColorLevel.Normal.DefaultHexFor());
        Assert.Equal(MenuBarTextColor.WarningDefaultHex, QuotaColorLevel.Warning.DefaultHexFor());
        Assert.Equal(MenuBarTextColor.CriticalDefaultHex, QuotaColorLevel.Critical.DefaultHexFor());
    }

    [Fact]
    public void PresetsHave16DistinctValidHexValues()
    {
        Assert.Equal(16, MenuBarTextColor.Presets.Count);
        Assert.Equal(
            MenuBarTextColor.Presets.Count,
            MenuBarTextColor.Presets.Select(p => p.Hex).Distinct().Count());
        foreach (var (_, hex) in MenuBarTextColor.Presets)
        {
            Assert.Equal(hex, MenuBarTextColor.NormalizeHex(hex));
        }
    }
}
