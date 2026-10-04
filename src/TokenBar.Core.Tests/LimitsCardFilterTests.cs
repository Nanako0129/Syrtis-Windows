using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The decision BuildLimits renders from. The wiring itself
/// (BuildLimits passing the stored sets) is WinUI that no test project
/// compiles; the Settings switch → card path is checked on Windows by eye.</summary>
public sealed class LimitsCardFilterTests
{
    private static AgentUsageSnapshot Card(string clientId, string? accountKey = null) =>
        new(clientId, "source", "2026-01-01T00:00:00Z", [], AccountKey: accountKey);

    private static readonly IReadOnlyList<AgentUsageSnapshot> Agents =
    [
        Card("claude"),
        Card("claude", @"D:\work\.claude"),
        Card("codex"),
        Card("gemini"),
    ];

    private static readonly HashSet<string> None = [];

    private static bool Hides(
        IReadOnlyList<AgentUsageSnapshot> agents, IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden, bool attempted = false) =>
        LimitsCardFilter.HidesClientCard(agents, clientIds, limitsHidden, attempted);

    private static string[] Ids(IReadOnlyList<AgentUsageSnapshot> cards) =>
        [.. cards.Select(card => card.AccountKey is { } key ? $"{card.ClientId}|{key}" : card.ClientId)];

    [Fact]
    public void NothingHiddenShowsEveryCard() =>
        Assert.Equal(
            ["claude", @"claude|D:\work\.claude", "codex", "gemini"],
            Ids(LimitsCardFilter.Visible(Agents, null, None, None)));

    /// <summary>The limits switch hides the primary card only; an extra
    /// account keeps its own (macOS expandedWithExtraAccounts).</summary>
    [Fact]
    public void LimitsHiddenDropsThePrimaryCardOnOverview() =>
        Assert.Equal(
            [@"claude|D:\work\.claude", "codex", "gemini"],
            Ids(LimitsCardFilter.Visible(Agents, null, None, new HashSet<string> { "claude" })));

    [Fact]
    public void LimitsHiddenAppliesOnTheClientsOwnTab() =>
        Assert.Equal(
            [@"claude|D:\work\.claude"],
            Ids(LimitsCardFilter.Visible(Agents, ["claude"], None, new HashSet<string> { "claude" })));

    [Fact]
    public void TabHiddenDropsEveryCardOfThatClientOnOverview() =>
        Assert.Equal(
            ["codex", "gemini"],
            Ids(LimitsCardFilter.Visible(Agents, null, new HashSet<string> { "claude" }, None)));

    /// <summary>Tab visibility is an Overview concern: the restricted card
    /// still narrows to its client and ignores the tab-hidden set.</summary>
    [Fact]
    public void ClientTabNarrowsAndIgnoresTabHidden() =>
        Assert.Equal(
            ["codex"],
            Ids(LimitsCardFilter.Visible(Agents, ["codex"], new HashSet<string> { "codex" }, None)));

    /// <summary>A client tab whose card is switched off draws no card, rather
    /// than one saying "No quota data yet" over a payload that exists (macOS
    /// allRestrictedClientsHidden). An extra account keeps the card; a client
    /// not switched off keeps it even while the payload is still empty.</summary>
    [Fact]
    public void ClientTabCardIsDroppedOnlyWhenSwitchedOffWithNoExtraAccount()
    {
        var hidden = new HashSet<string> { "codex", "claude" };
        Assert.True(Hides(Agents, ["codex"], hidden));
        Assert.True(Hides([], ["codex"], hidden)); // switched off: hidden at once, data or not (main #181)
        Assert.False(Hides(Agents, ["claude"], hidden));
        Assert.False(Hides(Agents, ["gemini"], hidden));
        Assert.False(Hides([], ["gemini"], hidden));
    }

    private static readonly string[] Grok = ["grok", "grok-bot"];

    private static readonly IReadOnlyList<AgentUsageSnapshot> GrokAgents =
        [Card("grok"), Card("grok-bot"), Card("codex")];

