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
            Ids(LimitsCardFilter.Visible(Agents, "claude", None, new HashSet<string> { "claude" })));

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
            Ids(LimitsCardFilter.Visible(Agents, "codex", new HashSet<string> { "codex" }, None)));

    /// <summary>A client tab whose card is switched off draws no card, rather
    /// than one saying "No quota data yet" over a payload that exists (macOS
    /// allRestrictedClientsHidden). An extra account keeps the card; a client
    /// not switched off keeps it even while the payload is still empty.</summary>
    [Fact]
    public void ClientTabCardIsDroppedOnlyWhenSwitchedOffWithNoExtraAccount()
    {
        var hidden = new HashSet<string> { "codex", "claude" };
        Assert.True(LimitsCardFilter.HidesClientCard(Agents, "codex", hidden));
        Assert.True(LimitsCardFilter.HidesClientCard([], "codex", hidden));
        Assert.False(LimitsCardFilter.HidesClientCard(Agents, "claude", hidden));
        Assert.False(LimitsCardFilter.HidesClientCard(Agents, "gemini", hidden));
        Assert.False(LimitsCardFilter.HidesClientCard([], "gemini", hidden));
    }
}
