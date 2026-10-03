using TokenBar.Core;

namespace TokenBar.Core.Tests;

// Only the interval is testable here. Whether the timer starts on show, stops
// on hide and redraws the preview is WinUI wiring in SettingsWindow.cs, which
// no test project compiles; that is checked by eye on Windows (Settings open,
// a reading left stale, the preview turns grey within 60 s).
public class SettingsPreviewRefreshTests
{
    // macOS redraws its preview every 60 s (SettingsWindowView.swift,
    // TimelineView(.periodic(from: .now, by: 60)), Syrtis #440).
    [Fact]
    public void RedrawsEverySixtySecondsAsOnMacOs() =>
        Assert.Equal(TimeSpan.FromSeconds(60), SettingsPreviewRefresh.Interval);

    // A reading turns grey at most one interval after it goes stale, so the
    // interval must stay well under the threshold it is there to observe.
    [Fact]
    public void TheIntervalIsShortAgainstTheStaleThreshold() =>
        Assert.True(SettingsPreviewRefresh.Interval * 10 <= QuotaStaleness.StaleAfter);
}
