using System.Text.Json;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// S2: a Claude card is identified, labelled and selected by (clientId,
// accountKey). Fixtures carry the primary's history scope on every outcome
// (the core's contract), so "no scope" below always means a non-primary card.
public class AccountIdentityTests
{
    private const long Hour = 3_600;
    private const string Desktop = "claude-desktop";
    private const string Dir = @"D:\Work\team-b";

    public AccountIdentityTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static UsageWindow Window(string cardId, string label, double remaining, string? key = null) =>
        new(Label: label, UsedPercent: 100 - remaining, RemainingPercent: remaining, CardId: cardId,
            PaceStatus: key is null
                ? new PaceStatus(UsagePaceState.Unavailable)
                : new PaceStatus(UsagePaceState.Available, WindowKey: key, DurationSeconds: 5 * Hour));

    private static AgentUsageSnapshot Card(
        string? accountKey, string? scope, string? error = null, params UsageWindow[] windows) =>
        new("claude", "oauth", "2026-08-31T00:00:00Z", windows, Error: error,
            HistoryScope: scope is null ? null : new AccountScopeStatus(Scope: scope),
            AccountKey: accountKey);

    private static AgentUsagePayload Payload(params AgentUsageSnapshot[] agents) =>
        new("2026-08-31T00:00:00Z", agents);

