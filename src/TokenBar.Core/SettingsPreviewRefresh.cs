namespace TokenBar.Core;

/// <summary>How often the Settings preview redraws on its own. The stale rule
/// (<see cref="QuotaStaleness"/>) reads the clock, so with polls failing and
/// nothing else changing, a reading that crosses 30 minutes while Settings is
/// open would keep its colour until some unrelated setting changed. macOS
/// redraws its preview every 60 seconds for that reason
/// (SettingsWindowView.swift, <c>TimelineView(.periodic(from: .now, by: 60))</c>,
/// Syrtis #440). It stops while the window is hidden, so a closed window
/// does no redraws. macOS tears its preview timelines down on close for the
/// same reason (a willClose observer in SettingsWindowController swaps in a
/// placeholder, bae97afc, added after a 40 fps animation preview kept
/// rendering off-screen).</summary>
public static class SettingsPreviewRefresh
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
}