    /// <summary>Grouped tab (grok + grok-bot), card result per hidden set:
    /// (a) only grok-bot hidden, (b) only grok hidden, (c) both hidden
    /// (macOS allRestrictedClientsHidden).</summary>
    [Fact]
    public void GroupedTabCardResultPerHiddenSet()
    {
        Assert.Equal(["grok", "grok-bot"], Ids(LimitsCardFilter.Visible(GrokAgents, Grok, None, None)));
        Assert.False(Hides(GrokAgents, Grok, None));

        var botHidden = new HashSet<string> { "grok-bot" };
        Assert.Equal(["grok"], Ids(LimitsCardFilter.Visible(GrokAgents, Grok, None, botHidden)));
        Assert.False(Hides(GrokAgents, Grok, botHidden));

        var grokHidden = new HashSet<string> { "grok" };
        Assert.Equal(["grok-bot"], Ids(LimitsCardFilter.Visible(GrokAgents, Grok, None, grokHidden)));
        Assert.False(Hides(GrokAgents, Grok, grokHidden));

        var both = new HashSet<string> { "grok", "grok-bot" };
        Assert.Empty(LimitsCardFilter.Visible(GrokAgents, Grok, None, both));
        Assert.True(Hides(GrokAgents, Grok, both));
    }

    /// <summary>An extra account on one member keeps that member, so the
    /// card stays even with both switches off.</summary>
    [Fact]
    public void GroupedTabKeepsTheCardWhenAHiddenMemberHasAnExtraAccount()
    {
        IReadOnlyList<AgentUsageSnapshot> agents = [Card("grok"), Card("grok-bot", "acct")];
        Assert.False(Hides(agents, Grok, new HashSet<string> { "grok", "grok-bot" }));
    }

    private static readonly string[] Antigravity = ["antigravity", "antigravity-cli"];

    /// <summary>Antigravity tab, antigravity hidden (macOS AgentLimitsCard.swift
    /// :502-510, :529): antigravity-cli has no switch, so the tab is not "all
    /// hidden"; the card waits before the first attempt and is dropped after
    /// it, when nothing is left to draw. Old interim rule hid it at once.</summary>
    [Fact]
    public void AntigravityTabWithAntigravityHiddenWaitsThenDisappearsOnceAttempted()
    {
        var hidden = new HashSet<string> { "antigravity" };
        IReadOnlyList<AgentUsageSnapshot> agents = [Card("antigravity"), Card("codex")];
        Assert.False(Hides(agents, Antigravity, hidden, attempted: false));
        Assert.True(Hides(agents, Antigravity, hidden, attempted: true));
        Assert.False(Hides([], Antigravity, hidden, attempted: false));
        Assert.True(Hides([], Antigravity, hidden, attempted: true));
        Assert.False(Hides(agents, Antigravity, None, attempted: true)); // control: antigravity has a card
        // An extra account on a hidden member keeps the card past the attempt.
        Assert.False(Hides([Card("antigravity", "acct")], Antigravity, hidden, attempted: true));
    }

    /// <summary>Single-client tab with no snapshot (any fetch outcome): not
    /// switched off, the card keeps main's loading / could-not-check states
    /// (macOS would draw placeholder rows, :437-439, :784-787); switched off,
    /// it is hidden at once, as main #181 and macOS :502-510.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void SingleClientTabWithNoSnapshotHidesOnlyWhenSwitchedOff(string client)
    {
        Assert.False(Hides([Card("gemini")], [client], None));
        Assert.False(Hides([], [client], None));
        Assert.True(Hides([], [client], new HashSet<string> { client }));
    }

    /// <summary>Grok tab with no snapshot at all (macOS :502-510, :437-439):
    /// grok-bot has a placeholder, so with grok hidden the card stays (it
    /// draws grok-bot's sign-in line) before and after the attempt; only both
    /// hidden drops it, at once. Old interim rule followed grok alone and hid
    /// the card when grok was hidden.</summary>
    [Fact]
    public void GrokTabWithNoSnapshotKeepsTheCardForAnUnhiddenBot()
    {
        foreach (var attempted in new[] { false, true })
        {
            Assert.False(Hides([], Grok, new HashSet<string> { "grok" }, attempted));
            Assert.False(Hides([], Grok, new HashSet<string> { "grok-bot" }, attempted));
            Assert.True(Hides([], Grok, new HashSet<string> { "grok", "grok-bot" }, attempted));
        }
    }

