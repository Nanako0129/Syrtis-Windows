using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// The Session-window card's state choice, its per-client filter, its hatched
// no-sample series and its copy. DashboardView.Quota.cs is WinUI and no test
// project compiles it, so this is where those decisions are asserted.
public class WindowCardTextTests
{
    private const long FiveHours = 5 * 3_600;
    private const long ResetAt = 1_767_330_000;

    public WindowCardTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaHistorySample Sample(
        double usedPercent, long sampledAt, bool active = true, long duration = FiveHours) =>
        new(
            ResetAt: ResetAt,
            DurationSeconds: duration,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: active);

    private static QuotaHistorySeries Series(
        string clientId, string windowKey, params QuotaHistorySample[] samples) =>
        new(clientId, "primary", windowKey, samples);

    private static QuotaHistorySeries Series(
        string clientId, string accountScope, string windowKey, params QuotaHistorySample[] samples) =>
        new(clientId, accountScope, windowKey, samples);

    private static WindowMessage Message(
        string client, string provider, string model, long at = 1) =>
        new(at, client, provider, model, 0, 100, 0, 0, 0, 0.5, true);

    // `Tabs` now enumerates the LIVE side — one tab per window the client is
    // currently reporting — rather than the store side, so every fixture that
    // wants a tab has to supply the live window that tab comes from.
    private static UsageWindow Window(string cardId, string label, string? windowKey) =>
        new(
            Label: label,
            UsedPercent: 10,
            RemainingPercent: 90,
            CardId: cardId,
            PaceStatus: windowKey is null
                ? new PaceStatus(UsagePaceState.Unavailable)
                : new PaceStatus(UsagePaceState.Available, WindowKey: windowKey));