    private static AgentUsagePayload TwoAccounts() => Payload(
        Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
        Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));

    private static QuotaHistorySample Sample(double used, long at, bool active) =>
        new(ResetAt: 100 * Hour, DurationSeconds: 5 * Hour,
            DurationSource: QuotaHistoryDurationSource.Provider, UsedPercent: used,
            SampledAt: at * Hour, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: active);

    // `active` = the running cycle (what a window card draws); otherwise a
    // completed cycle of two readings (what the Quota lens summarises).
    private static QuotaHistorySeries Series(
        string scope, string key = "session.v1", double used = 40, bool active = true) =>
        new("claude", scope, key,
            active ? [Sample(used, 97, true)] : [Sample(10, 96, false), Sample(used, 97, false)]);

    // ---- DTO ---------------------------------------------------------------

    [Fact]
    public void AccountKeyDecodesWhenPresentAndIsPrimaryWhenAbsentOrEmpty()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var p = JsonSerializer.Deserialize<AgentUsagePayload>(
            """
            {"generatedAt":"n","agents":[
              {"clientId":"claude","source":"oauth","updatedAt":"n","windows":[]},
              {"clientId":"claude","source":"oauth","updatedAt":"n","windows":[],"accountKey":"claude-desktop"},
              {"clientId":"claude","source":"oauth","updatedAt":"n","windows":[],"accountKey":""}]}
            """, opts)!;
        Assert.Null(p.Agents[0].AccountKey);
        Assert.Null(p.Agents[0].Account.AccountKey);
        Assert.Equal(Desktop, p.Agents[1].Account.AccountKey);
        Assert.Null(p.Agents[2].Account.AccountKey);
    }

    // ---- label ---------------------------------------------------------------

    [Fact]
    public void LabelIsOneFunctionPerAccountKind()
    {
        Assert.Equal("Claude", AccountLabel.Of(new AccountIdentity("claude", null)));
        Assert.Equal("Claude Code", AccountLabel.Of(new AccountIdentity("claude", null), full: true));
        Assert.Equal("Claude Desktop", AccountLabel.Of(new AccountIdentity("claude", Desktop)));
        Assert.Equal("Claude · team-b", AccountLabel.Of(new AccountIdentity("claude", Dir)));
        Assert.Equal("Claude · team-b", AccountLabel.Of(new AccountIdentity("claude", Dir + @"\")));
        Assert.Equal(Dir, AccountLabel.Detail(new AccountIdentity("claude", Dir)));
        Assert.Null(AccountLabel.Detail(new AccountIdentity("claude", Desktop)));
        Assert.Null(AccountLabel.Detail(new AccountIdentity("claude", null)));
    }

    // ---- selection -----------------------------------------------------------

    [Fact]
    public void PrimarySelectionIsByteIdenticalAndExtrasAreThreeSegments()
    {
        Assert.Equal("claude|session.v1", QuotaResolver.Selection("claude", "session.v1"));
        Assert.Equal("claude|session.v1", QuotaResolver.Selection("claude", "session.v1", null));
        Assert.Equal("claude|session.v1", QuotaResolver.Selection("claude", "session.v1", ""));
        Assert.Equal(
            $"claude|session.v1|{Desktop}", QuotaResolver.Selection("claude", "session.v1", Desktop));
    }

    [Fact]
    public void ResolvePicksTheNamedAccountsWindow()
    {
        var payload = TwoAccounts();

        var primary = QuotaResolver.Resolve(payload, "claude|session.v1")!;
        Assert.Null(primary.AccountKey);
        Assert.Equal(80, primary.Window.RemainingPercent);

        var desktop = QuotaResolver.Resolve(payload, $"claude|session.v1|{Desktop}")!;
        Assert.Equal(Desktop, desktop.AccountKey);
        Assert.Equal(30, desktop.Window.RemainingPercent);
    }

    [Fact]
    public void ExtraSelectionRoundTripsThroughCanonicalAndSurvivesAConfigDirPath()
    {
        var payload = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80)),
            Card(Dir, "D", null, Window("session.v1", "Session", 10)));
        var selection = QuotaResolver.Selection("claude", "session.v1", Dir);

        Assert.Equal(selection, QuotaResolver.CanonicalSelection(payload, selection));
        Assert.Equal(10, QuotaResolver.Resolve(payload, selection)!.Window.RemainingPercent);
        // A legacy label for the extra's window migrates inside ITS account.
        Assert.Equal(selection, QuotaResolver.CanonicalSelection(payload, $"claude|Session|{Dir}"));
    }

    [Fact]
    public void ACardIdContainingTheDelimiterStillBelongsToThePrimary()
    {
        var payload = Payload(
            Card(null, "P", null, Window("model.gpt|preview.v1", "M", 50)),
            Card(Desktop, "S", null, Window("model.gpt|preview.v1", "M", 20)));

        Assert.Equal(50, QuotaResolver.Resolve(payload, "claude|model.gpt|preview.v1")!.Window.RemainingPercent);
        Assert.Equal(
            20,
            QuotaResolver.Resolve(payload, $"claude|model.gpt|preview.v1|{Desktop}")!.Window.RemainingPercent);
    }

    [Fact]
    public void AnExtraSelectionWhoseAccountIsGoneMatchesNothingAndIsKept()
    {
        var payload = Payload(Card(null, "P", null, Window("session.v1", "Session", 80)));
        var selection = $"claude|session.v1|{Desktop}";

        Assert.Equal(selection, QuotaResolver.CanonicalSelection(payload, selection));
        Assert.Null(QuotaResolver.Resolve(payload, selection));
    }

    [Fact]
    public void AutoConsidersEveryAccountsWindows()
    {
        var pick = QuotaResolver.Resolve(TwoAccounts(), QuotaResolver.Auto)!;
        Assert.Equal(Desktop, pick.AccountKey);
        Assert.Equal(30, pick.Window.RemainingPercent);
    }

    // ---- summary -------------------------------------------------------------

    [Fact]
    public void TightestNamesTheAccountAndDoesNotCountItselfAmongTheOthers()
    {
        var summary = QuotaSummaryFold.Build(TwoAccounts())!;
        Assert.Equal(Desktop, summary.TightestAccountKey);
        Assert.Equal("Claude Desktop", QuotaSummaryText.TightestName(summary));
        // The primary's identically-keyed card is a different account: counted.
        Assert.Equal(1, summary.OtherWindows);

        var primaryTightest = QuotaSummaryFold.Build(Payload(
            Card(null, "P", null, Window("session.v1", "Session", 5)),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30))))!;
        Assert.Null(primaryTightest.TightestAccountKey);
        Assert.Equal("Claude Code", QuotaSummaryText.TightestName(primaryTightest));
    }

    // ---- history join ----------------------------------------------------------

    [Fact]
    public void WindowCardTabsForThePrimaryIgnoreAnExtrasSeries()
    {
        var quota = Payload(
            // Primary unconfigured: error, no windows, but it still has its scope.
            Card(null, "P", error: "not signed in"),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));

        var tabs = WindowCardText.Tabs(
            [Series("S"), Series("P", "weekly.v1")], quota, "claude", accountKey: null);

        Assert.DoesNotContain(tabs, tab => tab.Id.AccountScope == "S");
        Assert.All(tabs, tab => Assert.Equal("P", tab.Id.AccountScope));
    }

    [Fact]
    public void AnExtraSectionDrawsOnlyItsOwnSeriesOrNone()
    {
        var quota = TwoAccounts();
        var history = new[] { Series("P", used: 70), Series("S", used: 20) };

        var desktop = Assert.Single(WindowCardText.Tabs(history, quota, "claude", Desktop));
        Assert.Equal("S", desktop.Id.AccountScope);
        Assert.Equal(20, desktop.Active!.Samples[^1].UsedPercent);

        var primary = Assert.Single(WindowCardText.Tabs(history, quota, "claude", null));
        Assert.Equal("P", primary.Id.AccountScope);
        Assert.Equal(70, primary.Active!.Samples[^1].UsedPercent);

        // Desktop whose profile failed: a card with no scope gets no series.
        var noScope = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
            Card(Desktop, null, null, Window("session.v1", "Session", 30, "session.v1")));
        var tab = Assert.Single(WindowCardText.Tabs(history, noScope, "claude", Desktop));
        Assert.Null(tab.Active);
        Assert.False(tab.HasHistory);
    }

    [Fact]
    public void LensLabelsJoinByScopeAndNameTheAccount()
    {
        var quota = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));

        var (summaries, _, _) = QuotaLensData.Build([Series("P", active: false), Series("S", active: false)], quota);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("Session", summaries.Single(s => s.Id.AccountScope == "P").WindowLabel);
        Assert.Equal(
            "Claude Desktop · Session", summaries.Single(s => s.Id.AccountScope == "S").WindowLabel);
    }

    [Fact]
    public void ASeriesWithNoLiveSnapshotOfItsScopeKeepsTheFallbackLabel()
    {
        // Multi-card client: an unmatched series never borrows the primary's label.
        var (summaries, _, _) = QuotaLensData.Build([Series("gone", active: false)], TwoAccounts());

        Assert.Null(Assert.Single(summaries).WindowLabel);
    }

    [Fact]
    public void ASingleCardClientKeepsTheScopeBlindLabelJoin()
    {
        // Codex: one card, series from an earlier account still takes the live label.
        var quota = Payload(new AgentUsageSnapshot(
            "codex", "oauth", "n", [Window("session.v1", "Session", 80, "session.v1")],
            HistoryScope: new AccountScopeStatus(Scope: "new")));
        var old = Series("old", active: false) with { ProviderId = "codex" };

        var (summaries, _, _) = QuotaLensData.Build([old], quota);

        Assert.Equal("Session", Assert.Single(summaries).WindowLabel);
    }

    [Fact]
    public void ConfigDirLabelsWidenOnlyWhenTheDirectoryNameCollides()
    {
        var a = @"D:\one\Work";
        var b = @"E:\two\work";
        var c = @"E:\two\other";
        var payload = Payload(
            Card(null, "P"), Card(a, "A"), Card(b, "B"), Card(c, "C"));

        Assert.Equal(@"Claude · one\Work", AccountLabel.Of(new AccountIdentity("claude", a), payload));
        Assert.Equal(@"Claude · two\work", AccountLabel.Of(new AccountIdentity("claude", b), payload));
        Assert.Equal("Claude · other", AccountLabel.Of(new AccountIdentity("claude", c), payload));

        // Same parent too: walk up until they differ.
        var d = @"D:\x\shared\Work";
        var e = @"D:\y\shared\work";
        var deep = Payload(Card(d, "D"), Card(e, "E"));
        Assert.Equal(@"Claude · x\shared\Work", AccountLabel.Of(new AccountIdentity("claude", d), deep));
    }

    // ---- primary window-card target, normalization -------------------------

    [Fact]
    public void WindowCardFollowsThePrimaryElseTheFirstAccountWithWindows()
    {
        var desktopOnly = Payload(
            Card(null, "P", error: "not signed in"),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));
        Assert.Equal(Desktop, WindowCardText.WindowCardAccount(desktopOnly, "claude"));
        var tab = Assert.Single(WindowCardText.Tabs(
            [Series("S"), Series("P")], desktopOnly, "claude", WindowCardText.WindowCardAccount(desktopOnly, "claude")));
        Assert.Equal("S", tab.Id.AccountScope);
        Assert.Equal("Session", tab.Label);

        Assert.Null(WindowCardText.WindowCardAccount(TwoAccounts(), "claude"));
        Assert.Null(WindowCardText.WindowCardAccount(Payload(Card(null, "P", error: "x")), "claude"));
        Assert.Null(WindowCardText.WindowCardAccount(null, "claude"));
    }

    // ---- window-card account switcher --------------------------------------

    [Fact]
    public void AccountPillsListEveryAccountWithWindowsInPayloadOrderOnlyWhenThereAreTwo()
    {
        var pills = WindowCardText.AccountPills(TwoAccounts(), "claude");
        Assert.Equal([null, Desktop], pills.Select(p => p.Key));
        Assert.Equal(["Claude", "Claude Desktop"], pills.Select(p => p.Label));

        var withDir = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
            Card(Dir, "D", null, Window("session.v1", "Session", 60, "session.v1")),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));
        Assert.Equal(
            ["Claude", "Claude · team-b", "Claude Desktop"],
            WindowCardText.AccountPills(withDir, "claude").Select(p => p.Label));

        // One account (or one with windows) => no row; the card stays as it was.
        Assert.Empty(WindowCardText.AccountPills(
            Payload(Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1"))), "claude"));
        Assert.Empty(WindowCardText.AccountPills(
            Payload(
                Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
                Card(Desktop, "S", error: "expired")), "claude"));
        Assert.Empty(WindowCardText.AccountPills(null, "claude"));
    }

    [Fact]
    public void StoredAccountWinsWhenPresentWithWindowsElseTheDefaultRuleApplies()
    {
        var both = TwoAccounts();
        Assert.Equal(Desktop, WindowCardText.WindowCardAccount(both, "claude", Desktop));
        Assert.Null(WindowCardText.WindowCardAccount(both, "claude", ""));
        Assert.Null(WindowCardText.WindowCardAccount(both, "claude", null));

        // Stored account gone from the payload => today's rule (primary).
        Assert.Null(WindowCardText.WindowCardAccount(both, "claude", Dir));
        // Stored account present but errored with no windows => fallback.
        var errored = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
            Card(Desktop, "S", error: "expired"));
        Assert.Null(WindowCardText.WindowCardAccount(errored, "claude", Desktop));
        // Stored primary without windows => first other account with windows.
        var desktopOnly = Payload(
            Card(null, "P", error: "x"),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));
        Assert.Equal(Desktop, WindowCardText.WindowCardAccount(desktopOnly, "claude", ""));
    }

    private static QuotaLensProjection.Client ClientFor(
        string? stored, string? storedTab = null, IReadOnlyCollection<string>? localClients = null,
        IReadOnlyList<UsageAttribution.Record>? records = null)
    {
        var messages = new[] { new WindowMessage(97 * Hour * 1000 + 1, "claude", "anthropic", "m", 10, 0, 0, 0, 0, 1.0, true) };
        var history = new[]
        {
            new QuotaHistorySeries("claude", "P", "session.v1", [Sample(40, 96, true), Sample(50, 97, true)]),
            new QuotaHistorySeries("claude", "S", "session.v1", [Sample(5, 96, true), Sample(9, 97, true)]),
        };
        var graph = new UsagePayload(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []);
        return QuotaLensProjection.Build(
            history, TwoAccounts(), graph, new WindowUsage(messages, 3, 0),
            WindowEquivalence.FetchOutcome.Succeeded, WindowEquivalence.FetchOutcome.Succeeded,
            new UsageAttribution.Table(
                records ?? [new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Assigned("claude"))],
                IsWritable: true),
            year: null,
            new QuotaLensProjection.Selection("claude", storedTab ?? "", WindowCardAccount: stored, LocalUsageClients: localClients)).Client!;
    }

    /// <summary>A subscription used only through another client still has
    /// local records: OpenCode is the only present client and a confirmed
    /// opencode·openai → codex record makes the Codex tab scanned (macOS #468
    /// WCP2-attr). Controls: no record, an Excluded record, a record assigned
    /// to another owner, and a record for an absent client leave it
    /// quota-only.</summary>
    [Fact]
    public void ConfirmedAttributionFromAPresentClientCountsAsLocalRecords()
    {
        UsageAttribution.Record R(string client, UsageAttribution.State state) => new(client, "openai", state);
        string[] present = ["opencode"];
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords(
            "codex", present, [R("opencode", UsageAttribution.State.Assigned("codex"))]));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords("codex", present, []));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords(
            "codex", present, [R("opencode", UsageAttribution.State.Excluded)]));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords(
            "codex", present, [R("opencode", UsageAttribution.State.Assigned("claude"))]));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords(
            "codex", present, [R("kimi", UsageAttribution.State.Assigned("codex"))]));

        // Through the projection: the primary Claude card with only OpenCode
        // present is unattributed unless OpenCode usage is confirmed as Claude's.
        Assert.True(ClientFor(null, localClients: ["opencode"]).LocalUsageUnattributed);
        var viaOpenCode = ClientFor(null, localClients: ["opencode"], records:
            [new UsageAttribution.Record("opencode", "anthropic", UsageAttribution.State.Assigned("claude"))]);
        Assert.False(viaOpenCode.LocalUsageUnattributed);
    }

    [Fact]
    public void NonPrimaryCardReadsOnlyItsOwnEntriesAndShowsNoLocalUsage()
    {
        var primary = ClientFor(null);
        Assert.False(primary.LocalUsageUnattributed);
        Assert.Null(primary.SelectedAccount);
        Assert.Equal("P", primary.Selected!.Id.AccountScope);
        Assert.NotEmpty(primary.Mine);
        Assert.Equal(2, primary.Accounts.Count);

        var desktop = ClientFor(Desktop);
        Assert.True(desktop.LocalUsageUnattributed);
        Assert.Equal(Desktop, desktop.SelectedAccount);
        Assert.All(desktop.Tabs, tab => Assert.Equal("S", tab.Id.AccountScope));
        Assert.Equal("S", desktop.Selected!.Id.AccountScope);
        Assert.Empty(desktop.Mine);
        Assert.Empty(desktop.Messages);
        Assert.Null(desktop.LiveEquivalence);
        Assert.Equal(0, desktop.UndatedCount);
        Assert.Equal("Local usage can't be attributed to this account yet.", WindowCardText.LocalUsageUnattributed());
    }

    [Fact]
    public void QuotaOnlyTabTreatsEvenThePrimaryAsUnattributed()
    {
        var primary = ClientFor(null, localClients: ["codex"]);
        Assert.True(primary.LocalUsageUnattributed);
        Assert.Empty(primary.Mine);
        Assert.Empty(primary.Messages);
        Assert.Null(primary.LiveEquivalence);
        Assert.Equal(0, primary.UndatedCount);
        Assert.Equal(WindowCardText.LocalUsageUnattributed(), WindowCardText.ZoneUsage([], unattributed: primary.LocalUsageUnattributed).Empty);
    }

    [Fact]
    public void TabWithRecordsOrUnknownPresenceKeepsThePrimaryScanned()
    {
        foreach (var known in new[] { new[] { "claude" }, ["claude-code"] })
        {
            var withRecords = ClientFor(null, localClients: known);
            Assert.False(withRecords.LocalUsageUnattributed);
            Assert.NotEmpty(withRecords.Mine);
        }

        var unknown = ClientFor(null, localClients: null);
        Assert.False(unknown.LocalUsageUnattributed);
        Assert.NotEmpty(unknown.Mine);
        // Non-primary stays unattributed whatever the presence says.
        Assert.True(ClientFor(Desktop, localClients: ["claude"]).LocalUsageUnattributed);
    }

    [Fact]
    public void GroupedTabCountsAnyMemberAsHavingRecords()
    {
        // Antigravity tab = antigravity + antigravity-cli; the CLI carries the usage.
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("antigravity", ["antigravity-cli"], []));
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("antigravity-cli", ["antigravity-cli"], []));
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("antigravity", ["antigravity"], []));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords("antigravity", ["claude"], []));
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords("antigravity-cli", [], []));
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("antigravity", null, []));
    }

    [Fact]
    public void HeaderNamesTheResolvedAccountOnlyWhenItIsNotThePrimary()
    {
        Assert.Null(WindowCardText.HeaderAccountLabel(TwoAccounts(), "claude", null));
        // Primary errored, exactly one other account live: no pills, still labelled.
        var desktopOnly = Payload(
            Card(null, "P", error: "x"),
            Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));
        Assert.Empty(WindowCardText.AccountPills(desktopOnly, "claude"));
        Assert.Equal("Claude Desktop", WindowCardText.HeaderAccountLabel(
            desktopOnly, "claude", WindowCardText.WindowCardAccount(desktopOnly, "claude")));
        var withDir = Payload(
            Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")),
            Card(Dir, "D", null, Window("session.v1", "Session", 60, "session.v1")));
        Assert.Equal("Claude · team-b", WindowCardText.HeaderAccountLabel(withDir, "claude", Dir));
        Assert.Equal("Claude Desktop · 5h", WindowCardText.WithAccountLabel("5h", "Claude Desktop"));
        Assert.Equal("5h", WindowCardText.WithAccountLabel("5h", null));
        Assert.Equal("Claude Desktop", ClientFor(Desktop).AccountLabel);
        Assert.Null(ClientFor(null).AccountLabel);
    }

    [Fact]
    public void OverviewEquivalenceSkipsNonPrimaryAndUnmatchedSeriesOfMultiCardClients()
    {
        var quota = TwoAccounts(); // primary scope P, Desktop scope S
        Assert.True(QuotaLensProjection.LocalUsageScopable(quota, Series("P")));
        Assert.False(QuotaLensProjection.LocalUsageScopable(quota, Series("S")));
        Assert.False(QuotaLensProjection.LocalUsageScopable(quota, Series("old")));
        // Single-card client / no payload: unchanged.
        var one = Payload(Card(null, "P", null, Window("session.v1", "Session", 80, "session.v1")));
        Assert.True(QuotaLensProjection.LocalUsageScopable(one, Series("old")));
        Assert.True(QuotaLensProjection.LocalUsageScopable(null, Series("S")));
        Assert.True(QuotaLensProjection.LocalUsageScopable(Payload(), Series("S")));
        // A lone non-primary card: nothing is scopable, not even its own series.
        var lone = Payload(Card(Desktop, "S", null, Window("session.v1", "Session", 30, "session.v1")));
        Assert.False(QuotaLensProjection.LocalUsageScopable(lone, Series("S")));
        Assert.False(QuotaLensProjection.LocalUsageScopable(lone, Series("P")));

        var graph = new UsagePayload(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []);
        var model = QuotaLensProjection.Build(
            [Series("P"), Series("S")], quota, graph, new WindowUsage([], 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded, WindowEquivalence.FetchOutcome.Succeeded,
            new UsageAttribution.Table([], IsWritable: true), year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, ""));
        var id = Assert.Single(model.Overview.Equivalences).Key;
        Assert.Equal("P", id.AccountScope);
    }

    [Fact]
    public void ZoneUsageSaysUnattributedNotNoneForANonPrimaryCard()
    {
        Assert.Equal("No usage in this interval", WindowCardText.ZoneUsage([]).Empty);
        Assert.Equal(
            WindowCardText.LocalUsageUnattributed(), WindowCardText.ZoneUsage([], unattributed: true).Empty);
    }

    [Fact]
    public void AStoredTabOfAnotherAccountFallsBackToTheChosenAccountsDefaultTab()
    {
        // The stored tab names the PRIMARY's window; the Desktop account does
        // not have that id, so its own default tab shows.
        var desktop = ClientFor(Desktop, storedTab: "claude|P|session.v1");
        Assert.Equal("S", desktop.Selected!.Id.AccountScope);
    }

    [Fact]
    public void ThePrimaryFallsBackToStoredSeriesOnlyWithoutAPayload()
    {
        var history = new[] { Series("P"), Series("S") };

        Assert.Equal(2, WindowCardText.Tabs(history, null, "claude", null).Count);
        Assert.Equal(2, WindowCardText.Tabs(history, null, "claude", "").Count);
        // A primary agent with a scope filters by it, "" being the primary.
        var tab = Assert.Single(WindowCardText.Tabs(history, TwoAccounts(), "claude", ""));
        Assert.Equal("P", tab.Id.AccountScope);
        // A non-primary account with no payload gets nothing.
        Assert.Empty(WindowCardText.Tabs(history, null, "claude", Desktop));
    }
}
