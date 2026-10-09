using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The classification page's pure derivation, ported from
/// TokenBarCore/UsageAttributionSettings.swift.</summary>
public class UsageAttributionSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ModelReportEntry Entry(
        string client, string provider, string model, long total = 100, double cost = 1.0) =>
        new(client, model, provider, 0, 0, 0, 0, 0, total, 1, cost);

    // MARK: - the load-bearing distinction: a suggestion is not a classification

    [Fact]
    public void ASuggestionDoesNotClassifyUntilItIsAccepted()
    {
        var store = new SettingsStore(StorePath);
        // The documented example: a Claude Code session routed through a gateway
        // to an OpenAI model, whose spend is the Codex subscription's.
        var entry = Entry("claude", "openai", "gpt-5");
        ModelReportEntry[] entries = [entry];
        string[] targets = ["codex"];

        var proposals = UsageAttributionSettings.SuggestionRecords(entries, [], targets);
        Assert.Equal(
            new UsageAttribution.Record("claude", "openai", UsageAttribution.State.Assigned("codex")),
            Assert.Single(proposals));

        // Storing the proposal must not classify anything.
        Assert.Null(UsageAttributionSettings.WriteRecords(
            store, UsageAttribution.SuggestionsKey, proposals));

        var confirmed = UsageAttribution.Confirmed(store);
        var suggestions = UsageAttribution.Suggestions(store);
        var rows = UsageAttributionSettings.Rows(entries, confirmed.Records, suggestions.Records);
        var row = Assert.Single(rows);

        Assert.Equal(UsageAttribution.State.Unassigned, row.State);
        Assert.Equal(UsageAttribution.State.Assigned("codex"), row.SuggestedState);
        Assert.Equal(UsageAttribution.State.Unassigned, UsageAttribution.EffectiveState(entry, store));

        // Only explicit acceptance writes a confirmed record.
        var accepted = UsageAttributionSettings.AcceptanceRecords(rows);
        Assert.Null(UsageAttributionSettings.WriteRecords(
            store, UsageAttribution.ConfirmedKey, accepted));

        Assert.Equal(UsageAttribution.State.Assigned("codex"), UsageAttribution.EffectiveState(entry, store));
        var settled = Assert.Single(UsageAttributionSettings.Rows(
            entries, UsageAttribution.Confirmed(store).Records, UsageAttribution.Suggestions(store).Records));
        Assert.Equal(UsageAttribution.State.Assigned("codex"), settled.State);
        // A classified row no longer carries a proposal.
        Assert.Null(settled.SuggestedState);
    }

    [Fact]
    public void AcceptanceRecordsSkipRowsThatAreAlreadyClassified()
    {
        UsageAttributionSettings.Row[] rows =
        [
            new("a", "openai", 1, 1, UsageAttribution.State.Assigned("codex"), UsageAttribution.State.Excluded),
            new("b", "openai", 1, 1, UsageAttribution.State.Unassigned, UsageAttribution.State.Excluded),
            new("c", "openai", 1, 1, UsageAttribution.State.Unassigned, null),
        ];

        var accepted = UsageAttributionSettings.AcceptanceRecords(rows);
        Assert.Equal(
            new UsageAttribution.Record("b", "openai", UsageAttribution.State.Excluded),
            Assert.Single(accepted));
    }

    // MARK: - rows

    [Fact]
    public void RowsAggregatePerClientProviderInFirstAppearanceOrderAndDropEmptySources()
    {
        ModelReportEntry[] entries =
        [
            Entry("claude", "anthropic", "m1", 10, 0.5),
            Entry("claude", "anthropic", "m2", 5, 0.25),
            Entry("claude", "openai", "m1", 0, 0),
            Entry("opencode", "openai", "m1", 7, 0.1),
        ];

        var rows = UsageAttributionSettings.Rows(entries, [], []);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("claude", row.Client);
                Assert.Equal("anthropic", row.Provider);
                Assert.Equal(15, row.Tokens);
                Assert.Equal(0.75, row.Cost, 10);
            },
            row =>
            {
                Assert.Equal("opencode", row.Client);
                Assert.Equal("openai", row.Provider);
                Assert.Equal(7, row.Tokens);
            });
    }

    [Fact]
    public void PageStateSeparatesUnavailableFromEmpty()
    {
        Assert.Equal(
            UsageAttributionSettings.PageState.Loading,
            UsageAttributionSettings.ResolvePageState(hasReport: false, rowCount: 0, isLoading: true));
        Assert.Equal(
            UsageAttributionSettings.PageState.Unavailable,
            UsageAttributionSettings.ResolvePageState(hasReport: false, rowCount: 0, isLoading: false));
        Assert.Equal(
            UsageAttributionSettings.PageState.Empty,
            UsageAttributionSettings.ResolvePageState(hasReport: true, rowCount: 0, isLoading: false));
        Assert.Equal(
            UsageAttributionSettings.PageState.Rows,
            UsageAttributionSettings.ResolvePageState(hasReport: true, rowCount: 1, isLoading: false));
    }

    // MARK: - suggestionTarget

    [Fact]
    public void OwnSubscriptionWinsEvenWithoutAQuotaSnapshot()
    {
        // UsageAttributionSettings.swift:409-411 — the table is asked directly,
        // not the snapshot-filtered owners list.
        Assert.Equal(
            UsageAttribution.State.Assigned("cursor"),
            UsageAttributionSettings.SuggestionTarget("cursor", "anthropic", []));
    }

    [Fact]
    public void GrokBotCoversOnlyItsOwnRowsNotOtherClients()
    {
        // Grok Bot is an own-client-only plan: with both quotas present, a
        // Codex row served by xai is still SuperGrok's (as before Grok Bot
        // got a table entry), not ambiguous between grok and grok-bot.
        Assert.Equal(
            UsageAttribution.State.Assigned("grok"),
            UsageAttributionSettings.SuggestionTarget("codex", "xai", ["grok", "grok-bot"]));
        Assert.Equal(
            UsageAttribution.State.Assigned("grok-bot"),
            UsageAttributionSettings.SuggestionTarget("grok-bot", "xai", ["grok", "grok-bot"]));
    }

    [Fact]
    public void GrokBotUsageIsItsOwnPlansSpendNotSuperGrok()
    {
        // The engine re-tags Grok Bot events from the Cursor usage API as
        // (grok-bot, xai). Without a grok-bot row the page offered no suggestion;
        // with it they are Grok Bot's own plan, even when a SuperGrok (grok)
        // quota is present.
        Assert.Equal(
            UsageAttribution.State.Assigned("grok-bot"),
            UsageAttributionSettings.SuggestionTarget("grok-bot", "xai", ["grok"]));
        // Control: Grok Build's own rows are unchanged.
        Assert.Equal(
            UsageAttribution.State.Assigned("grok"),
            UsageAttributionSettings.SuggestionTarget("grok", "xai", ["grok"]));
    }

    [Fact]
    public void TheCliFoldsOntoTheSubscriptionItSpends()
    {
        // quotaOwner is why this is not "API spend": antigravity-cli draws on the
        // antigravity subscription (ClientRegistry.swift:124).
        Assert.Equal(
            UsageAttribution.State.Assigned("antigravity"),
            UsageAttributionSettings.SuggestionTarget("antigravity-cli", "google", []));
    }

    [Fact]
    public void ABoundProviderReachedFromElsewhereIsProposedAsApiSpend()
    {
        // anthropic is subscription-bound and only the user's own Claude plan
        // covers it, so nothing is eligible and the proposal is `excluded`.
        Assert.Equal(
            UsageAttribution.State.Excluded,
            UsageAttributionSettings.SuggestionTarget("codex", "anthropic", ["claude"]));

        // A reseller is unaffected by the bound-provider rule.
        Assert.Equal(
            UsageAttribution.State.Assigned("copilot"),
            UsageAttributionSettings.SuggestionTarget("codex", "anthropic", ["copilot"]));

        // Two eligible resellers: nothing can be said.
        Assert.Null(UsageAttributionSettings.SuggestionTarget(
            "codex", "anthropic", ["copilot", "cursor"]));
    }

    [Fact]
    public void ASourceTheTableSaysNothingAboutGetsNoSuggestion()
    {
        // `hermes` has no entry in subscriptionProviderMap: absence of knowledge
        // is not evidence of API spend (UsageAttributionSettings.swift:425-430).
        Assert.Null(UsageAttributionSettings.SuggestionTarget("hermes", "anthropic", ["claude"]));
    }

    [Fact]
    public void ADeclaredRouterSpendsTheSubscriptionItIsSignedInto()
    {
        var payload = new AgentUsagePayload("now", [], ["Codex"]);
        var routed = UsageAttributionSettings.RoutedSubscriptionsFrom(payload);
        Assert.Equal(["codex"], routed.For("opencode"));

        Assert.Equal(
            UsageAttribution.State.Assigned("codex"),
            UsageAttributionSettings.SuggestionTarget("opencode", "openai", [], routed));

        // Signed into two subscriptions that both cover the provider: ambiguous.
        var both = UsageAttributionSettings.RoutedSubscriptionsFrom(
            new AgentUsagePayload("now", [], ["Codex", "Copilot"]));
        Assert.Null(UsageAttributionSettings.SuggestionTarget("opencode", "openai", [], both));
    }

    [Fact]
    public void SubscriptionClientsKeepConfiguredSnapshotsAndOpencodeLabels()
    {
        var withIdentity = new AgentUsageSnapshot(
            "claude", "oauth", "now", [], new AgentIdentity("person@example.com", "Max"));
        var placeholder = new AgentUsageSnapshot("grok", "oauth", "now", []);
        var payload = new AgentUsagePayload("now", [withIdentity, placeholder], ["Gemini"]);

        Assert.Equal(["claude", "antigravity"], UsageAttributionSettings.SubscriptionClients(payload));
    }

    [Fact]
    public void SubscriptionClientsNeverAdmitTheOpencodeRouterEvenWithItsOwnQuota()
    {
        // The OpenCode Go card publishes an `opencode` snapshot with windows and a
        // plan. That would pass the capability filter, but a router is never an
        // attribution target. The `kiro` snapshot of the same shape is the control:
        // it proves the filter would admit such a snapshot at all.
        UsageWindow[] windows = [new UsageWindow("Weekly", 63, 37, CardId: "weekly.v1")];
        var opencode = new AgentUsageSnapshot(
            "opencode", "api", "now", windows, new AgentIdentity(null, "Go"));
        var kiro = new AgentUsageSnapshot("kiro", "oauth", "now", windows);
        var payload = new AgentUsagePayload("now", [opencode, kiro], []);

        Assert.Equal(["kiro"], UsageAttributionSettings.SubscriptionClients(payload));
    }

    [Theory]
    // The four renames, from ClientRegistry.subscriptionLabelAliases.
    [InlineData("Codex", "codex")]
    [InlineData("Claude", "claude")]
    [InlineData("Copilot", "copilot")]
    [InlineData("Gemini", "antigravity")]
    // Capitalized provider keys resolve through providerOwnClient…
    [InlineData("Xai", "grok")]
    [InlineData("Anthropic", "claude")]
    // …with the plan qualifier trimmed off the leading vendor segment.
    [InlineData("Minimax-coding-plan", "micode")]
    [InlineData("Kimi-for-coding", "kimi")]
    // A vendor segment that names a client selling a plan is accepted as-is…
    [InlineData("Kiro", "kiro")]
    // …while a registered id that owns no subscription names no subscription.
    [InlineData("Crush", null)]
    public void OpencodeLabelsResolveToTheSubscriptionTheyName(string label, string? expected) =>
        Assert.Equal(expected, UsageAttributionSettings.SubscriptionClientForLabel(label));

    // MARK: - signature

    [Fact]
    public void SignatureSeparatesNoTargetsFromTargetsNotYetKnown()
    {
        ModelReportEntry[] entries = [Entry("claude", "anthropic", "m1")];
        Assert.NotEqual(
            UsageAttributionSettings.Signature(entries, [], targetsKnown: true),
            UsageAttributionSettings.Signature(entries, [], targetsKnown: false));

        // Routing is an independent input even when the target list is identical.
        var routed = UsageAttributionSettings.RoutedSubscriptionsFrom(
            new AgentUsagePayload("now", [], ["Codex"]));
        Assert.NotEqual(
            UsageAttributionSettings.Signature(entries, ["codex"]),
            UsageAttributionSettings.Signature(entries, ["codex"], routedSubscriptions: routed));

        // Cost changes the signature even when the token totals do not.
        Assert.NotEqual(
            UsageAttributionSettings.Signature([Entry("claude", "anthropic", "m1", 1, 1.0)], []),
            UsageAttributionSettings.Signature([Entry("claude", "anthropic", "m1", 1, 2.0)], []));
    }

    // MARK: - the registry members this slice ported

    [Theory]
    [InlineData("antigravity-cli", "antigravity")]
    [InlineData("antigravity", "antigravity")]
    [InlineData("claude", "claude")]
    [InlineData("unregistered", "unregistered")]
    public void QuotaOwnerFoldsOnlyTheAntigravityCli(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.QuotaOwner(id));

    [Fact]
    public void SubscriptionLabelAliasesAreTheFourRenames()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Codex"] = "codex",
                ["Claude"] = "claude",
                ["Copilot"] = "copilot",
                ["Gemini"] = "antigravity",
            },
            ClientRegistry.SubscriptionLabelAliases);

        // A rename, and a passthrough that lowercases.
        Assert.Equal("antigravity", ClientRegistry.ClientIdForSubscriptionLabel("Gemini"));
        Assert.Equal("xai", ClientRegistry.ClientIdForSubscriptionLabel("Xai"));

        // Every alias target and every subscription in the provider map has to be
        // a registered client, or it names a bucket the app cannot render.
        Assert.All(
            ClientRegistry.SubscriptionLabelAliases.Values,
            id => Assert.Contains(id, ClientRegistry.AllIds));
        Assert.All(
            UsageAttributionSettings.SubscriptionProviderMap.Keys,
            id => Assert.Contains(id, ClientRegistry.AllIds));
        Assert.All(
            UsageAttributionSettings.ProviderOwnClient.Values,
            id => Assert.Contains(id, ClientRegistry.AllIds));
    }

    [Fact]
    public void EverySubscriptionServesOnlyProvidersSomePolicyCovers()
    {
        // The Swift self-test at UsageAttributionSettings.swift:443-447 relies on
        // this: a provider a subscription serves must be either bound or
        // cross-agent, or suggestionTarget's backstop branch would be reachable.
        foreach (var providers in UsageAttributionSettings.SubscriptionProviderMap.Values)
        {
            Assert.All(providers, provider => Assert.True(
                UsageAttributionSettings.SubscriptionBoundProviders.Contains(provider)
                || UsageAttributionSettings.CrossAgentSubscriptionProviders.Contains(provider),
                $"{provider} is served by a subscription but covered by no policy"));
        }
    }
}
