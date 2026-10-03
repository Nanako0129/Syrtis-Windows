namespace TokenBar.Core.Tests;

// G5m: the live-rate LED against macOS PopoverView.swift activityLED.
public class LiveLedTests
{
    private static double LitShare(double rate) =>
        Enumerable.Range(0, 10_000).Count(s => LiveLed.Lit((ulong)s, rate)) / 10_000.0;

    [Fact]
    public void FlickersOffAboutAQuarterOfTheTimeNearIdle() =>
        Assert.InRange(LitShare(1), 0.72, 0.78);

    [Fact]
    public void BlinksOffMoreAtHighRatesUpToFortyFivePercent()
    {
        Assert.InRange(LitShare(1_000_000), 0.52, 0.58);
        // Capped: ten times the rate blinks no more than 1M tok/min does.
        Assert.Equal(LitShare(1_000_000), LitShare(10_000_000));
    }

    // Pinned to the macOS hash so the two apps blink alike for one slot.
    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(2UL, false)]
    [InlineData(4UL, true)]
    public void HashesTheSlotAsMacOSDoes(ulong slot, bool lit) =>
        Assert.Equal(lit, LiveLed.Lit(slot, 1));

    [Fact]
    public void IsActiveOnlyWhileTokensFlow()
    {
        Assert.False(LiveLed.Active(0));
        Assert.True(LiveLed.Active(0.5));
        Assert.Equal(90, LiveLed.SlotMs);
    }
}