    private static AgentUsagePayload Quota(string clientId, params UsageWindow[] windows) =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(clientId, "source", "2026-01-01T00:00:00Z", windows)]);

    /// <summary>The live agent's own resolved history scope — the HMAC a
    /// stored <see cref="QuotaHistorySeries.AccountScope"/> is compared
    /// against, per <see cref="WindowCardText.Tabs"/>.</summary>
    private static AgentUsagePayload Quota(
        string clientId, string historyScope, params UsageWindow[] windows) =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(
                clientId, "source", "2026-01-01T00:00:00Z", windows,
                HistoryScope: new AccountScopeStatus(Scope: historyScope))]);

    // ---- the live-window enumeration -------------------------------------

    // The bug this fix removes: several stored series collapsing onto one
    // live window used to draw one tab PER SERIES, and a series with no
    // live-side match fell back to printing its raw store key. Four live
    // windows — one of them keyless, one of them fed by two stored series
    // that share a windowKey — must yield exactly four tabs, each carrying
    // the provider's own label and none of them a raw key.
    [Fact]
    public void FourLiveWindowsYieldFourTabsWithDistinctLabelsEvenWhenSeriesShareAWindowKey()
    {
        var quota = Quota(
            "codex",
            Window("codex|session.v1", "Session", "session.v1"),
            Window("codex|weekly.v1", "Weekly", "weekly.v1"),
            Window("codex|fable.v1", "Fable only", "weekly_scoped.fable.v1"),
            Window("codex|additional.d62616d2.primary.v1", "Extra usage", windowKey: null));

        var tabs = WindowCardText.Tabs(
            [
                Series("codex", "session.v1", Sample(40, ResetAt - 600)),
                // A second stored series landing on the SAME windowKey — a
                // rename or a re-carded provider revision. Must not draw a
                // second tab for the one live window it maps to.
                Series("codex", "session.v1", Sample(41, ResetAt - 600)),
                Series("codex", "weekly_scoped.fable.v1", Sample(12, ResetAt - 600)),
            ],
            quota,
            clientId: "codex");

        Assert.Equal(4, tabs.Count);
        Assert.Equal(
            new[] { "Session", "Weekly", "Fable only", "Extra usage" }.OrderBy(l => l),
            tabs.Select(tab => tab.Label).OrderBy(l => l));
        // No tab label is ever a raw store key — neither the dotted window
        // key nor the hashed card id it used to fall back to.
        Assert.All(tabs, tab => Assert.False(string.IsNullOrWhiteSpace(tab.Label)));
        Assert.DoesNotContain(tabs, tab => tab.Label == "weekly_scoped.fable.v1");
        Assert.DoesNotContain(tabs, tab => tab.Label!.Contains("d62616d2"));
    }

    // A live window the store has no series for still gets a tab — it just
    // has no history to draw, which is `NoQuotaHistory`, not an absent tab.
    [Fact]
    public void ALiveWindowWithNoStoredSeriesStillGetsATab()
    {
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var tabs = WindowCardText.Tabs([], quota, "codex");

        Assert.Single(tabs);
        Assert.Equal("Weekly", tabs[0].Label);
        Assert.Equal(
            WindowCardState.NoQuotaHistory,
            WindowCardText.State(tabs[0], WindowEquivalence.FetchOutcome.Succeeded));
        Assert.Equal(
            WindowCardState.Loading, WindowCardText.State(tabs[0], WindowEquivalence.FetchOutcome.NotAttempted));
    }

    // A stored series with no live window (the provider stopped reporting
    // it) produces no tab: macOS cannot show a window its own live payload
    // no longer offers either.
    [Fact]
    public void AStoredSeriesWithNoLiveWindowProducesNoTab()
    {
        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));

        var tabs = WindowCardText.Tabs(
            [
                Series("codex", "session.v1", Sample(40, ResetAt - 600)),
                Series("codex", "retired.window.v1", Sample(99, ResetAt - 600)),
            ],
            quota,
            "codex");

        Assert.Single(tabs);
        Assert.Equal("Session", tabs[0].Label);
    }

    // Codex round 16, finding 2: when the agent-usage fetch that would supply
    // `quota` is still pending or just failed, `quota` is null and there is no
    // `agent?.UniqueCardWindows` to enumerate — but that must not throw away a
    // series this client's own history read already retrieved successfully.
    // No live payload at all here, only a stored series.
    [Fact]
    public void ANullQuotaFallsBackToTheStoredSeriesInsteadOfDiscardingThem()
    {
        var tabs = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))],
            quota: null,
            clientId: "codex");

        var tab = Assert.Single(tabs);
        Assert.True(tab.HasHistory);
        Assert.NotNull(tab.Active);
        Assert.Equal(40, tab.Active!.Samples[^1].UsedPercent);
        // Both cards this feeds must see real data, not the false "nothing
        // recorded" claim `selected is null` used to produce upstream.
        Assert.Equal(
            WindowCardState.Chart,
            WindowCardText.State(tab, WindowEquivalence.FetchOutcome.Succeeded));
    }

    // A client with no stored series at all AND no live payload still
    // produces no tab — the fallback surfaces retained data, it does not
    // invent data that was never read.
    [Fact]
    public void ANullQuotaWithNoStoredSeriesStillProducesNoTabs()
    {
        var tabs = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))],
            quota: null,
            clientId: "codex");

        Assert.Empty(tabs);
    }

    // Round 17's finding: a non-null payload is not the same fact as this
    // client's own live windows being usable. Rust's `empty_error_snapshot`
    // (Error set, Windows empty — an identity-verification failure, a
    // terminal fetch failure, or a transient one with no last-good cache to
    // fall back to) must fall back to the stored series the same way a null
    // `quota` already does, or a per-provider failure discards a series this
    // client's own history read had already retrieved successfully.
    [Fact]
    public void AnErrorSnapshotWithNoLiveWindowsFallsBackToTheStoredSeries()
    {
        var quota = new AgentUsagePayload(
            "2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(
                "codex", "oauth", "2026-01-01T00:00:00Z", Windows: [], Error: "Codex could not be reached.")]);

        var tabs = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))],
            quota,
            "codex");

        var tab = Assert.Single(tabs);
        Assert.True(tab.HasHistory);
        Assert.NotNull(tab.Active);
        Assert.Equal(40, tab.Active!.Samples[^1].UsedPercent);
        Assert.Equal(
            WindowCardState.Chart,
            WindowCardText.State(tab, WindowEquivalence.FetchOutcome.Succeeded));
    }

    // The sibling shape that must NOT fall back: a transient failure that DID
    // find a last-good cache entry keeps that entry's non-empty Windows and
    // stamps the new failure's Error onto it as a staleness note. That is
    // real, previously-live data — not the empty-windows placeholder — so the
    // live enumeration must still draw it rather than discarding it for a
    // store fallback that would be a step backward.
    [Fact]
    public void AnErrorSnapshotThatStillCarriesWindowsStaysOnTheLivePath()
    {
        var quota = new AgentUsagePayload(
            "2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(
                "codex", "oauth", "2026-01-01T00:00:00Z",
                Windows: [Window("codex|session.v1", "Session", "session.v1")],
                Error: "Codex could not be reached; showing the last known values.")]);

        var tabs = WindowCardText.Tabs([], quota, "codex");

        var tab = Assert.Single(tabs);
        Assert.Equal("Session", tab.Label);
    }

    // Distinct from both shapes above: this client has no entry in
    // quota.Agents at all (Rust's ProviderFetchOutcome::Absent — no
    // credential found). Deliberately left as pre-existing behaviour: unlike
    // an error snapshot, Absent carries no Error string to swallow, and it
    // does not mean "attempted and failed" — it means "not authenticated for
    // this provider right now". Falling back to old stored tabs here would
    // present retired history as if it were still an active subscription.
    [Fact]
    public void AnAgentAbsentFromThePayloadEntirelyStillProducesNoTabs()
    {
        var quota = new AgentUsagePayload(
            "2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot("claude", "oauth", "2026-01-01T00:00:00Z", Windows: [])]);

        var tabs = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))],
            quota,
            "codex");

        Assert.Empty(tabs);
    }

    // The account dimension (PR #81 structural review, P2): the store can
    // hold two series under one (providerId, windowKey) that differ only in
    // AccountScope — for a provider keyed on an authoritative owner ID, an
    // account switch leaves the previous account's series in place and
    // starts a new one. Joining by WindowKey alone cannot tell them apart, so
    // which one a tab bound to used to be decided by dictionary iteration
    // order (TryAdd, first wins) rather than by which account is actually
    // signed in. The live agent's own resolved HistoryScope is the signal
    // that says which one that is; a series
    // belonging to any other account must be excluded outright, not merely
    // deprioritised — the account-b series is listed FIRST here specifically
    // so a first-wins join would pick the wrong one.
    [Fact]
    public void TabsBindToTheLiveAccountsOwnSeriesNotWhicheverComesFirst()
    {
        var quota = Quota(
            "claude", historyScope: "account-a",
            Window("claude|session.v1", "Session", "session.v1"));

        var tabs = WindowCardText.Tabs(
            [
                // Listed first, and would win under a plain WindowKey-only
                // TryAdd join — but it belongs to the account that is no
                // longer signed in.
                Series("claude", "account-b", "session.v1", Sample(90, ResetAt - 600)),
                Series("claude", "account-a", "session.v1", Sample(40, ResetAt - 600)),
            ],
            quota,
            clientId: "claude");

        var tab = Assert.Single(tabs);
        Assert.Equal("account-a", tab.Id.AccountScope);
        Assert.True(tab.HasHistory);
        // The account-a series' own sample (40%), not account-b's (90%) —
        // pinning that the RIGHT series' data reached the tab, not just that
        // the right scope string was recorded on the identity.
        Assert.NotNull(tab.Active);
        Assert.Equal(40, tab.Active!.Samples[^1].UsedPercent);
    }

    // The mirror case: when the live scope cannot be resolved at all (an
    // AccountScopeStatus.Error, or an absent AccountScope on an older
    // payload), there is no account signal to filter by, so this falls back
    // to the pre-fix first-wins behaviour rather than showing nothing.
    [Fact]
    public void TabsFallBackToFirstWinsWhenTheLiveScopeIsUnavailable()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "account-a", "session.v1", Sample(40, ResetAt - 600)),
                Series("claude", "account-b", "session.v1", Sample(90, ResetAt - 600)),
            ],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            clientId: "claude");

        var tab = Assert.Single(tabs);
        Assert.Equal("account-a", tab.Id.AccountScope);
    }

    // The history-identity split: for a provider with no authoritative owner
    // ID the live AccountScope is the credential lineage, which rotates, and
    // the store is keyed on the HistoryScope, which does not. The two differ
    // on the wire, and the tab must bind the series the writer is recording
    // into — the HistoryScope one — even when a series under the live
    // AccountScope exists too and is listed first.
    [Fact]
    public void TabsBindTheHistoryScopeSeriesWhenItDiffersFromTheAccountScope()
    {
        var quota = new AgentUsagePayload(
            "2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(
                "claude", "oauth", "2026-01-01T00:00:00Z",
                [Window("claude|session.v1", "Session", "session.v1")],
                AccountScope: new AccountScopeStatus(Scope: "lineage-scope"),
                HistoryScope: new AccountScopeStatus(Scope: "history-scope"))]);

        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "lineage-scope", "session.v1", Sample(90, ResetAt - 600)),
                Series("claude", "history-scope", "session.v1", Sample(40, ResetAt - 600)),
            ],
            quota,
            clientId: "claude");

        var tab = Assert.Single(tabs);
        Assert.Equal("history-scope", tab.Id.AccountScope);
        Assert.NotNull(tab.Active);
        Assert.Equal(40, tab.Active!.Samples[^1].UsedPercent);
    }

    // A payload from a producer that predates `historyScope` still carries
    // `accountScope`, and that must NOT be used as the join key any more —
    // it no longer names the series the store records under. With no history
    // scope there is no signal to filter by, so the join is the first-wins
    // fallback: account-a (listed first) wins even though the live
    // AccountScope names account-b.
    [Fact]
    public void APayloadWithoutAHistoryScopeKeepsFirstWinsEvenWithAnAccountScope()
    {
        var quota = new AgentUsagePayload(
            "2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(
                "claude", "oauth", "2026-01-01T00:00:00Z",
                [Window("claude|session.v1", "Session", "session.v1")],
                AccountScope: new AccountScopeStatus(Scope: "account-b"))]);

        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "account-a", "session.v1", Sample(40, ResetAt - 600)),
                Series("claude", "account-b", "session.v1", Sample(90, ResetAt - 600)),
            ],
            quota,
            clientId: "claude");

        var tab = Assert.Single(tabs);
        Assert.Equal("account-a", tab.Id.AccountScope);
        Assert.Equal(40, tab.Active!.Samples[^1].UsedPercent);
    }

    // Round 19's finding: the store-only fallback (no live windows to
    // enumerate) used to join through `byWindowKey` — keyed on WindowKey
    // alone — so two accounts' series sharing one WindowKey collapsed into
    // whichever TryAdd saw first, the same bug already fixed for the live
    // join above but left standing in this second path. There is no live
    // window here to join against at all, so both accounts' distinct
    // history must surface as two tabs, not one chosen by input order.
    [Fact]
    public void FallbackTabsKeepBothAccountsSeriesForTheSameWindowKey()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "account-a", "session.v1", Sample(40, ResetAt - 600)),
                Series("claude", "account-b", "session.v1", Sample(90, ResetAt - 600)),
            ],
            quota: null,
            clientId: "claude");

        Assert.Equal(2, tabs.Count);
        Assert.Equal(
            new[] { "account-a", "account-b" }.OrderBy(s => s),
            tabs.Select(tab => tab.Id.AccountScope).OrderBy(s => s));
        Assert.Contains(tabs, tab => tab.Id.AccountScope == "account-a" && tab.Active!.Samples[^1].UsedPercent == 40);
        Assert.Contains(tabs, tab => tab.Id.AccountScope == "account-b" && tab.Active!.Samples[^1].UsedPercent == 90);
    }

    // The window key is the store's own, and ProviderId is already a registered
    // CLIENT id (QuotaEquivalenceFold states this and relies on it), so the
    // filter is that equality and nothing else.
    [Fact]
    public void TabsSelectOnlyTheSelectedClientsWindows()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "session.v1", Sample(40, ResetAt - 600)),
                Series("claude", "weekly.v1", Sample(12, ResetAt - 600)),
                Series("codex", "main.weekly.v1", Sample(70, ResetAt - 600)),
            ],
            Quota(
                "claude",
                Window("claude|session.v1", "Session", "session.v1"),
                Window("claude|weekly.v1", "Weekly", "weekly.v1")),
            clientId: "claude");

        Assert.Equal(2, tabs.Count);
        Assert.All(tabs, tab => Assert.Equal("claude", tab.Id.ProviderId));
    }

    // Tab order is the provider's own order, never re-sorted — the defect
    // this replaces used to move a running tab to the front, which is what
    // let "每週" jump ahead of "工作階段" whenever the session window had no
    // running cycle. Which tab OPENS first is QuotaLensProjection's decision,
    // not this method's, even when only the weekly window is running.
    [Fact]
    public void TabOrderIsTheProvidersOrderEvenWhenOnlyTheWeeklyWindowIsRunning()
    {
        var tabs = WindowCardText.Tabs(
            [
                Series("claude", "session.v1", Sample(40, ResetAt - 600, active: false)),
                Series("claude", "weekly.v1", Sample(12, ResetAt - 600)),
            ],
            Quota(
                "claude",
                Window("claude|session.v1", "Session", "session.v1"),
                Window("claude|weekly.v1", "Weekly", "weekly.v1")),
            clientId: "claude");

        Assert.Equal(["Session", "Weekly"], tabs.Select(tab => tab.Label));
    }

    // A key with a session-labelled dot COMPONENT is session-class, whether
    // or not it is the first component.
    [Fact]
    public void ADotComponentNamedSessionIsSessionClass() =>
        Assert.True(WindowCardText.IsSessionClass("main.session.v1"));

    // weekly_scoped.fable.v1 is macOS's own worked example of a key that is
    // NOT session-class — its components are "weekly_scoped", "fable", "v1",
    // none of them the exact word "session".
    [Fact]
    public void AWeeklyScopedKeyIsNotSessionClass() =>
        Assert.False(WindowCardText.IsSessionClass("weekly_scoped.fable.v1"));

    // Bars under the quota line are a claim about which subscription paid, so
    // only the user's own confirmed classification admits a message.
    [Fact]
    public void MineAdmitsOnlyMessagesTheUserAssignedToThisClient()
    {
        UsageAttribution.Record[] confirmed =
        [
            new("claude-code", "anthropic", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, "claude")),
            new("codex-cli", "openai", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, "codex")),
        ];

        var mine = WindowCardText.Mine(
            [
                Message("claude-code", "anthropic", "sonnet"),
                Message("codex-cli", "openai", "gpt-5"),
                Message("other", "other", "model"),
            ],
            "claude",
            confirmed);

        Assert.Single(mine);
        Assert.Equal("claude-code", mine[0].Client);
    }

    [Fact]
    public void MineAdmitsNothingWhileNothingIsDeclared() =>
        Assert.Empty(WindowCardText.Mine(
            [Message("claude-code", "anthropic", "sonnet")], "claude", []));

    // Round-3 P1: antigravity-cli spends the "antigravity" subscription
    // (ClientRegistry.QuotaOwner), so `Tabs` and `Mine` — every
    // subscription-facing lookup on the per-client lens — must be keyed by
    // that owner, not the raw client id, or the whole card renders empty for
    // that client even though it consumes the subscription.
    [Fact]
    public void TabsAndMineResolveAgainstTheOwnerForAClientWhoseOwnerDiffersFromItsId()
    {
        var owner = ClientRegistry.QuotaOwner("antigravity-cli");
        Assert.Equal("antigravity", owner);

        var quota = Quota(owner, Window("antigravity|session.v1", "Session", "session.v1"));
        var byOwner = WindowCardText.Tabs(
            [Series(owner, "session.v1", Sample(40, ResetAt - 600))], quota, owner);
        Assert.Single(byOwner);
        Assert.Equal("Session", byOwner[0].Label);

        // The bug: the raw id finds neither the live agent nor the stored
        // series, so the tab list — and with it the whole card — is empty.
        var byRawId = WindowCardText.Tabs(
            [Series(owner, "session.v1", Sample(40, ResetAt - 600))], quota, "antigravity-cli");
        Assert.Empty(byRawId);

        UsageAttribution.Record[] confirmed =
        [
            new("antigravity-cli", "antigravity", null,
                new UsageAttribution.State(UsageAttribution.StateKind.Assigned, owner)),
        ];
        var messages = new[] { Message("antigravity-cli", "antigravity", "model") };
        Assert.Single(WindowCardText.Mine(messages, owner, confirmed));
        Assert.Empty(WindowCardText.Mine(messages, "antigravity-cli", confirmed));
    }

    // A client whose owner equals its own id must behave identically either
    // way — the fix must not introduce a second id space for clients that
    // never had one.
    [Fact]
    public void TabsAndMineAreUnaffectedWhenTheOwnerEqualsTheRawClientId()
    {
        var owner = ClientRegistry.QuotaOwner("codex");
        Assert.Equal("codex", owner);

        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));
        var byOwner = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))], quota, owner);
        var byRawId = WindowCardText.Tabs(
            [Series("codex", "session.v1", Sample(40, ResetAt - 600))], quota, "codex");
        Assert.Single(byOwner);
        Assert.Single(byRawId);
        Assert.Equal(byOwner[0].Label, byRawId[0].Label);
    }

    // ---- the no-sample series ------------------------------------------

    // "無樣本" is a rendered series, not an absence. The stretch with no reading
    // is produced as its own region so the chart can DRAW it: a gap would say
    // "nothing happened here" while the fact is "we did not look here".
    [Fact]
    public void TheNoSampleStretchesAreProducedAsRegionsRatherThanLeftAsGaps()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(
            start, end, start + 3_600_000,
            [new QuotaSample(start + 1_200_000, 10), new QuotaSample(start + 2_400_000, 20)],
            [],
            QuotaMetric.Used);

        var regions = WindowCardText.NoSampleRegions(chart);

        // Two: the stretch before the first reading, and the future.
        Assert.Equal(2, regions.Count);
        Assert.Equal(0, regions[0].From);
        Assert.Equal(chart.FirstSampleX, regions[0].To);
        Assert.Equal(chart.NowX, regions[1].From);
        Assert.Equal(1, regions[1].To);
    }

    // A window sampled from its first millisecond has no leading region, and a
    // zero-width hatch is a rendering artefact rather than a statement.
    [Fact]
    public void AZeroWidthNoSampleStretchIsNotDrawn()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(
            start, end, end,
            [new QuotaSample(start, 5), new QuotaSample(end, 60)],
            [],
            QuotaMetric.Used);

        Assert.Empty(WindowCardText.NoSampleRegions(chart));
    }

    // No reading at all leaves the WHOLE box hatched, not half of it: the app
    // was not running to sample any of this window.
    [Fact]
    public void AWindowWithNoReadingIsHatchedEndToEnd()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        var chart = WindowCardGeometry.Chart(start, end, start + 600_000, [], [], QuotaMetric.Used);

        // Two abutting regions rather than one — the leading stretch runs up to
        // `now`, where the future takes over — and between them they cover the
        // whole box with no gap. The count is an implementation detail; the
        // coverage is the claim.
        var regions = WindowCardText.NoSampleRegions(chart);
        Assert.Equal(0, regions[0].From);
        Assert.Equal(1, regions[^1].To);
        for (var i = 1; i < regions.Count; i++)
        {
            Assert.Equal(regions[i - 1].To, regions[i].From);
        }
    }

    // ---- states ---------------------------------------------------------

    [Fact]
    public void EveryStateIsDistinct()
    {
        var running = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var idle = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, active: false))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var unplaceable = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600, duration: 0))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var noHistory = WindowCardText.Tabs(
            [], Quota("claude", Window("claude|session.v1", "Session", "session.v1")), "claude")[0];

        Assert.Equal(
            WindowCardState.Chart, WindowCardText.State(running, WindowEquivalence.FetchOutcome.Succeeded));
        Assert.Equal(
            WindowCardState.Idle, WindowCardText.State(idle, WindowEquivalence.FetchOutcome.Succeeded));
        Assert.Equal(
            WindowCardState.Unplaceable,
            WindowCardText.State(unplaceable, WindowEquivalence.FetchOutcome.Succeeded));
        // The pair that differs only in `outcome`, which is the whole point:
        // a lazy lens fetches on first visit, so no tab before the read has
        // settled is the first paint of every cold start.
        Assert.Equal(
            WindowCardState.NoQuotaHistory,
            WindowCardText.State(null, WindowEquivalence.FetchOutcome.Succeeded));
        Assert.Equal(
            WindowCardState.Loading, WindowCardText.State(null, WindowEquivalence.FetchOutcome.NotAttempted));
        // Round 8: a fetch that threw is neither of the above — it must not
        // render as "no history" (which claims the read landed and found
        // nothing) nor stay stuck on "loading" (which claims no answer has
        // come back at all).
        Assert.Equal(
            WindowCardState.HistoryFetchFailed,
            WindowCardText.State(null, WindowEquivalence.FetchOutcome.Failed));
        // A live window with a tab but no matched series reads the same as no
        // tab at all — `HasHistory` is what carries the distinction from
        // `Idle`, not the tab's mere presence.
        Assert.Equal(
            WindowCardState.NoQuotaHistory,
            WindowCardText.State(noHistory, WindowEquivalence.FetchOutcome.Succeeded));
        Assert.Equal(
            WindowCardState.HistoryFetchFailed,
            WindowCardText.State(noHistory, WindowEquivalence.FetchOutcome.Failed));
    }

    // Each empty state has its own sentence. "The window ended" and "the
    // provider gave no reset time" are different facts, and one shared line
    // would report the second as the first — a claim that the user stopped
    // working.
    [Fact]
    public void EveryEmptyStateSaysSomethingDifferent()
    {
        var bodies = new[]
        {
            WindowCardState.Loading,
            WindowCardState.NoQuotaHistory,
            WindowCardState.Idle,
            WindowCardState.Unplaceable,
        }.Select(WindowCardText.EmptyBody).ToList();

        Assert.Equal(bodies.Count, bodies.Distinct().Count());
    }

    [Fact]
    public void TheHeadlineReadsTheLastSampleInTheWindowAndNamesItsDirection()
    {
        var start = 1_000_000L;
        var end = start + (FiveHours * 1000);
        QuotaSample[] samples = [new(start + 600_000, 10), new(start + 1_200_000, 42)];

        var used = WindowCardText.Headline(
            WindowCardGeometry.Chart(
                start, end, start + 1_800_000, samples, [], QuotaMetric.Used),
            QuotaMetric.Used);
        Assert.Equal("42%", used.Percent);
        Assert.Equal("used", used.Caption);

        var remaining = WindowCardText.Headline(
            WindowCardGeometry.Chart(
                start, end, start + 1_800_000, samples, [], QuotaMetric.Remaining),
            QuotaMetric.Remaining);
        Assert.Equal("58%", remaining.Percent);
        Assert.Equal("remaining", remaining.Caption);
    }

    // A window with a placed axis and no reading in it still draws — and says
    // why the big number is missing rather than printing a zero nobody
    // measured.
    [Fact]
    public void NoReadingInTheWindowHasNoPercentAndSaysSo()
    {
        var start = 1_000_000L;
        var chart = WindowCardGeometry.Chart(
            start, start + (FiveHours * 1000), start + 600_000, [], [], QuotaMetric.Used);

        var headline = WindowCardText.Headline(chart, QuotaMetric.Used);
        Assert.Null(headline.Percent);
        Assert.Equal("No quota reading in this window", headline.Caption);
    }

    // A zone in the hatch has no closing reading, and must say that rather than
    // show nothing.
    [Fact]
    public void AZoneWithNoClosingReadingSaysThereWasNoReading()
    {
        var start = 1_000_000L;
        var zones = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)]);

        Assert.Equal("Quota 10% used", WindowCardText.ZoneQuota(zones[0], QuotaMetric.Used));
        Assert.Equal(
            "No quota reading in this interval",
            WindowCardText.ZoneQuota(zones[^1], QuotaMetric.Used));
        // Absent rather than zero: an interval nobody measured is not an
        // interval that consumed nothing.
        Assert.Null(WindowCardText.ZoneConsumed(zones[0], QuotaMetric.Used));
    }

    [Fact]
    public void AZoneWithNoMessagesSaysSoRatherThanPrintingZeroTokens()
    {
        var start = 1_000_000L;
        var zone = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 600_000, [])[0];

        var empty = WindowCardText.ZoneUsage(WindowCardText.InZone([], zone));
        Assert.Equal("No usage in this interval", empty.Empty);
        Assert.Null(empty.Tokens);

        var used = WindowCardText.ZoneUsage(
            WindowCardText.InZone([Message("claude-code", "anthropic", "sonnet", start)], zone));
        Assert.Null(used.Empty);
        Assert.Equal("100 tokens", used.Tokens);
    }

    // Zone 0 owns its own lower bound, matching the bar built from the same
    // message — otherwise the bar and the tooltip explaining it disagree about
    // whether the message is in the interval.
    [Fact]
    public void TheTooltipAndTheBarAgreeAboutAMessageOnTheWindowStart()
    {
        var start = 1_000_000L;
        var zones = WindowCardGeometry.Zones(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)]);

        Assert.Single(WindowCardText.InZone(
            [Message("claude-code", "anthropic", "sonnet", start)], zones[0]));
        Assert.Empty(WindowCardText.InZone(
            [Message("claude-code", "anthropic", "sonnet", start)], zones[1]));
    }

    [Fact]
    public void UndatedRowsAreStatedRatherThanSwallowed()
    {
        Assert.Null(WindowCardText.UndatedNote(0));
        Assert.Contains("3", WindowCardText.UndatedNote(3));
    }

    // ---- i18n ------------------------------------------------------------
    //
    // Against the *shipped* strings-zh-Hant.json, for the reason QuotaLensText's
    // own i18n test states: the failure guarded against is a Localized() call
    // site whose key was never added to that file, and each card shows one state
    // at a time, so only driving every branch can prove the entries exist.
    [Fact]
    public void EveryStringTheCardCanShowHasATableEntry()
    {
        var tab = WindowCardText.Tabs(
            [Series("claude", "session.v1", Sample(40, ResetAt - 600))],
            Quota("claude", Window("claude|session.v1", "Session", "session.v1")),
            "claude")[0];
        var start = 1_000_000L;
        var chart = WindowCardGeometry.Chart(
            start, start + (FiveHours * 1000), start + 1_800_000,
            [new QuotaSample(start + 600_000, 10)], [], QuotaMetric.Used);
        var zones = chart.Hits;

        Func<string?>[] surfaces =
        [
            () => WindowCardText.Title(null),
            () => WindowCardText.Title(tab),
            () => WindowCardText.Subtitle(WindowCardState.Loading, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.NoQuotaHistory, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.HistoryFetchFailed, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.Idle, null, DateTimeOffset.Now),
            () => WindowCardText.Subtitle(WindowCardState.Unplaceable, null, DateTimeOffset.Now),
            () => WindowCardText.EmptyBody(WindowCardState.Loading),
            () => WindowCardText.EmptyBody(WindowCardState.NoQuotaHistory),
            () => WindowCardText.EmptyBody(WindowCardState.HistoryFetchFailed),
            () => WindowCardText.EmptyBody(WindowCardState.Idle),
            () => WindowCardText.EmptyBody(WindowCardState.Unplaceable),
            () => WindowCardText.Headline(chart, QuotaMetric.Used).Caption,
            () => WindowCardText.Headline(chart, QuotaMetric.Remaining).Caption,
            () => WindowCardText.Headline(
                WindowCardGeometry.Chart(
                    start, start + (FiveHours * 1000), start + 600_000, [], [], QuotaMetric.Used),
                QuotaMetric.Used).Caption,
            WindowCardText.QuotaKey,
            WindowCardText.UsageKey,
            WindowCardText.NoSampleKey,
            () => WindowCardText.Readings(4),
            () => WindowCardText.MetricLabel(QuotaMetric.Used),
            () => WindowCardText.MetricLabel(QuotaMetric.Remaining),
            () => WindowCardText.UndatedNote(3),
            () => WindowCardText.ZoneQuota(zones[^1], QuotaMetric.Used),
            () => WindowCardText.ZoneQuota(zones[0], QuotaMetric.Used),
            () => WindowCardText.ZoneConsumed(
                new HitZone(1, 0, 1, 0, 0.1,
                    new QuotaSample(1, 30), new QuotaSample(0, 10)), QuotaMetric.Used),
            () => WindowCardText.ZoneUsage([]).Empty,
        ];

        var english = surfaces.Select(surface => surface()).ToList();
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.NotEqual(english[i], surfaces[i]());
            }

            // Deliberately outside the loop above: the shipped zh-Hant value for
            // "%@ tokens" is "{0} tokens" — the unit is not translated — so an
            // inequality assertion would fail on a correct entry. Asserted as a
            // lookup that RESOLVES instead, which is the property that matters.
            Assert.Equal("100 tokens", WindowCardText.ZoneUsage(
                [Message("claude-code", "anthropic", "sonnet", 5)]).Tokens);
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // ---- LiveEquivalence ---------------------------------------------------

    // Round-4 P2: BuildWindowCard's Chart state stopped after the chart and
    // legend — WindowEquivalence.LiveRow had no caller outside tests, so the
    // card never printed the "10% of quota ~ ..." line macOS shows. This pins
    // the wiring: QuotaSample (the chart's own reading type) converts to
    // WindowEquivalence.Sample and reaches LiveRow, using the samples and
    // attributed messages the card already has in hand.
    [Fact]
    public void LiveEquivalenceConvertsQuotaSamplesAndReachesARatio()
    {
        QuotaSample[] samples = [new(0, 10), new(1000, 30)];
        WindowMessage[] mine = [new(500, "claude-code", "anthropic", "sonnet", 1_000, 0, 0, 0, 0, 2.0, true)];

        var row = WindowCardText.LiveEquivalence(samples, mine, declared: true, attempt: WindowEquivalence.FetchOutcome.Succeeded);

        var ratio = Assert.IsType<WindowEquivalence.Row.Ratio>(row);
        Assert.Equal(500, ratio.TokensPerTenth);
        Assert.Equal(1.0, ratio.CostPerTenth);
    }

    // The card must render EVERY Row case, not only Ratio — the reasons-
    // there-is-no-number cases are the point of the type. A dashboard that
    // special-cased Ratio (`row is Ratio r ? ... : nothing`) would pass a test
    // that only ever fed it a Ratio; this theory feeds LiveEquivalence one
    // input per reachable case and pins that each survives the conversion
    // instead of collapsing to Unavailable or Undeclared.
    [Theory]
    [MemberData(nameof(LiveEquivalenceCases))]
    public void LiveEquivalenceReachesEveryRowCaseTheChartCanProduce(
        QuotaSample[] samples, WindowMessage[] mine, Type expectedRowType)
    {
        var row = WindowCardText.LiveEquivalence(samples, mine, declared: true, attempt: WindowEquivalence.FetchOutcome.Succeeded);
        Assert.IsType(expectedRowType, row);

        // The strip card's own renderer must accept whatever comes back —
        // Text() throws on an unhandled case, so this also guards that no
        // reachable LiveRow case was left unmapped there.
        Assert.NotEmpty(WindowEquivalenceText.Line(row));
    }

    // Round-5 P2: the Session-window card's own live equivalence line was the
    // THIRD call site to reach WindowEquivalence's evidence rules, and it was
    // wired straight to LiveRow's samples/messages with no `declared` or
    // `attempted` in sight — the same unclassified corpus the fold
    // (QuotaEquivalenceFoldTests) and the history card
    // (WindowHistoryTextTests) already pin to Undeclared read as Unaccounted
    // here instead, because an empty `mine` list from "nothing classified"
    // and an empty list from "quota moved, nothing recorded" are the same
    // list. `declared` and `attempted` are now required parameters of
    // LiveEquivalence itself — this pins that they are actually respected,
    // not merely accepted.
    [Fact]
    public void LiveEquivalenceIsUndeclaredNotUnaccountedForAnUnclassifiedCorpus()
    {
        QuotaSample[] samples = [new(0, 10), new(1000, 30)];

        // Plenty of quota movement and an empty `mine` — exactly what an
        // unclassified machine hands this call site (WindowCardText.Mine
        // filters every message out when nothing is declared).
        var row = WindowCardText.LiveEquivalence(samples, [], declared: false, attempt: WindowEquivalence.FetchOutcome.Succeeded);

        Assert.IsType<WindowEquivalence.Row.Undeclared>(row);
    }

    // The other state the same defect collapsed into Unaccounted: the
    // window-usage export has not landed yet, so `mine` is empty because
    // nothing has been scanned, not because nothing was found.
    [Fact]
    public void LiveEquivalenceIsLoadingNotUnaccountedWhenNotYetAttempted()
    {
        QuotaSample[] samples = [new(0, 10), new(1000, 30)];

        var row = WindowCardText.LiveEquivalence(samples, [], declared: true, attempt: WindowEquivalence.FetchOutcome.NotAttempted);

        Assert.IsType<WindowEquivalence.Row.Loading>(row);
    }

    // The third state the old bool collapsed: the window-usage fetch was
    // attempted and threw (DashboardModel.FetchLazyWanted's TryFetch
    // swallows the exception and still publishes completion), so `mine` is
    // empty because nothing was read, not because the scan found nothing.
    [Fact]
    public void LiveEquivalenceIsScanFailedNotUnaccountedWhenTheFetchThrew()
    {
        QuotaSample[] samples = [new(0, 10), new(1000, 30)];

        var row = WindowCardText.LiveEquivalence(samples, [], declared: true, attempt: WindowEquivalence.FetchOutcome.Failed);

        Assert.IsType<WindowEquivalence.Row.ScanFailed>(row);
    }

    public static TheoryData<QuotaSample[], WindowMessage[], Type> LiveEquivalenceCases()
    {
        var data = new TheoryData<QuotaSample[], WindowMessage[], Type>();

        // Unavailable: fewer than two readings.
        data.Add([new(0, 10)], [], typeof(WindowEquivalence.Row.Unavailable));

        // NotMoved: two readings, no delta.
        data.Add([new(0, 10), new(1000, 10)], [], typeof(WindowEquivalence.Row.NotMoved));

        // Unaccounted: quota moved, nothing attributed inside the span.
        data.Add([new(0, 10), new(1000, 30)], [], typeof(WindowEquivalence.Row.Unaccounted));

        // Insufficient: quota moved, but by less than MinimumDelta (5 points).
        data.Add(
            [new(0, 10), new(1000, 12)],
            [new WindowMessage(500, "claude-code", "anthropic", "sonnet", 100, 0, 0, 0, 0, 0.1, true)],
            typeof(WindowEquivalence.Row.Insufficient));

        // TokensOnly: enough delta and tokens, no priced cost.
        data.Add(
            [new(0, 10), new(1000, 30)],
            [new WindowMessage(500, "claude-code", "anthropic", "sonnet", 1_000, 0, 0, 0, 0, 0, true)],
            typeof(WindowEquivalence.Row.TokensOnly));

        // CostOnly: enough delta and cost, no counted tokens.
        data.Add(
            [new(0, 10), new(1000, 30)],
            [new WindowMessage(500, "claude-code", "anthropic", "sonnet", 0, 0, 0, 0, 0, 2.0, true)],
            typeof(WindowEquivalence.Row.CostOnly));

        // Ratio: enough delta, both tokens and cost present.
        data.Add(
            [new(0, 10), new(1000, 30)],
            [new WindowMessage(500, "claude-code", "anthropic", "sonnet", 1_000, 0, 0, 0, 0, 2.0, true)],
            typeof(WindowEquivalence.Row.Ratio));

        return data;
    }
}
