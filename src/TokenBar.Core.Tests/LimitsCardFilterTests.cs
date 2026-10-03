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
        Assert.True(LimitsCardFilter.HidesClientCard(Agents, ["codex"], hidden));
        Assert.True(LimitsCardFilter.HidesClientCard([], ["codex"], hidden));
        Assert.False(LimitsCardFilter.HidesClientCard(Agents, ["claude"], hidden));
        Assert.False(LimitsCardFilter.HidesClientCard(Agents, ["gemini"], hidden));
        Assert.False(LimitsCardFilter.HidesClientCard([], ["gemini"], hidden));
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
        Assert.False(LimitsCardFilter.HidesClientCard(GrokAgents, Grok, None));

        var botHidden = new HashSet<string> { "grok-bot" };
        Assert.Equal(["grok"], Ids(LimitsCardFilter.Visible(GrokAgents, Grok, None, botHidden)));
        Assert.False(LimitsCardFilter.HidesClientCard(GrokAgents, Grok, botHidden));

        var grokHidden = new HashSet<string> { "grok" };
        Assert.Equal(["grok-bot"], Ids(LimitsCardFilter.Visible(GrokAgents, Grok, None, grokHidden)));
        Assert.False(LimitsCardFilter.HidesClientCard(GrokAgents, Grok, grokHidden));

        var both = new HashSet<string> { "grok", "grok-bot" };
        Assert.Empty(LimitsCardFilter.Visible(GrokAgents, Grok, None, both));
        Assert.True(LimitsCardFilter.HidesClientCard(GrokAgents, Grok, both));
    }

    /// <summary>An extra account on one member keeps that member, so the
    /// card stays even with both switches off.</summary>
    [Fact]
    public void GroupedTabKeepsTheCardWhenAHiddenMemberHasAnExtraAccount()
    {
        IReadOnlyList<AgentUsageSnapshot> agents = [Card("grok"), Card("grok-bot", "acct")];
        Assert.False(LimitsCardFilter.HidesClientCard(agents, Grok, new HashSet<string> { "grok", "grok-bot" }));
    }
}
