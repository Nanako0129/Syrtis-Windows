using TokenBar.App;
using TokenBar.Core;

namespace TokenBar.Core.Tests;

// macOS PopoverView.handleKeyDown / handleFlagsChanged. The shortcut handlers in
// DashboardView only route a key to these functions, so the rules are asserted here.
public class TabShortcutsTests
{
    private static readonly List<string> Tabs = TabShortcuts.Tabs(["claude", "codex", "gemini"]);

    [Fact]
    public void TabsAreOverviewThenClientsInRowOrder() =>
        Assert.Equal([ClientRegistry.OverviewTab, "claude", "codex", "gemini"], Tabs);

    // The old Windows binding selected the Nth LENS; Ctrl+2 must now be the
    // first client tab (index 2 on macOS's row: Overview is 1).
    [Fact]
    public void CtrlTwoIsTheFirstClientTabNotTheSecondLens()
    {
        Assert.Equal("claude", TabShortcuts.Target(Tabs, 2));
        Assert.Equal(ClientRegistry.OverviewTab, TabShortcuts.Target(Tabs, 1));
        Assert.Equal("gemini", TabShortcuts.Target(Tabs, 4));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(0)]
    public void CtrlNPastTheCountDoesNothing(int n) =>
        Assert.Null(TabShortcuts.Target(Tabs, n));

    [Fact]
    public void NextWrapsFromLastToOverview() =>
        Assert.Equal(ClientRegistry.OverviewTab, TabShortcuts.Step(Tabs, "gemini", +1));

    [Fact]
    public void PreviousWrapsFromOverviewToLast() =>
        Assert.Equal("gemini", TabShortcuts.Step(Tabs, ClientRegistry.OverviewTab, -1));

    [Fact]
    public void StepsMoveOneTabInTheMiddle()
    {
        Assert.Equal("codex", TabShortcuts.Step(Tabs, "claude", +1));
        Assert.Equal("claude", TabShortcuts.Step(Tabs, "codex", -1));
    }

    [Fact]
    public void UnknownActiveCountsAsTheFirstTab()
    {
        Assert.Equal("claude", TabShortcuts.Step(Tabs, "vanished", +1));
        Assert.Equal("gemini", TabShortcuts.Step(Tabs, "vanished", -1));
    }

    [Fact]
    public void OverviewAloneStepsToItself()
    {
        var only = TabShortcuts.Tabs([]);
        Assert.Equal(ClientRegistry.OverviewTab, TabShortcuts.Step(only, ClientRegistry.OverviewTab, +1));
        Assert.Equal(ClientRegistry.OverviewTab, TabShortcuts.Step(only, ClientRegistry.OverviewTab, -1));
        Assert.Null(TabShortcuts.Target(only, 2));
    }

    [Fact]
    public void HintDelayIsFourHundredMs() => Assert.Equal(400, TabShortcuts.HintDelayMs);

    [Fact]
    public void CtrlAloneStartsPendingButShowsNothingUntilTheTimerFires()
    {
        var gate = new CtrlHintGate();
        Assert.Equal(HintStep.StartTimer, gate.Update(ctrlAlone: true));
        Assert.True(gate.Pending);
        Assert.False(gate.Visible); // not shown immediately
        gate.Fire();
        Assert.True(gate.Visible);
        Assert.False(gate.Pending);
    }

    [Fact]
    public void KeyRepeatDoesNotRestartTheTimerOrHide()
    {
        var gate = new CtrlHintGate();
        gate.Update(true);
        Assert.Equal(HintStep.None, gate.Update(true)); // pending
        gate.Fire();
        Assert.Equal(HintStep.None, gate.Update(true)); // visible
        Assert.True(gate.Visible);
    }

    [Fact]
    public void ReleaseBeforeTheDelayCancelsAndNeverShows()
    {
        var gate = new CtrlHintGate();
        gate.Update(true);
        Assert.Equal(HintStep.CancelAndHide, gate.Update(false));
        gate.Fire(); // a tick that raced the release
        Assert.False(gate.Visible);
        Assert.False(gate.Pending);
    }

    [Fact]
    public void ReleaseOrAnotherModifierHidesShownHints()
    {
        var gate = new CtrlHintGate();
        gate.Update(true);
        gate.Fire();
        // Ctrl+Shift (or Alt/Win) arrives as ctrlAlone == false, same as release.
        Assert.Equal(HintStep.CancelAndHide, gate.Update(false));
        Assert.False(gate.Visible);
        // And Ctrl alone again starts a fresh 400 ms wait.
        Assert.Equal(HintStep.StartTimer, gate.Update(true));
        Assert.False(gate.Visible);
    }
}
