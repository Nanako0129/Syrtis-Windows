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
            quota: null,
            EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Failed,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))),
            year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        Assert.Empty(model.Overview.Equivalences);
    }

    [Fact]
    public void OverviewEquivalencesArePopulatedOnceTheFetchSucceeds()
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
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        // QuotaEquivalenceFold.Build inserts one row per series regardless of
        // how much evidence it carries — the row's own shape (Ratio,
        // Unavailable, ...) is QuotaEquivalenceFold's own contract, already
        // asserted in QuotaEquivalenceFoldTests. What this test pins is that
        // the projection actually calls it once the outcome is Succeeded.
        Assert.Single(model.Overview.Equivalences);
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
            new QuotaLensProjection.Selection("codex", string.Empty));

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
        var history = new[]
        {
            Series(
                "codex", "primary", "weekly.v1",
                // Old active sample, taken before the newest sample's
                // shorter duration moves StartMs forward past it.
                Sample(5, sampledAt: 4_000, resetAt: 6_000, duration: 20 * 3_600),
                // Sits exactly at the new StartMs (6_000 - 1_000 = 5_000) —
                // the earliest reading the clip should still keep.
                Sample(10, sampledAt: 5_000, resetAt: 6_000, duration: 20 * 3_600),
                // Newest sample: its own short duration is what places
                // StartMs at 5_000_000ms, past the first sample above.
                Sample(15, sampledAt: 5_900, resetAt: 6_000, duration: 1_000)),
        };
        // The only evidence, and it sits strictly before the clipped start
        // (5_000_000ms) though inside the old, unclipped span
        // (4_000_000ms onward) — exactly the gap this finding closes.
        var messages = new[] { Message(4_500_000, "codex", "openai", 5_000, 25.0) };
        var confirmed = Confirmed(
            new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex")));
        var quota = Quota("codex", Window("codex|weekly.v1", "Weekly", "weekly.v1"));

        var model = QuotaLensProjection.Build(
            history, quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded, confirmed, year: null,
            new QuotaLensProjection.Selection("codex", string.Empty));

        Assert.NotNull(model.Client);
        // Undeclared, not a computed ratio: once clipped, the message before
        // the new StartMs is no longer evidence for this window at all — the
        // same span the chart above this line draws nothing over either.
        Assert.IsType<WindowEquivalence.Row.Undeclared>(model.Client!.LiveEquivalence);
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
}