    /// <summary>G's report (a): Grok has a snapshot but is hidden, grok-bot is
    /// not signed in (no snapshot). macOS keeps the card for grok-bot's
    /// placeholder; the old rule dropped the whole card.</summary>
    [Fact]
    public void GrokTabGrokHiddenWithBotNotSignedInKeepsTheCardForItsPlaceholder()
    {
        var grokHidden = new HashSet<string> { "grok" };
        Assert.False(Hides([Card("grok")], Grok, grokHidden, attempted: true));
        Assert.False(Hides([Card("grok")], Grok, grokHidden, attempted: false));
        Assert.False(Hides([Card("grok"), Card("grok-bot")], Grok, grokHidden, attempted: true));
    }

    /// <summary>:529 on a client with no placeholder rows: before the attempt
    /// the card waits (loading), after it a card with nothing to draw is not
    /// drawn. A placeholder client keeps its card (it draws placeholders).</summary>
    [Fact]
    public void ACardWithNothingToDrawIsDroppedOnlyOnceAttempted()
    {
        Assert.False(Hides([], ["copilot"], None, attempted: false));
        Assert.True(Hides([], ["copilot"], None, attempted: true));
        Assert.False(Hides([Card("copilot")], ["copilot"], None, attempted: true));
        Assert.False(Hides([], ["claude"], None, attempted: true)); // placeholder row
    }

    /// <summary>macOS known() (:437-439) excludes a member with only an
    /// extra-account snapshot (no placeholder, no primary), so a restricted
    /// card has nothing for it: dropped once attempted. A placeholder client
    /// with an extra still draws its primary's placeholder.</summary>
    [Fact]
    public void AnExtraOnlyMemberIsNotKnownSoTheCardIsDroppedOnceAttempted()
    {
        Assert.True(Hides([Card("copilot", "acct")], ["copilot"], None, attempted: true));
        Assert.False(Hides([Card("copilot", "acct")], ["copilot"], None, attempted: false));
        Assert.False(Hides([Card("claude", "acct")], ["claude"], None, attempted: true));
    }

    /// <summary>The decision and the card draw from the same pieces: for any
    /// payload, an attempted restricted card is hidden exactly when Rows is
    /// empty or every member is switched off with no extra account.</summary>
    [Fact]
    public void TheHideDecisionAgreesWithTheRowsTheCardDraws()
    {
        IReadOnlyList<AgentUsageSnapshot>[] payloads =
            [[], [Card("grok")], [Card("grok-bot")], [Card("grok"), Card("grok-bot")], [Card("grok", "acct")]];
        HashSet<string>[] hiddens = [[], ["grok"], ["grok-bot"], ["grok", "grok-bot"]];
        foreach (var agents in payloads)
        {
            foreach (var hidden in hiddens)
            {
                var visible = LimitsCardFilter.Visible(agents, Grok, None, hidden);
                var rows = LimitsPlaceholders.Rows(visible, agents, Grok, false, None, hidden);
                var allHidden = Grok.All(hidden.Contains) && !agents.Any(a => a.AccountKey is not null);
                Assert.Equal(allHidden || rows.Count == 0, Hides(agents, Grok, hidden, attempted: true));
            }
        }
    }

    [Fact]
    public void GrokTabBotHiddenWithGrokSnapshotKeepsTheCard()
    {
        var botHidden = new HashSet<string> { "grok-bot" };
        Assert.False(Hides([Card("grok"), Card("grok-bot")], Grok, botHidden));
        Assert.False(Hides([Card("grok")], Grok, botHidden));
        Assert.True(Hides(
            [Card("grok"), Card("grok-bot")], Grok, new HashSet<string> { "grok", "grok-bot" }));
    }

    [Fact]
    public void EmptyClientListHidesNothing()
    {
        Assert.False(Hides(Agents, [], None));
        Assert.False(Hides([], [], new HashSet<string> { "codex" }));
    }
}
