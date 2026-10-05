using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The Quota lens's seven assembly sites, folded into
/// <see cref="QuotaLensProjection"/> and asserted here for the first time —
/// <c>DashboardView.Quota.cs</c>, where they used to live, is compiled by no
/// test project. Four of these tests pin round 7's findings: one reading of
/// the fetch outcome instead of three (finding 1), the retained-stale-data
/// signal (finding 2), and the January cross-year window (finding 4);
/// finding 3 is a Rust-side fix asserted in <c>window_usage.rs</c>.
/// </summary>
public class QuotaLensProjectionTests
{
    private const long FiveHours = 5 * 3_600;

    public QuotaLensProjectionTests() => Localization.Load("en", AppContext.BaseDirectory);

    // ---- fixtures ---------------------------------------------------------

    private static QuotaHistorySample Sample(
        double usedPercent, long sampledAt, long resetAt, bool active = true, long duration = FiveHours) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: duration,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: active);

    private static QuotaHistorySeries Series(
        string providerId, string accountScope, string windowKey, params QuotaHistorySample[] samples) =>
        new(providerId, accountScope, windowKey, samples);

    private static WindowMessage Message(long timestampMs, string client, string provider, long tokens, double cost) =>
        new(timestampMs, client, provider, "some-model", tokens, 0, 0, 0, 0, cost, true);

    private static UsageAttribution.Table Confirmed(params UsageAttribution.Record[] records) =>
        new(records, IsWritable: true);

    private static UsagePayload EmptyGraph() =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            []);

    private static UsageWindow Window(string cardId, string label, string? windowKey) =>
        Window(cardId, label, windowKey, remainingPercent: 90);

    private static UsageWindow Window(
        string cardId, string label, string? windowKey, double remainingPercent) =>
        new(
            Label: label,
            UsedPercent: 100 - remainingPercent,
            RemainingPercent: remainingPercent,
            CardId: cardId,
            PaceStatus: windowKey is null
                ? new PaceStatus(UsagePaceState.Unavailable)
                : new PaceStatus(UsagePaceState.Available, WindowKey: windowKey));

    private static AgentUsagePayload Quota(string clientId, params UsageWindow[] windows) =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot(clientId, "source", "2026-01-01T00:00:00Z", windows)]);

    // A payload with a codex primary whose history scope matches
    // TwoCycleSeries("codex", "primary", ...), so the series passes the
    // overview's visible-agents filter and the equivalence tests below fold
    // it the way production does once a payload has arrived.
    private static AgentUsagePayload CodexPrimaryQuota() =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot("codex", "source", "2026-01-01T00:00:00Z",
                [new UsageWindow(Label: "Weekly", UsedPercent: 10, RemainingPercent: 90, CardId: "codex|weekly.v1",
                    PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: "weekly.v1"))],
                HistoryScope: new AccountScopeStatus("primary"))]);

    // Two completed (single-sample) cycles, plus a running cycle carrying TWO
    // active samples — WindowEquivalence.LiveRow needs a non-empty
    // [first, last] span to admit a message as "declared" at all before it
    // ever looks at the fetch outcome, so a single active sample (whose span
    // is a single instant) cannot exercise that branch. These tests only
    // assert WHICH branch ran, never a computed ratio.
    private static QuotaHistorySeries TwoCycleSeries(string providerId, string scope, string windowKey) =>
        Series(
            providerId, scope, windowKey,
            Sample(40, 1_500, 2_000, active: false),
            Sample(70, 3_500, 4_000, active: false),
            Sample(10, 5_000, 6_000, active: true),
            Sample(15, 5_900, 6_000, active: true));

    // Inside the running cycle's own sample span (5_000_000..5_900_000ms, per
    // TwoCycleSeries) so WindowEquivalence.LiveRow can see it as evidence.
    private const long InActiveSpanMs = 5_200_000;

    /// <summary>A completed cycle per sample, spaced far enough apart that
    /// QuotaHistoryFold cannot fold two into one — enough of them to reach
    /// past the history card's opening row count.</summary>
    private static QuotaHistorySeries ManyCycleSeries(
        string providerId, string scope, string windowKey, int cycles) =>
        Series(
            providerId, scope, windowKey,
            [.. Enumerable.Range(1, cycles).Select(i =>
                Sample(40, (i * 2 * FiveHours * 1_000) - 1_000, i * 2 * FiveHours * 1_000, active: false))]);

    /// <summary>
    /// Like <see cref="ManyCycleSeries"/> but each cycle carries TWO samples
    /// that rise, so it has a non-empty <c>(first, last]</c> span and a real
    /// <c>DeltaPercent</c>. A one-sample cycle is degenerate — first == last,
    /// delta and observed fraction both exactly zero — which
    /// <c>WindowEquivalence.Aggregate</c> treats as "no reliable evidence" and
    /// short-circuits before it ever consults <c>declared</c>. Reaching that
    /// flag's branch at all requires cycles shaped like these.
    /// </summary>
    private static QuotaHistorySeries RisingCycleSeries(
        string providerId, string scope, string windowKey, int cycles) =>
        Series(
            providerId, scope, windowKey,
            [.. Enumerable.Range(1, cycles).SelectMany(i =>
            {
                var resetAt = i * 2 * FiveHours;
                return new[]
                {
                    Sample(10, resetAt - FiveHours, resetAt, active: false),
                    Sample(70, resetAt - 60, resetAt, active: false),
                };
            })]);

    /// <summary>A millisecond inside cycle <paramref name="index"/>'s sample
    /// span, as <see cref="RisingCycleSeries"/> lays it out. Cycles are
    /// 1-based and ascend in time, so index 1 is the OLDEST — the one the
    /// history card hides first.</summary>
    private static long InCycleSpanMs(int index) =>
        (((index * 2 * FiveHours) - FiveHours) + 60) * 1_000;

    // ---- finding 1: one reading of the fetch outcome, not three -----------

    // Round 7's first finding: the overview path used to gate its equivalence
    // fold on the plain WindowUsageAttempted bool while the live and history
    // cards already switched on WindowUsageOutcome. `quotaHistoryOutcome`
    // is deliberately Succeeded here while `windowUsageOutcome` is Failed —
    // the exact combination the old boolean gate could not tell apart from a
    // genuine success. If site 1 still read a plain boolean, this dictionary
    // would come back populated.
    [Fact]
    public void OverviewEquivalencesAreEmptyWhenTheFetchFailedEvenThoughHistoryHasAttempted()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        var messages = new[] { Message(1_800, "codex", "openai", 1000, 5.0) };

        var model = QuotaLensProjection.Build(
            history,
            quota: CodexPrimaryQuota(),
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Failed,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty, PresentClients: ["codex"]));

        Assert.Empty(model.Overview.Equivalences);
    }

    [Fact]
    public void OverviewEquivalencesArePopulatedOnceTheFetchSucceeds()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        var messages = new[] { Message(1_800, "codex", "openai", 1000, 5.0) };

        var model = QuotaLensProjection.Build(
            history,
            quota: CodexPrimaryQuota(),
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty, PresentClients: ["codex"]));

        // QuotaEquivalenceFold.Build inserts one row per series regardless of
        // how much evidence it carries — the row's own shape (Ratio,
        // Unavailable, ...) is QuotaEquivalenceFold's own contract, already
        // asserted in QuotaEquivalenceFoldTests. What this test pins is that
        // the projection actually calls it once the outcome is Succeeded.
        Assert.Single(model.Overview.Equivalences);
    }

    // Before the first payload the overview draws retained series (a
    // maintainer-approved deviation) but prices none of them: without a
    // payload nothing can be narrowed to its account or model scope, so an
    // estimate would come from the whole unscoped scan. Same inputs as the
    // test above, with no payload.
    [Fact]
    public void OverviewEquivalencesWaitForThePayloadEvenWhenRowsAreDrawn()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        var messages = new[] { Message(1_800, "codex", "openai", 1000, 5.0) };

        var model = QuotaLensProjection.Build(
            history,
            quota: null,
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty, PresentClients: ["codex"]));

        Assert.NotEmpty(model.Overview.Summaries);
        Assert.Empty(model.Overview.Equivalences);
    }

    // ---- retained data renders as data, not as a failure ------------------

    // Formerly "FailedOutcomeIsNotReadAsSuccessAtAnyOfTheThreeSitesEvenWithMessagesRetained",
    // pinning the opposite rule: a Failed outcome, retaining stale messages
    // from an earlier successful fetch, still rendered ScanFailed rather than
    // a ratio computed from those messages. That rule inverted with
    // DashboardModel.Snapshot.WindowUsageOutcome (see its own doc comment):
    // DashboardModel never nulls WindowUsage out on a failed refetch, it only
    // ever retains, and WindowUsageOutcome is now derived purely from
    // `WindowUsage is null` — so once any earlier fetch has succeeded, this
    // lane can no longer produce Failed while messages are retained; the
    // combination this test used to construct directly cannot occur upstream
    // any more. What DOES occur on a failed refresh that retains data is
    // Succeeded with those same (possibly stale) messages — the exact
    // scenario `DashboardView.Quota.cs`'s Session-window card already lived
    // in: it draws its bars from `client.Mine` (built from these same
    // retained messages, unconditionally) while the line directly beneath
    // printed `client.LiveEquivalence`, which under the old rule was
    // `Row.ScanFailed` — bars drawn from data the line under them said could
    // not be read. This test pins the fix: retained messages render as data
    // at all three equivalence sites, because there is no longer a way to
    // tell a fresh success from a failed retry that retained good data, and
    // no card anywhere carries a staleness marker to hedge with.
    [Fact]
    public void RetainedMessagesRenderAsDataAtAllThreeSitesRatherThanScanFailed()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };
        // Stale-looking messages: present, and would otherwise produce a real
        // ratio — proving the branch below is not simply "no messages, so no
        // line".
        var retainedMessages = new[]
        {
            Message(InActiveSpanMs, "codex", "openai", 5_000, 25.0),
        };
        var confirmed = Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));
        // A live window for "codex" so the client lens resolves an active
        // tab and site 4 (the live card) has something to test at all.
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            history, quota, EmptyGraph(),
            windowUsage: new WindowUsage(retainedMessages, 0, 0),
            // Succeeded: the only outcome DashboardModel can now report once
            // WindowUsage is non-null, whether this pass's own fetch just
            // landed or just failed and fell back to what was already there.
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded, confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        // Site 1 (overview): a row computed from the retained messages.
        Assert.NotEmpty(model.Overview.Equivalences);

        // Site 4 (live card): a ratio computed from the retained messages,
        // not ScanFailed.
        Assert.NotNull(model.Client);
        Assert.IsNotType<WindowEquivalence.Row.ScanFailed>(model.Client!.LiveEquivalence);

        // Site 6 (history card): same rendering, same reason.
        Assert.IsNotType<WindowEquivalence.Row.ScanFailed>(model.Client.History.Equivalence);
    }

    // ---- finding 4: the trend window's own range, not the calendar year ---

    [Fact]
    public void CurrentYearSelectedEarlyInJanuaryIsFlaggedBecauseTheWindowCrossesIntoDecember()
    {
        // The 14-day window ending 2026-01-05 is [2025-12-23, 2026-01-05] —
        // it reaches into the year before, which "2026" selected does not
        // cover. The old check (selectedYear != today's calendar year) missed
        // this exactly because "2026" IS today's calendar year.
        Assert.True(QuotaLensProjection.PastYearSelected("2026-01-05", "2026"));
    }

    [Fact]
    public void CurrentYearSelectedMidYearIsNotFlagged()
    {
        // The window [2026-02-19, 2026-03-05] does not cross a year boundary.
        Assert.False(QuotaLensProjection.PastYearSelected("2026-03-05", "2026"));
    }

    [Fact]
    public void AllTimeSelectionIsNeverFlagged()
    {
        Assert.False(QuotaLensProjection.PastYearSelected("2026-01-05", year: null));
    }

    [Fact]
    public void AGenuinelyPastYearIsStillFlagged()
    {
        // The window is entirely inside 2026, which never overlaps "2024" —
        // the case the old check already handled; still true under the new
        // range-based check.
        Assert.True(QuotaLensProjection.PastYearSelected("2026-03-05", "2024"));
    }

    // ---- round 9 finding 1: a failed quota-history read renders as failed,
    // not as loading or as empty -------------------------------------------

    // Round 9's first finding: StripState/HeatmapState used to take a plain
    // `bool attempted`, so a fetch that ATTEMPTED and FAILED read identically
    // to one that succeeded and found nothing (NoMovement/NoCompletedWindows)
    // — the strip and heatmap cards had no way to tell "we asked and it
    // threw" from "we asked and there was nothing". Overview.Outcome now
    // carries the FULL three-value outcome through, and this pins that the
    // projection does not collapse Failed back down to a bool anywhere on the
    // way.
    [Fact]
    public void OverviewOutcomeCarriesAFailedQuotaHistoryReadThrough()
    {
        var model = QuotaLensProjection.Build(
            history: null, quota: null, EmptyGraph(), windowUsage: null,
            windowUsageOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Failed,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        Assert.Equal(WindowEquivalence.FetchOutcome.Failed, model.Overview.Outcome);
    }

    // Round 9's second finding: DashboardView.Quota.cs's window card and
    // history card read snapshot.QuotaHistoryOutcome directly, bypassing the
    // projection entirely, because QuotaLensProjection.Client carried no such
    // field. This pins that the field now exists and actually carries the
    // outcome passed into Build — the fact a caller reading `client.
    // QuotaHistoryOutcome` instead of `snapshot.QuotaHistoryOutcome` depends
    // on.
    [Fact]
    public void ClientCarriesTheQuotaHistoryOutcomeSoTheViewNeverHasToReadThePassedSnapshot()
    {
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            history: null, quota, EmptyGraph(), windowUsage: null,
            windowUsageOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Failed,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.NotNull(model.Client);
        Assert.Equal(WindowEquivalence.FetchOutcome.Failed, model.Client!.QuotaHistoryOutcome);
    }

    // Codex round 16, finding 2: `quota` null (the agent-usage fetch pending
    // or failed) while `history` holds this client's own successfully-read
    // series used to make `Tabs` return no candidates, so `Selected` went
    // null and both the Session-window and Window-history cards claimed
    // there was nothing recorded — discarding data `history` had already
    // retrieved. `Selected` (and its history) must survive a null `quota`.
    [Fact]
    public void SelectedTabSurvivesANullQuotaWhenHistoryHasThisClientsSeries()
    {
        var history = new[] { TwoCycleSeries("codex", "primary", "weekly.v1") };

        var model = QuotaLensProjection.Build(
            history, quota: null, EmptyGraph(), windowUsage: null,
            windowUsageOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty),
            quotaAttempted: false); // attempted + an ended cycle would block (Q39)

        Assert.NotNull(model.Client);
        Assert.NotEmpty(model.Client!.Tabs);
        Assert.NotNull(model.Client.Selected);
        Assert.True(model.Client.Selected!.HasHistory);
        Assert.NotEmpty(model.Client.History.Cycles);
    }

    // ---- the seven sites, generally ---------------------------------------

    [Fact]
    public void OverviewTabBuildsNoClientLens()
    {
        var model = QuotaLensProjection.Build(
            [], quota: null, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        Assert.Null(model.Client);
    }

    // Site 2: the owner-keyed lookup (antigravity-cli spends the antigravity
    // subscription) and the persisted tab selection both land in the same
    // place, so a future card cannot read one without the other.
    [Fact]
    public void ClientTabResolvesToItsQuotaOwnerAndHonoursThePersistedTabSelection()
    {
        var weekly = TwoCycleSeries("antigravity", "primary", "weekly.v1");
        var session = TwoCycleSeries("antigravity", "primary", "session.v1");
        var quota = Quota(
            "antigravity",
            Window("antigravity|weekly.v1", "Weekly", "weekly.v1"),
            Window("antigravity|session.v1", "Session", "session.v1"));

        var model = QuotaLensProjection.Build(
            [weekly, session], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            // antigravity-cli, the raw client id — ClientRegistry.QuotaOwner
            // maps it to "antigravity", which is what Tabs() must be called
            // with or the store-side series above never match.
            new QuotaLensProjection.Selection("antigravity-cli", "antigravity|primary|session.v1"));

        Assert.NotNull(model.Client);
        Assert.Equal("antigravity", model.Client!.Owner);
        Assert.Equal(2, model.Client.Tabs.Count);
        Assert.Equal("session.v1", model.Client.Selected?.Id.WindowKey);
    }

    // ---- default tab selection (the tab-order bug this fix replaces) ------

    // The bug report itself: on a client with a session and a weekly window,
    // when the session window has no running cycle, the session tab must
    // still open first — not the weekly one, even though weekly is the
    // window that is actually running. Tab order (WindowCardText.Tabs) is no
    // longer what decides this; the default-selection rule in
    // QuotaLensProjection.DefaultTab is.
    [Fact]
    public void DefaultSelectionPrefersTheSessionTabEvenWhenItIsIdleAndWeeklyIsRunning()
    {
        var weekly = TwoCycleSeries("claude", "primary", "weekly.v1");
        var session = Series("claude", "primary", "session.v1", Sample(40, 1_500, 2_000, active: false));
        var quota = Quota(
            "claude",
            Window("claude|weekly.v1", "Weekly", "weekly.v1"),
            Window("claude|session.v1", "Session", "session.v1"));

        var model = QuotaLensProjection.Build(
            [weekly, session], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            // No stored preference — the default-selection arm is what runs.
            new QuotaLensProjection.Selection("claude", string.Empty));

        Assert.Equal("session.v1", model.Client!.Selected?.Id.WindowKey);
    }

    // With no session-class window at all, the default falls back to the most
    // depleted tab — the lowest finite RemainingPercent — mirroring macOS's
    // own fallback in WindowCardLoader.pick.
    [Fact]
    public void DefaultSelectionPicksTheMostDepletedTabWhenNoneAreSessionClass()
    {
        var quota = Quota(
            "claude",
            Window("claude|weekly.v1", "Weekly", "weekly.v1", remainingPercent: 80),
            Window("claude|chat.v1", "Chat", "chat.v1", remainingPercent: 15));

        var model = QuotaLensProjection.Build(
            null, quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("claude", string.Empty));

        Assert.Equal("chat.v1", model.Client!.Selected?.Id.WindowKey);
    }

    // An explicit stored pick still wins over the default rule, even when it
    // names the tab the default rule would NOT have picked (weekly, not the
    // session-class tab).
    [Fact]
    public void AnExplicitPickStillWinsOverTheSessionDefault()
    {
        var quota = Quota(
            "claude",
            Window("claude|weekly.v1", "Weekly", "weekly.v1"),
            Window("claude|session.v1", "Session", "session.v1"));

        var model = QuotaLensProjection.Build(
            null, quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("claude", "claude|primary|weekly.v1"));

        Assert.Equal("weekly.v1", model.Client!.Selected?.Id.WindowKey);
    }

    // ---- site 6: growing the history card (parity with macOS #334) ---------

    // The count is honoured only for the window it was grown on. macOS keys
    // the same state on the RESOLVED window for two reasons, and this covers
    // the second: a stored preference belonging to another client leaves this
    // one's window alone, so a reset driven by the click alone would miss it.
    [Fact]
    public void AGrownRowCountAppliesOnlyToTheWindowItWasGrownOn()
    {
        var weekly = ManyCycleSeries("antigravity", "primary", "weekly.v1", 20);
        var session = ManyCycleSeries("antigravity", "primary", "session.v1", 20);
        var quota = Quota(
            "antigravity",
            Window("antigravity|weekly.v1", "Weekly", "weekly.v1"),
            Window("antigravity|session.v1", "Session", "session.v1"));

        QuotaLensProjection.Model Build(string? grownOn) =>
            QuotaLensProjection.Build(
                [weekly, session], quota, EmptyGraph(), windowUsage: null,
                WindowEquivalence.FetchOutcome.NotAttempted,
                quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
                UsageAttribution.Table.Empty, year: null,
                new QuotaLensProjection.Selection(
                    "antigravity-cli", "antigravity|primary|session.v1",
                    HistoryShownWindow: grownOn, HistoryShownCount: 18));

        // Grown on the window that is actually resolved: the count applies.
        var onSession = Build("antigravity|primary|session.v1");
        Assert.Equal("antigravity|primary|session.v1", onSession.Client!.History.ShownWindow);
        Assert.Equal(18, onSession.Client.History.DisplayRows.Count);

        // Grown on the sibling window: the card opens at its usual count
        // rather than inheriting a list the reader grew somewhere else.
        var onWeekly = Build("antigravity|primary|weekly.v1");
        Assert.Equal(
            WindowHistoryText.VisibleRows, onWeekly.Client!.History.DisplayRows.Count);
    }

    // The remainder is folded here, not in the view, so the control and the
    // rows beside it cannot disagree about how much history is left.
    [Fact]
    public void TheRemainderCountsTheAdmittedCyclesNotYetDrawn()
    {
        var series = ManyCycleSeries("codex", "primary", "weekly.v1", 20);
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            [series], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        var history = model.Client!.History;
        Assert.Equal(WindowHistoryText.VisibleRows, history.DisplayRows.Count);
        Assert.Equal(history.Cycles.Count - history.DisplayRows.Count, history.Remaining);
        Assert.True(history.Remaining > 0);
    }

    // The ≈ line is pooled over the rows ON SCREEN, so the `declared` flag it
    // is folded with has to be asked of those same rows. Taking it over every
    // admitted cycle lets evidence the reader cannot see cast the vote: with
    // classification only in a hidden cycle, `!declared` is false, Aggregate
    // skips its Undeclared branch, and visible rows carrying movement but
    // nothing attributed report "the quota moved and none of it was recorded
    // on this machine" — a data failure — when the truth is that this user has
    // not classified what those rows hold.
    //
    // The skew predates the grow control (12 shown against up to
    // ConsideredCycles folded); making the shown count variable is what turned
    // it from a fixed offset into one a reader can move.
    [Fact]
    public void TheDeclaredFlagIsAskedOfTheShownCyclesNotTheHiddenOnes()
    {
        // One more cycle than the card opens with, so exactly one is hidden —
        // and it is the oldest, which is index 1 here.
        var series = RisingCycleSeries("codex", "primary", "weekly.v1", WindowHistoryText.VisibleRows + 1);
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));
        // The only classified usage sits in that hidden cycle.
        var messages = new[] { Message(InCycleSpanMs(1), "codex", "openai", 5_000, 2.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));

        var model = QuotaLensProjection.Build(
            [series], quota, EmptyGraph(), new WindowUsage(messages, 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        var history = model.Client!.History;
        // The fixture is only meaningful if the cycle holding the evidence is
        // genuinely off screen.
        Assert.Equal(WindowHistoryText.VisibleRows, history.DisplayRows.Count);
        Assert.True(history.Cycles.Count > history.DisplayRows.Count);
        Assert.DoesNotContain(
            history.DisplayRows, row => row.ResetAtMs == history.Cycles[^1].ResetAtMs);

        Assert.IsType<WindowEquivalence.Row.Undeclared>(history.Equivalence);
    }

    // The mirror: once the classified cycle is on screen, the same flag reads
    // true and the line stops saying "classify your usage". Without this the
    // test above would also pass on a `declared` hardwired to false.
    [Fact]
    public void TheDeclaredFlagReadsTrueOnceTheClassifiedCycleIsShown()
    {
        var series = RisingCycleSeries("codex", "primary", "weekly.v1", WindowHistoryText.VisibleRows + 1);
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));
        var messages = new[] { Message(InCycleSpanMs(1), "codex", "openai", 5_000, 2.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));

        var model = QuotaLensProjection.Build(
            [series], quota, EmptyGraph(), new WindowUsage(messages, 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            confirmed, year: null,
            // One press: the hidden cycle joins the list.
            new QuotaLensProjection.Selection(
                "codex", string.Empty,
                HistoryShownWindow: "codex|primary|weekly.v1",
                HistoryShownCount: WindowHistoryText.VisibleRows * 2));

        var history = model.Client!.History;
        Assert.Equal(history.Cycles.Count, history.DisplayRows.Count);
        Assert.IsNotType<WindowEquivalence.Row.Undeclared>(history.Equivalence);
    }

    // Site 5: the undated note's own raw part, threaded through rather than
    // read straight off the snapshot in the view.
    [Fact]
    public void UndatedCountIsCarriedFromTheWindowUsagePayload()
    {
        var model = QuotaLensProjection.Build(
            [TwoCycleSeries("codex", "primary", "weekly.v1")], quota: null, EmptyGraph(),
            windowUsage: new WindowUsage([], UndatedCount: 7, 0),
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.Equal(7, model.Client!.UndatedCount);
    }

    // Site 3/4: no active cycle selected (a client with only completed
    // history) draws no live equivalence line at all — the same guard the
    // view used to apply by checking WindowCardState == Chart before
    // touching declared/equivalence.
    [Fact]
    public void NoLiveEquivalenceWhenNoTabHasAPlacedActiveCycle()
    {
        var idleOnly = Series("codex", "primary", "weekly.v1", Sample(40, 1_500, 2_000, active: false));
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            [idleOnly], quota, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.NotNull(model.Client!.Selected);
        Assert.Null(model.Client.LiveEquivalence);
    }

    // ---- round 19 finding 2: the live card and its footer must describe
    // the same clipped interval -----------------------------------------

    // A provider that shortens its reported duration mid-cycle moves StartMs
    // forward past readings QuotaHistoryFold.Active deliberately still
    // retains (see QuotaCycle.EvidenceStartMs's own doc comment for the same
    // shape already fixed on the completed-cycle path). The chart above this
    // line clips to [StartMs, now) — WindowCardGeometry.Chart's own
    // windowStartMs filter — so the live equivalence line must clip its
    // samples and its message span at that same StartMs, not the full active
    // sample span, or the two disagree about which evidence counts.
    [Fact]
    public void LiveEquivalenceClipsToTheActiveCyclesOwnStartNotTheFullSampleSpan()
    {
        // Inferred window: start = the first own message (NowMs - 1_800_000), well
        // after the payload range start (reset - 5 h), so the chart samples
        // carry a reading (NowMs - 3_000_000) from before the window start that
        // RangeSamples keeps and only the projection clip can drop. That
        // reading plus the message at the window start are the only evidence the
        // UNCLIPPED span [-3_000_000, -600_000] would hold; clipped to
        // [-1_800_000, now] the span [-1_200_000, -600_000] holds none, so the
        // row is Undeclared, not a computed ratio.
        var first = NowMs - 1_800_000;
        var client = InferenceClient(
            -3_600_000,
            [
                Sample(5, (NowMs - 3_000_000) / 1_000, 2_000, active: false),
                Sample(8, (NowMs - 1_200_000) / 1_000, 2_000, active: false),
                Sample(10, (NowMs - 600_000) / 1_000, 2_000, active: false),
            ],
            Message(first, "codex", "openai", 10, 0.1));

        Assert.True(client.Selected!.Inferred);
        Assert.Equal(
            [5d, 8d, 10d], client.Selected.Active!.Samples.Select(sample => sample.UsedPercent).ToArray());
        Assert.IsType<WindowEquivalence.Row.Undeclared>(client.LiveEquivalence);
    }

    // The upper bound (`<= ResetAtMs`): the engine keeps a group open for its
    // rollover grace, so the store-fallback Active can carry a reading past the
    // window's reset (the next cycle's first). It is not this window's evidence.
    // In-window readings are flat (10, 10) => NotMoved; counting the 50 taken
    // after the reset would give a 40-point rise instead.
    [Fact]
    public void LiveEquivalenceDropsAReadingTakenAfterTheWindowsReset()
    {
        var history = new[]
        {
            Series(
                "codex", "primary", "weekly.v1",
                Sample(10, sampledAt: 5_000, resetAt: 6_000, duration: 1_000),
                Sample(10, sampledAt: 5_900, resetAt: 6_000, duration: 1_000),
                Sample(50, sampledAt: 6_100, resetAt: 6_000, duration: 1_000)),
        };
        var messages = new[] { Message(5_500_000, "codex", "openai", 5_000, 25.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));

        var model = QuotaLensProjection.Build(
            history, quota: null, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded, confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.Equal(6_000_000, model.Client!.Selected!.Active!.ResetAtMs);
        Assert.IsType<WindowEquivalence.Row.NotMoved>(model.Client.LiveEquivalence);
    }

    // State's `{ IsPlaced: false } => Unplaceable` arm on the store-fallback
    // path (no payload, so ChartSamples is null and the stored Active stands):
    // the newest active sample reports duration 0, so no start can be derived.
    [Fact]
    public void AStoredActiveCycleWithNoDurationOnTheFallbackPathIsUnplaceable()
    {
        var history = new[]
        {
            Series("codex", "primary", "weekly.v1", Sample(10, sampledAt: 5_000, resetAt: 6_000, duration: 0)),
        };

        var model = QuotaLensProjection.Build(
            history, quota: null, EmptyGraph(), windowUsage: null,
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        var selected = model.Client!.Selected!;
        Assert.Null(selected.ChartSamples);
        Assert.False(selected.Active!.IsPlaced);
        Assert.Equal(
            WindowCardState.Unplaceable,
            WindowCardText.State(selected, WindowEquivalence.FetchOutcome.Succeeded, DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
    }

    // ---- round 8 finding 2: the collapsed row must show the WHOLE-WINDOW
    // total, not the sample-span-restricted one -----------------------------

    // A single-sample completed cycle: FirstSampleMs == LastSampleMs, so the
    // span QuotaHistoryFold.SpanTotals bounds is empty by construction — the
    // same "one-sample cycle" case WindowEquivalence's own comment on
    // MinimumObservedFraction/MinimumCycles notes computes to exactly zero.
    // A message stamped between the cycle's own StartMs and its first quota
    // sample is inside [EvidenceStartMs, ResetAtMs) — Mine — but strictly
    // before FirstSampleMs, so it is outside the span. If the display row
    // were built from SpanTokens/SpanCost (the round 8 bug) this message
    // would vanish from the collapsed row and its bar scale while still
    // showing up in the row's own expanded model breakdown.
    [Fact]
    public void TheCollapsedHistoryRowShowsTheSameTotalAsItsOwnExpandedModelBreakdown()
    {
        // duration 5h = 18_000s, resetAt 6_000s -> StartMs = -12_000_000.
        // sampledAt 5_000s -> FirstSampleMs = LastSampleMs = 5_000_000.
        var series = Series(
            "codex", "primary", "session.v1",
            Sample(usedPercent: 40, sampledAt: 5_000, resetAt: 6_000, active: false, duration: 18_000));
        var quota = Quota("codex", Window("codex|session.v1", "Session", "session.v1"));
        // 1_000_000ms is inside [StartMs=-12_000_000, ResetAtMs=6_000_000) —
        // Mine — and NOT inside (FirstSampleMs=5_000_000, LastSampleMs] —
        // Span, which is empty here regardless.
        var messages = new[] { Message(1_000_000, "codex", "openai", tokens: 1_000, cost: 5.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));

        var model = QuotaLensProjection.Build(
            [series], quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded, quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        var displayRow = Assert.Single(model.Client!.History.DisplayRows);
        var storedRow = model.Client.History.ByResetAt[displayRow.ResetAtMs];

        // The fixture actually exercises the Mine-vs-Span gap it claims to.
        Assert.Equal(1_000, storedRow.MineTokens);
        Assert.Equal(5.0, storedRow.MineCost);
        Assert.Equal(0, storedRow.SpanTokens);
        Assert.Equal(0, storedRow.SpanCost);

        // The collapsed row and its own expanded model breakdown must report
        // the same total — the whole-window one, not the empty span.
        var breakdownTokens = storedRow.Models.Sum(m => m.Tokens);
        var breakdownCost = storedRow.Models.Sum(m => m.Cost);
        Assert.Equal(1_000, breakdownTokens);
        Assert.Equal(5.0, breakdownCost);
        Assert.Equal(breakdownTokens, displayRow.Tokens);
        Assert.Equal(breakdownCost, displayRow.Cost);
    }

    // ---- grouped tabs: which member's window card a tab draws -------------

    private static AgentUsagePayload Agents(params AgentUsageSnapshot[] agents) =>
        new("2026-01-01T00:00:00Z", agents);

    private static AgentUsageSnapshot Agent(string clientId, params UsageWindow[] windows) =>
        new(clientId, "source", "2026-01-01T00:00:00Z", windows);

    private static QuotaLensProjection.Client GrokTab(
        AgentUsagePayload quota, IReadOnlyList<QuotaHistorySeries> history, string tab = "grok") =>
        QuotaLensProjection.Build(
            history, quota, EmptyGraph(),
            windowUsage: new WindowUsage([Message(InActiveSpanMs, "grok", "xai", 1000, 1.0)], 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(),
            year: null,
            new QuotaLensProjection.Selection(tab, string.Empty, LocalUsageClients: ["grok"])).Client!;

    // A Grok Bot-only user: the "Grok Build & Bot" tab used to key the card by
    // QuotaOwner("grok") = "grok", which reports nothing, so the Bot's weekly
    // window and its history never appeared. The tab's other member draws it.
    [Fact]
    public void ABotOnlyGrokTabDrawsTheBotWeeklyWindowAndItsHistory()
    {
        var quota = Agents(Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1")));
        var history = new[] { TwoCycleSeries("grok-bot", "primary", "weekly.v1") };

        var client = GrokTab(quota, history);

        Assert.Equal("grok-bot", client.Owner);
        Assert.Equal(["grok-bot|weekly.v1"], client.Tabs.Select(tab => tab.Id.ProviderId + "|" + tab.Id.WindowKey));
        Assert.Equal("grok-bot", client.Selected!.Id.ProviderId);
        Assert.NotEmpty(client.History.Cycles);
        // Grok Bot has no local usage: Build's messages are not its usage.
        Assert.True(client.LocalUsageUnattributed);
        Assert.Empty(client.Messages);
    }

    // With both members reporting, the tab keeps the owner's card (macOS draws
    // one window card per tab); Build's local usage stays attributed.
    [Fact]
    public void AGrokTabWithBuildAndBotDrawsTheBuildCard()
    {
        var quota = Agents(
            Agent("grok", Window("grok|billing.weekly.v1", "Weekly", "billing.weekly.v1")),
            Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1")));

        var client = GrokTab(quota, []);

        Assert.Equal("grok", client.Owner);
        Assert.Equal("grok", client.Selected!.Id.ProviderId);
        Assert.False(client.LocalUsageUnattributed);
        Assert.Equal("grok-bot", QuotaLensProjection.WindowCardOwner(Agents(Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1"))), "grok"));
    }

    // macOS WindowCardGate.clients (WindowCardLoader.swift:625-639) with the
    // PopoverView.swift:151-165 inputs. (i) Grok Build has local records and only
    // Grok Bot reports windows: grok is a card client (present slice), so it owns
    // the card - the old "owner has no windows tab" rule picked grok-bot.
    [Fact]
    public void GrokBuildPresentLocallyOwnsTheTabWhenOnlyTheBotReports()
    {
        var botOnly = Agents(Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1")));
        Assert.Equal("grok", QuotaLensProjection.WindowCardOwner(botOnly, "grok", present: ["grok"]));
        // (ii) control: no local records, no grok quota - the Bot's card.
        Assert.Equal("grok-bot", QuotaLensProjection.WindowCardOwner(botOnly, "grok", present: []));
        Assert.Equal("grok-bot", QuotaLensProjection.WindowCardOwner(botOnly, "grok", present: ["codex"]));
    }

    // (iii) A limits-hidden Bot is not picked by the fallback: the grok tab's
    // owner is not a card client and the only member that is, grok-bot, is
    // excluded, so macOS draws no window card (WindowCardLoader.swift:625-639).
    [Fact]
    public void ALimitsHiddenBotIsNotPickedByTheFallback()
    {
        var botOnly = Agents(Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1")));
        var hidden = new HashSet<string> { "grok-bot" };
        Assert.Null(QuotaLensProjection.WindowCardOwner(botOnly, "grok", present: [], limitsHidden: hidden));
        Assert.Equal("grok-bot", QuotaLensProjection.WindowCardOwner(botOnly, "grok", present: [], limitsHidden: new HashSet<string>()));
    }

    // `guard !excluded.contains(tab)`: a visible tab whose own limits are
    // switched off draws no window card even though it reports windows (the
    // old fallback returned the owner).
    [Fact]
    public void ALimitsHiddenOwnerOfAVisibleTabDrawsNoWindowCard()
    {
        var quota = Agents(Agent("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1")));
        Assert.Equal("codex", QuotaLensProjection.WindowCardOwner(quota, "codex"));
        Assert.Null(QuotaLensProjection.WindowCardOwner(
            quota, "codex", limitsHidden: new HashSet<string> { "codex" }));

        var client = CodexTab(quota, new HashSet<string> { "codex" });
        Assert.False(client.HasWindowCard);
        Assert.True(CodexTab(quota, new HashSet<string>()).HasWindowCard);
    }

    // The grouped tab's id is excluded but another member qualifies: macOS
    // returns nil for the TAB first, so no other member's card is drawn.
    [Fact]
    public void AnExcludedGroupedTabIdDrawsNoCardEvenWhenAMemberQualifies()
    {
        var both = Agents(
            Agent("grok", Window("grok|billing.weekly.v1", "Weekly", "billing.weekly.v1")),
            Agent("grok-bot", Window("grok-bot|weekly.v1", "Weekly", "weekly.v1")));
        Assert.Null(QuotaLensProjection.WindowCardOwner(
            both, "grok", present: ["grok"], limitsHidden: new HashSet<string> { "grok" }));
    }

    // A tab with no card client at all (no records, no quota): no card.
    [Fact]
    public void ATabWithNoQuotaAndNoRecordsDrawsNoWindowCard() =>
        Assert.Null(QuotaLensProjection.WindowCardOwner(Agents(), "cursor", present: []));

    // The strip and heatmap a no-card tab draws are the tab's own slice
    // (QuotaView.swift:92, :96).
    [Fact]
    public void TheStripAndHeatmapFilterToTheTabsClients()
    {
        QuotaWindowIdentity Id(string provider) => new(provider, "primary", "weekly.v1");
        IReadOnlyList<QuotaWindowSummary> summaries =
            [new(Id("grok"), "Weekly", [], [], 0, false, 1), new(Id("grok-bot"), "Weekly", [], [], 0, false, 1),
             new(Id("codex"), "Weekly", [], [], 0, false, 1)];
        IReadOnlyList<QuotaHeatmapWindow> windows =
            [new(Id("grok"), "Weekly", 1), new(Id("codex"), "Weekly", 1)];

        var slice = ClientRegistry.TabSlice("grok");
        Assert.Equal(["grok", "grok-bot"], QuotaOverviewFold.ForClients(summaries, slice).Select(s => s.Id.ProviderId));
        Assert.Equal(["grok"], QuotaOverviewFold.ForClients(windows, slice).Select(w => w.Id.ProviderId));
        Assert.Empty(QuotaOverviewFold.ForClients(summaries, ["cursor"]));
    }

    private static QuotaLensProjection.Client CodexTab(AgentUsagePayload quota, IReadOnlySet<string> limitsHidden) =>
        QuotaLensProjection.Build(
            [], quota, EmptyGraph(),
            windowUsage: new WindowUsage([], 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(),
            year: null,
            new QuotaLensProjection.Selection("codex", string.Empty, LimitsHidden: limitsHidden)).Client!;

    // Antigravity's local usage is recorded under antigravity-cli; with no
    // payload yet (before the first fetch, or offline) the tab must keep the
    // antigravity owner, whose stored history the card draws, rather than
    // move to antigravity-cli (whose slice alone leaves antigravity out).
    [Fact]
    public void AntigravityCliPresentKeepsTheAntigravityOwnerBeforeAnyPayload()
    {
        Assert.Equal("antigravity", QuotaLensProjection.WindowCardOwner(null, "antigravity", present: ["antigravity-cli"]));
    }

    // Control: the Antigravity group's other member, antigravity-cli, is
    // never a quota provider, so its tab keeps the antigravity card whether
    // or not antigravity reports windows.
    [Fact]
    public void TheAntigravityTabKeepsTheAntigravityCard()
    {
        var withWindows = Agents(Agent("antigravity", Window("antigravity|session.v1", "Session", "session.v1")));
        var noWindows = Agents(Agent("antigravity"), Agent("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1")));

        Assert.Equal("antigravity", QuotaLensProjection.WindowCardOwner(withWindows, "antigravity"));
        Assert.Equal("antigravity", QuotaLensProjection.WindowCardOwner(withWindows, "antigravity-cli"));
        Assert.Equal("antigravity", QuotaLensProjection.WindowCardOwner(noWindows, "antigravity"));
        Assert.Equal("codex", QuotaLensProjection.WindowCardOwner(noWindows, "codex"));

        var client = GrokTab(withWindows, [], tab: "antigravity");
        Assert.Equal("antigravity", client.Owner);
        Assert.Equal("antigravity", client.Selected!.Id.ProviderId);
    }

    // ---- the macOS `.inferred` branch (WindowResolution.swift:33-35) -------

    private const long NowMs = 1_800_000_000_000;
    private const long DurationMs = FiveHours * 1_000;

    // Store: only a completed cycle (the LearningDuration shape, no active
    // group). Live: reset one hour ago, five-hour duration.
    private static QuotaLensProjection.Client InferenceClient(params WindowMessage[] messages) =>
        InferenceClient(-3_600_000, messages);

    private static QuotaLensProjection.Client InferenceClient(long resetOffsetMs, params WindowMessage[] messages) =>
        InferenceClient(
            resetOffsetMs, [Sample(40, 1_500, 2_000, active: false)],
            WindowEquivalence.FetchOutcome.Succeeded, null, false, messages);

    private static QuotaLensProjection.Client InferenceClient(
        long resetOffsetMs, QuotaHistorySample[] stored, params WindowMessage[] messages) =>
        InferenceClient(resetOffsetMs, stored, WindowEquivalence.FetchOutcome.Succeeded, null, false, messages);

    private static QuotaLensProjection.Client InferenceClient(
        long resetOffsetMs, WindowEquivalence.FetchOutcome outcome, long? scanFromMs, bool unattributed,
        params WindowMessage[] messages) =>
        InferenceClient(
            resetOffsetMs, [Sample(40, 1_500, 2_000, active: false)], outcome, scanFromMs, unattributed, messages);

    private static QuotaLensProjection.Client InferenceClient(
        long resetOffsetMs, QuotaHistorySample[] stored, WindowEquivalence.FetchOutcome outcome,
        long? scanFromMs, bool unattributed, params WindowMessage[] messages)
    {
        var resetIso = DateTimeOffset.FromUnixTimeMilliseconds(NowMs + resetOffsetMs).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var quota = Quota(
            "codex",
            Window("codex|session.v1", "Session", "session.v1") with
            {
                ResetsAt = resetIso,
                DurationSeconds = FiveHours,
            });
        var history = new[] { Series("codex", "primary", "session.v1", stored) };

        return QuotaLensProjection.Build(
            history, quota, EmptyGraph(), new WindowUsage(messages, 0, 0),
            outcome, WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(
                new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")),
                new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Assigned("claude"))),
            year: null,
            new QuotaLensProjection.Selection(
                "codex", string.Empty, LocalUsageClients: unattributed ? [] : null),
            now: DateTimeOffset.FromUnixTimeMilliseconds(NowMs),
            windowUsageFromMs: scanFromMs).Client!;
    }

    private static WindowCardState CardState(QuotaLensProjection.Client client) =>
        WindowCardText.State(
            client.Selected, WindowEquivalence.FetchOutcome.Succeeded,
            DateTimeOffset.FromUnixTimeMilliseconds(NowMs), client.Scan);

    // ---- placement pending (macOS placementPending + scan.covers) ----------
    private const long ResetMs = NowMs - 3_600_000;

    [Fact] // row 1
    public void AColdStartCardIsPlacementPendingNotIdle() =>
        Assert.Equal(WindowCardState.PlacementPending, CardState(
            InferenceClient(-3_600_000, WindowEquivalence.FetchOutcome.NotAttempted, null, false)));

    [Fact] // row 2
    public void AFailedScanCardIsPlacementPendingNotIdle() =>
        Assert.Equal(WindowCardState.PlacementPending, CardState(
            InferenceClient(-3_600_000, WindowEquivalence.FetchOutcome.Failed, null, false)));

    [Fact] // row 3
    public void AScanStartingAfterTheResetCannotSayNothingWasUsed() =>
        Assert.Equal(WindowCardState.PlacementPending, CardState(
            InferenceClient(-3_600_000, WindowEquivalence.FetchOutcome.Succeeded, ResetMs + 1, false)));

    [Fact] // row 4
    public void AnInferredWindowStartingBeforeTheScanIsPlacementPendingNotChart()
    {
        var first = NowMs - 1_800_000;
        Assert.Equal(WindowCardState.PlacementPending, CardState(
            InferenceClient(
                -3_600_000, WindowEquivalence.FetchOutcome.Succeeded, first + 1, false,
                Message(first, "codex", "openai", 10, 0.1))));
        Assert.Equal(WindowCardState.Chart, CardState(
            InferenceClient(
                -3_600_000, WindowEquivalence.FetchOutcome.Succeeded, first, false,
                Message(first, "codex", "openai", 10, 0.1))));
    }

    [Fact] // row 5
    public void ACoveringScanWithNoOwnUsageSinceTheResetIsIdle() =>
        Assert.Equal(WindowCardState.Idle, CardState(
            InferenceClient(-3_600_000, WindowEquivalence.FetchOutcome.Succeeded, ResetMs, false)));

    [Fact] // row 6
    public void AnUnattributedAccountWhoseWindowEndedIsIdleUnattributed() =>
        Assert.Equal(WindowCardState.IdleUnattributed, CardState(
            InferenceClient(-3_600_000, WindowEquivalence.FetchOutcome.Succeeded, ResetMs, true)));

    [Fact]
    public void OwnUsageAfterTheLiveResetInfersTheWindowFromTheFirstMessage()
    {
        var first = NowMs - 1_800_000;
        var client = InferenceClient(
            Message(first + 600_000, "codex", "openai", 10, 0.1),
            Message(first, "codex", "openai", 10, 0.1));

        Assert.Equal(
            WindowCardState.Chart,
            WindowCardText.State(client.Selected, WindowEquivalence.FetchOutcome.Succeeded,
                DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
        Assert.True(client.Selected!.Inferred);
        Assert.Equal(first, client.Selected.Active!.StartMs);
        Assert.Equal(first + DurationMs, client.Selected.Active.ResetAtMs);
        // The store's only reading is far outside the payload window, so the
        // chart is the live reading alone (macOS curveSamples + liveReading).
        Assert.Equal([10d], client.Selected.Active.Samples.Select(sample => sample.UsedPercent).ToArray());
        // One reading is not enough to compare against usage.
        Assert.Equal(
            "Not enough quota readings yet",
            WindowCardText.LiveLine(client.LocalUsageUnattributed, client.LiveEquivalence));
        Assert.Same(client.Selected, Assert.Single(client.Tabs));
        Assert.StartsWith(
            "Inferred window · resets in",
            WindowCardText.Subtitle(
                WindowCardState.Chart, client.Selected, DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
    }

    // H1: the live reading (10) equals the newest stored sample, so the chart
    // has no live point, and its only reading predates the inferred window
    // [first, first + 5 h]. The line must not borrow it (macOS
    // WindowUsageCard.swift:540-549: strict [start, end], no fallback).
    [Fact]
    public void AnInferredWindowWhoseOnlyReadingPredatesItSaysNotEnoughReadings()
    {
        var first = NowMs - 1_800_000;
        var client = InferenceClient(
            -3_600_000,
            [Sample(10, (NowMs - 7_200_000) / 1_000, 2_000, active: false)],
            Message(first, "codex", "openai", 10, 0.1));

        Assert.True(client.Selected!.Inferred);
        Assert.Equal(first, client.Selected.Active!.StartMs);
        Assert.Equal([10d], client.Selected.Active.Samples.Select(sample => sample.UsedPercent).ToArray());
        // macOS WindowUsageCard.swift:167 always renders the row; an empty
        // in-window list is .unavailable (WindowEquivalence.swift:164). Never a
        // ratio, and never the previous cycle's.
        Assert.IsType<WindowEquivalence.Row.Unavailable>(client.LiveEquivalence);
        Assert.Equal(
            "Not enough quota readings yet",
            WindowCardText.LiveLine(client.LocalUsageUnattributed, client.LiveEquivalence));
    }

    // H1 control: the same shape with a reading inside the window gives a row.
    [Fact]
    public void AnInferredWindowWithAReadingInsideItHasAnEquivalenceLine()
    {
        var first = NowMs - 1_800_000;
        var client = InferenceClient(
            -3_600_000,
            [Sample(10, (NowMs - 1_200_000) / 1_000, 2_000, active: false)],
            Message(first, "codex", "openai", 10, 0.1));

        Assert.NotNull(client.LiveEquivalence);
        Assert.NotNull(WindowCardText.LiveLine(client.LocalUsageUnattributed, client.LiveEquivalence));
    }

    // ---- placement follows the payload's resolution, not the store's group --
    // WindowCardLoader.swift:122-129 -> WindowResolver.resolve: the engine keeps
    // the store's active group for its rollover grace after the payload's reset
    // passes, but the window is over; the store must not place it.

    private static QuotaHistorySample[] OpenGroupAroundReset(long resetOffsetMs) =>
        [Sample(40, (NowMs + resetOffsetMs - 600_000) / 1_000, (NowMs + resetOffsetMs) / 1_000, active: true, duration: FiveHours)];

    [Fact]
    public void AStoredOpenGroupDoesNotKeepAnEndedWindowOnTheChart()
    {
        var client = InferenceClient(-3_600_000, OpenGroupAroundReset(-3_600_000));

        Assert.Null(client.Selected!.Active);
        Assert.Equal(WindowCardState.Idle, CardState(client));
    }

    [Fact]
    public void AnEndedWindowWhoseScanHasNotLandedIsPlacementPendingNotChart()
    {
        var client = InferenceClient(
            -3_600_000, OpenGroupAroundReset(-3_600_000), WindowEquivalence.FetchOutcome.NotAttempted, null, false);

        Assert.Equal(WindowCardState.PlacementPending, CardState(client));
    }

    [Fact]
    public void AnEndedWindowWithOwnUsageSinceTheResetIsInferredNotStoredActive()
    {
        var first = NowMs - 1_800_000;
        var client = InferenceClient(
            -3_600_000, OpenGroupAroundReset(-3_600_000), Message(first, "codex", "openai", 10, 0.1));

        Assert.True(client.Selected!.Inferred);
        Assert.Equal(first, client.Selected.Active!.StartMs);
    }

    [Fact]
    public void AStoredOpenGroupWithAResetMoreThanOneDurationAheadIsUnplaceable()
    {
        var client = InferenceClient(DurationMs + 3_600_000, OpenGroupAroundReset(DurationMs + 3_600_000));

        Assert.Null(client.Selected!.Active);
        Assert.Equal(WindowCardState.Unplaceable, CardState(client));
    }

    [Fact]
    public void AStoredOpenGroupWithAFutureResetIsPlacedAtThePayloadWindow()
    {
        var client = InferenceClient(3_600_000, OpenGroupAroundReset(3_600_000));

        Assert.Equal(WindowCardState.Chart, CardState(client));
        Assert.Equal(NowMs + 3_600_000, client.Selected!.Active!.ResetAtMs);
        Assert.Equal(NowMs + 3_600_000 - DurationMs, client.Selected.Active.StartMs);
        Assert.False(client.Selected.Inferred);
    }

    // macOS `.active` (WindowResolution.swift:29-31): the quota payload shows
    // a new session whose reset is ahead, before the next history read
    // records it, so the store has no running cycle yet. Placed at macOS's
    // interval [reset - duration, reset), not "Window unavailable".
    [Fact]
    public void AFutureLiveResetWithinOneDurationPlacesTheWindowWithoutAStoredCycle()
    {
        var reset = NowMs + 3_600_000;
        var client = InferenceClient(3_600_000);

        Assert.Equal(
            WindowCardState.Chart,
            WindowCardText.State(client.Selected, WindowEquivalence.FetchOutcome.Succeeded,
                DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
        Assert.False(client.Selected!.Inferred);
        Assert.Equal(reset - DurationMs, client.Selected.Active!.StartMs);
        Assert.Equal(reset, client.Selected.Active.ResetAtMs);
        Assert.Equal(
            "Not enough quota readings yet",
            WindowCardText.LiveLine(client.LocalUsageUnattributed, client.LiveEquivalence));
        Assert.StartsWith(
            "Resets in",
            WindowCardText.Subtitle(
                WindowCardState.Chart, client.Selected, DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
    }

    [Fact]
    public void AnotherClientsUsageAfterTheResetDoesNotInferTheWindow()
    {
        var client = InferenceClient(Message(NowMs - 1_800_000, "claude", "anthropic", 10, 0.1));

        Assert.False(client.Selected!.Inferred);
        Assert.Null(client.Selected.Active);
        Assert.Equal(
            WindowCardState.Idle,
            WindowCardText.State(client.Selected, WindowEquivalence.FetchOutcome.Succeeded,
                DateTimeOffset.FromUnixTimeMilliseconds(NowMs)));
    }

    [Fact]
    public void OwnUsageBeforeTheResetDoesNotInferTheWindow()
    {
        var client = InferenceClient(Message(NowMs - 3_600_000 - 1, "codex", "openai", 10, 0.1));

        Assert.False(client.Selected!.Inferred);
        Assert.Null(client.Selected.Active);
    }
}
