using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The opencode tab's routed-subscription cards (macOS
/// AgentLimitsCard.swift :321-328, :364-366, :421-440, :526-565). The wiring
/// (DashboardView passing the list on) is WinUI no test project compiles; the
/// decisions it asks are here.</summary>
public sealed class OpencodeRoutesTests
{
    public OpencodeRoutesTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static AgentUsageSnapshot Card(string clientId, string? accountKey = null) =>
        new(clientId, "source", "2026-01-01T00:00:00Z", [], AccountKey: accountKey);

    private static AgentUsagePayload Payload(string[] subs, params AgentUsageSnapshot[] agents) =>
        new("2026-01-01T00:00:00Z", agents, subs);

    private static readonly HashSet<string> None = [];
    private static readonly string[] OpencodeTab = ["opencode"];

    /// <summary>The rows the opencode tab draws: the routed list through the
    /// same snapshot filter and placeholder rows every restricted card uses.</summary>
    private static string[] Rows(AgentUsagePayload payload, IReadOnlySet<string> limitsHidden)
    {
        var ids = OpencodeRoutes.LimitsClients(OpencodeTab, payload);
        var visible = LimitsCardFilter.Visible(payload.Agents, ids, None, limitsHidden);
        return [.. LimitsPlaceholders.Rows(visible, payload.Agents, ids, multiClient: false, None, limitsHidden)
            .Select(row => row.ClientId)];
    }

    [Fact]
    public void RoutedSubscriptionsDrawTheirOwnCards() =>
        Assert.Equal(
            ["codex", "copilot"],
            Rows(Payload(["Codex", "Copilot"], Card("codex"), Card("copilot")), None));

    [Fact]
    public void OpencodeOwnQuotaCardLeadsAndIsNeverDuplicated()
    {
        Assert.Equal(
            ["opencode", "codex", "copilot"],
            Rows(Payload(["Codex", "Copilot"], Card("codex"), Card("opencode"), Card("copilot")), None));
        // A routed label that resolves back to opencode stays out of the tail.
        Assert.Equal(["opencode", "codex"], OpencodeRoutes.CardClients(true, ["codex", "opencode"]));
        Assert.Equal(["codex"], OpencodeRoutes.CardClients(false, ["codex", "opencode"]));
    }

    [Fact]
    public void ALimitsHiddenRoutedClientIsDropped() =>
        Assert.Equal(
            ["copilot"],
            Rows(Payload(["Codex", "Copilot"], Card("codex"), Card("copilot")), new HashSet<string> { "codex" }));

    [Fact]
    public void ALabelWhoseClientHasNoSnapshotIsDropped() =>
        Assert.Equal(
            ["copilot"],
            Rows(Payload(["Codex", "Copilot"], Card("copilot")), None));

    /// <summary>The Xai label resolves to the owner `grok`, not the raw `xai`
    /// the snapshot is never keyed by (macOS :424-427).</summary>
    [Fact]
    public void ALabelResolvesThroughTheSubscriptionOwner() =>
        Assert.Equal(["grok"], Rows(Payload(["Xai"], Card("grok")), None));

    [Fact]
    public void NoOwnQuotaAndNoRoutesDrawsNothing() =>
        Assert.Empty(Rows(Payload([], Card("codex")), None));

    [Fact]
    public void OtherTabsKeepTheirOwnClients() =>
        Assert.Equal(
            ["codex"],
            OpencodeRoutes.LimitsClients(["codex"], Payload(["Codex"], Card("codex"), Card("copilot"))));

    /// <summary>The old list was ["opencode"] alone, which drew nothing (opencode
    /// has no placeholder) and so hid the card once attempted; the routed
    /// list draws codex, so the card stays.</summary>
    [Fact]
    public void TheHideDecisionAsksTheRowsTheCardDraws()
    {
        var payload = Payload(["Codex"], Card("codex"));
        var ids = OpencodeRoutes.LimitsClients(OpencodeTab, payload);
        Assert.False(LimitsCardFilter.HidesClientCard(payload.Agents, ids, None, attempted: true, OpencodeTab));
        Assert.True(LimitsCardFilter.HidesClientCard(payload.Agents, OpencodeTab, None, attempted: true));
        // Every routed card switched off: nothing visible once attempted.
        Assert.True(LimitsCardFilter.HidesClientCard(
            payload.Agents, ids, new HashSet<string> { "codex" }, attempted: true, OpencodeTab));
        // Not attempted yet: the card waits and shows its loading state.
        Assert.False(LimitsCardFilter.HidesClientCard(
            payload.Agents, ids, new HashSet<string> { "codex" }, attempted: false, OpencodeTab));
    }

    /// <summary>macOS allRestrictedClientsHidden (:502-510) reads the card's
    /// own `clients` ["opencode"], not the routed list: opencode switched off
    /// (and no extra account) hides the whole card although codex would draw.</summary>
    [Fact]
    public void ASwitchedOffOpencodeHidesTheWholeCardAsOnMacOS()
    {
        var payload = Payload(["Codex"], Card("codex"));
        var ids = OpencodeRoutes.LimitsClients(OpencodeTab, payload);
        Assert.True(LimitsCardFilter.HidesClientCard(
            payload.Agents, ids, new HashSet<string> { "opencode" }, attempted: false, OpencodeTab));
        // An extra account of opencode is exempt from the hide.
        var withExtra = Payload(["Codex"], Card("codex"), Card("opencode", "acct"));
        Assert.False(LimitsCardFilter.HidesClientCard(
            withExtra.Agents, OpencodeRoutes.LimitsClients(OpencodeTab, withExtra),
            new HashSet<string> { "opencode" }, attempted: false, OpencodeTab));
    }

    [Fact]
    public void HeaderAndEmptyStateTextFollowMacOS()
    {
        string[] subs = ["Codex", "Copilot"];
        Assert.Equal("↔ Routes through opencode", OpencodeRoutes.HeaderLine(true, true, subs));
        Assert.Equal("↔ Routes through opencode", OpencodeRoutes.HeaderLine(true, true, []));
        Assert.Equal("opencode also taps: Codex · Copilot", OpencodeRoutes.HeaderLine(false, false, subs));
        Assert.Null(OpencodeRoutes.HeaderLine(false, false, []));
        // A restricted card of another client says nothing.
        Assert.Null(OpencodeRoutes.HeaderLine(false, true, subs));
    }
}
