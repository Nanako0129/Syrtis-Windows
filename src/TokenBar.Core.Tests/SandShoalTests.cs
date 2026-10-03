using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>The Sand shoal tray style against macOS TrayAnimator.swift
/// "MARK: Sand" (:440-520). The glue in TrayAnimator/TrayService/
/// SettingsWindow compiles under no test project, so every decision lives in
/// SandShoal and is asserted here.</summary>
public class SandShoalTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(49_999, 0)]
    [InlineData(50_000, 1)]
    [InlineData(50_001, 1)]
    [InlineData(299_999, 1)]
    [InlineData(300_000, 2)]
    [InlineData(300_001, 2)]
    [InlineData(1_499_999, 2)]
    [InlineData(1_500_000, 3)]
    [InlineData(1_500_001, 3)]
    [InlineData(50_000_000, 3)]
    public void LevelFollowsTheThresholds(double tokensPerMinute, int level) =>
        Assert.Equal(level, SandShoal.Level(tokensPerMinute));

    [Theory]
    // The animator hands the level the pace-SCALED rate.
    [InlineData(AnimationPace.Moderate, 300_000, 2)]
    [InlineData(AnimationPace.Moderate, 299_999, 1)]
    [InlineData(AnimationPace.Light, 10_000, 1)] // 10K / 0.2 = 50K
    [InlineData(AnimationPace.Light, 9_999, 0)]
    [InlineData(AnimationPace.Light, 300_000, 3)] // 1.5M
    [InlineData(AnimationPace.Heavy, 1_000_000, 1)] // 1M / 3.334 = 299.9K
    [InlineData(AnimationPace.Heavy, 1_100_000, 2)]
    public void LevelUsesThePaceScaledRate(AnimationPace pace, double raw, int level) =>
        Assert.Equal(level, SandShoal.Level(pace.Scaled(raw)));

    [Fact]
    public void NoCurrentLevelIsTheRawLevel() =>
        Assert.Equal(1, SandShoal.Level(60_000, null));

    [Theory]
    // Up: rate / 1.2 must still clear the higher level.
    [InlineData(55_000, 0, 0)] // raw 1, but 45.8K < 50K
    [InlineData(60_000, 0, 1)] // 50K clears
    [InlineData(359_999, 1, 1)] // raw 2, 299,999 < 300K
    [InlineData(360_000, 1, 2)]
    [InlineData(1_799_999, 2, 2)]
    [InlineData(1_800_000, 2, 3)]
    // Down: rate * 1.2 must fall below the current level's threshold.
    [InlineData(45_000, 1, 1)] // raw 0, but 54K still clears 50K
    [InlineData(41_666, 1, 0)] // 49,999.2
    [InlineData(250_000, 2, 2)]
    [InlineData(249_999, 2, 1)] // 299,998.8
    [InlineData(1_249_999, 3, 2)]
    [InlineData(1_300_000, 3, 3)]
    [InlineData(0, 3, 0)] // a long idle drops every level at once
    public void HysteresisMatchesMacOS(double rate, int current, int expected) =>
        Assert.Equal(expected, SandShoal.Level(rate, current));

    [Fact]
    public void NoFlappingAroundABoundary()
    {
        int? level = 0;
        var changes = 0;
        // 300K +-5% wobble, as a 30 s poll would see it, starting from level 1.
        level = 1;
        foreach (var rate in new double[] { 295_000, 305_000, 292_000, 310_000, 299_000, 315_000, 290_000 })
        {
            var next = SandShoal.Level(rate, level);
            changes += next != level ? 1 : 0;
            level = next;
        }

        Assert.Equal(0, changes);
        Assert.Equal(1, level);
    }

    [Fact]
    public void EveryLevelPlaysAtOneFixedRate()
    {
        Assert.Equal(24.0, SandShoal.Fps);
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 24), SandShoal.FrameInterval);
        // FrameInterval takes no level or rate: usage can only change the set.
        Assert.Equal(41.67, SandShoal.FrameInterval.TotalMilliseconds, 2);
    }

    [Fact]
    public void StyleIsSandAndAnimatedWithItsLabel()
    {
        Assert.Equal("sand", SandShoal.Style);
        Assert.True(SandShoal.IsAnimated("sand"));
        Assert.True(SandShoal.IsAnimated("cat"));
        Assert.True(SandShoal.IsAnimated("parrot"));
        Assert.False(SandShoal.IsAnimated("ring"));
        Assert.False(SandShoal.IsAnimated(null));
        Assert.Equal("Sand shoal", SandShoal.Label);
    }

    [Fact]
    public void FrameKeyAndDirectoryPerLevelAndTheme()
    {
        Assert.Equal("sand2|dark", SandShoal.FrameKey(2, true));
        Assert.Equal("sand0|light", SandShoal.FrameKey(0, false));
        Assert.Equal("anim-sand3", SandShoal.AssetDirectory("sand3", true));
        Assert.Equal("anim-sand1-light", SandShoal.AssetDirectory("sand1", false));
        Assert.Null(SandShoal.AssetDirectory("sand", true));
        Assert.Null(SandShoal.AssetDirectory("sand4", true));
        Assert.Null(SandShoal.AssetDirectory("cat", true));
    }

    [Fact]
    public void SandSetsLoadOnlyOnFirstUseOfTheSandStyle()
    {
        Assert.Empty(SandShoal.SetsToLoad("cat", false));
        Assert.Empty(SandShoal.SetsToLoad("parrot", false));
        Assert.Empty(SandShoal.SetsToLoad("sand", true));
        var sets = SandShoal.SetsToLoad("sand", false);
        Assert.Equal(8, sets.Count);
        Assert.Equal(8, sets.Distinct().Count());
        Assert.Contains("sand0|dark", sets);
        Assert.Contains("sand3|light", sets);
    }

    [Fact]
    public void EachSetShipsExactly144Frames()
    {
        // Walk up from the test binary to the repo's Assets folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "TokenBar.App", "Assets")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var assets = Path.Combine(dir!.FullName, "src", "TokenBar.App", "Assets");
        foreach (var level in Enumerable.Range(0, SandShoal.Levels))
        {
            foreach (var dark in new[] { true, false })
            {
                var set = Path.Combine(assets, SandShoal.AssetDirectory($"sand{level}", dark)!);
                Assert.Equal(144, Directory.GetFiles(set, "frame-*.png").Length);
            }
        }
    }
}
