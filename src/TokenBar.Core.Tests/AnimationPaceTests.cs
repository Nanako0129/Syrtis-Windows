using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>The animated tray icon's pace and speed curve, against macOS
/// AnimationPace.swift:14-61 and TrayAnimator.swift:408-433. Expected
/// intervals are the macOS formula evaluated at each rate.</summary>
public class AnimationPaceTests
{
    [Theory]
    [InlineData(0, 500)]
    [InlineData(50_000, 500)] // the floor itself is still idle (strict >)
    [InlineData(100_000, 301)]
    [InlineData(1_000_000, 55)] // log ramp: the pre-port linear curve pegged 25 ms here
    [InlineData(3_000_000, 25)]
    [InlineData(10_000_000, 25)] // clamped at the cap
    public void ModeratePaceFollowsTheMacOSCurve(double tokensPerMinute, int milliseconds) =>
        Assert.Equal(milliseconds,
            TrayAnimationSpeed.IntervalMilliseconds(tokensPerMinute, AnimationPace.Moderate));

    [Theory]
    [InlineData(AnimationPace.Light, 600_000, 25)] // "Light at 600K tokens/min"
    [InlineData(AnimationPace.Light, 120_000, 81)]
    [InlineData(AnimationPace.Heavy, 10_000_000, 25)] // "Heavy at 10M"
    [InlineData(AnimationPace.Heavy, 3_000_000, 60)] // Moderate's top is mid-ramp for Heavy
    public void PaceScalesTheRateBeforeTheCurve(
        AnimationPace pace, double tokensPerMinute, int milliseconds) =>
        Assert.Equal(milliseconds, TrayAnimationSpeed.IntervalMilliseconds(tokensPerMinute, pace));

    [Fact]
    public void NoRateIsIdle() =>
        Assert.Equal(500, TrayAnimationSpeed.IntervalMilliseconds(null, AnimationPace.Light));

    [Fact]
    public void MultipliersMatchMacOS()
    {
        Assert.Equal(0.2, AnimationPace.Light.Multiplier());
        Assert.Equal(1, AnimationPace.Moderate.Multiplier());
        Assert.Equal(16.67 / 5, AnimationPace.Heavy.Multiplier());
    }

    [Theory]
    [InlineData("light", AnimationPace.Light)]
    [InlineData("moderate", AnimationPace.Moderate)]
    [InlineData("heavy", AnimationPace.Heavy)]
    [InlineData(null, AnimationPace.Moderate)]
    [InlineData("Heavy", AnimationPace.Moderate)] // raw values are exact, as macOS rawValue
    public void ParsesTheMacOSRawValues(string? raw, AnimationPace expected)
    {
        Assert.Equal(expected, AnimationPaces.Parse(raw));
        if (raw is "light" or "moderate" or "heavy")
        {
            Assert.Equal(raw, expected.RawValue());
        }
    }

    [Fact]
    public void ReadsTheSharedPrefsKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tb-pace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.Equal(AnimationPace.Moderate, AnimationPaces.Current(store));
            store.SetString("tokenbar.tray.animationPace", "light");
            Assert.Equal(AnimationPace.Light, AnimationPaces.Current(store));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
