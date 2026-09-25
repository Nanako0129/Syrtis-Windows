using Xunit;

namespace TokenBar.Core.Tests;

public class ClientRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private SettingsStore NewStore() => new(Path.Combine(_dir, "settings.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void KnownClientHasBrandStyle()
    {
        var style = ClientRegistry.Style("claude");
        Assert.Equal("Claude Code", style.DisplayName);
        Assert.Equal("#d97706", style.Color);
    }

    [Fact]
    public void UnknownClientTitleCasesWithGreyDisc()
    {
        var style = ClientRegistry.Style("mystery");
        Assert.Equal("Mystery", style.DisplayName);
        Assert.Equal("#6b7280", style.Color);
    }

    [Theory]
    [InlineData("claude", "Claude")]       // " Code" dropped
    [InlineData("codex", "Codex")]         // no suffix to drop (not surface-scoped)
    [InlineData("cursor", "Cursor")]       // no suffix to drop (not surface-scoped)
    [InlineData("amp", "Amp")]             // no suffix
    [InlineData("antigravity-cli", "Antigravity CLI")] // base collides with the IDE client
    public void ShortNameDropsFormFactorSafely(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.ShortName(id));

    // --- Tabs order & visibility delta ---

    [Fact]
    public void GrokBuildIsRegistered()
    {
        var style = ClientRegistry.Style("grok");
        Assert.Equal("Grok Build", style.DisplayName);
        Assert.Equal("#1f2937", style.Color);
    }

    [Fact]
    public void AllIdsAreSortedAndIncludeGrok()
    {
        var ids = ClientRegistry.AllIds;
        Assert.Contains("grok", ids);
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
    }

    // Both carry the same grey the unregistered fallback uses, so Style()
    // alone cannot tell a registered "junie" from an unregistered one. The
    // display name can: the fallback only upper-cases the first letter, so an
    // unregistered "opencodereview" would render "Opencodereview".
    [Theory]
    [InlineData("junie", "Junie")]
    [InlineData("opencodereview", "OpenCodeReview")]
    public void NewClientsAreRegisteredWithTheirOwnCasing(string id, string expected)
    {
        Assert.Contains(id, ClientRegistry.AllIds);
        Assert.Equal(expected, ClientRegistry.Style(id).DisplayName);
    }

    [Theory]
    [InlineData("claude-code", "claude")]
    [InlineData("codex-cli", "codex")]
    [InlineData("gemini-cli", "gemini")]
    [InlineData("antigravity-cli", "antigravity-cli")] // NOT folded — distinct client
    [InlineData("amp", "amp")]
    public void CanonicalClientAliasesExplicitOnly(string id, string expected) =>
        Assert.Equal(expected, ClientRegistry.CanonicalClient(id));

    [Fact]
    public void ParseIdSetAndListTolerateEmptyAndBlanks()
    {
        Assert.Empty(ClientRegistry.ParseIdSet(""));
        Assert.Empty(ClientRegistry.ParseIdList(""));
        Assert.Equal(new HashSet<string> { "a", "b" }, ClientRegistry.ParseIdSet("a,,b"));
        Assert.Equal(["a", "b", "c"], ClientRegistry.ParseIdList("a,b,c"));
    }

    [Fact]
    public void QuotaExcludedIsUnionOfHiddenAndLimitsHidden()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "claude,codex");
        store.SetString(ClientRegistry.LimitsHiddenKey, "codex,gemini");

        Assert.Equal(
            new HashSet<string> { "claude", "codex", "gemini" },
            ClientRegistry.QuotaExcludedClients(store));
    }

    [Fact]
    public void KnownLimitsClientsDedupesKeepingPresentThenQuotaOrder()
    {
        // present carries claude+codex; only codex has a known limit (quota),
        // claude is offered via placeholder; antigravity is quota-only.
        var known = ClientRegistry.KnownLimitsClients(
            present: ["claude", "codex", "mux"],
            quotaIds: ["codex", "antigravity"],
            placeholders: new HashSet<string> { "claude" });

        // mux has neither placeholder nor quota → dropped. codex appears once.
        Assert.Equal(["claude", "codex", "antigravity"], known);
    }

    [Fact]
    public void OrderedClientsSortsBySavedOrderAppendingUnknownStably()
    {
        var ordered = ClientRegistry.OrderedClients(
            ["gemini", "claude", "codex", "amp"], orderRaw: "codex,claude");
        // codex, claude by saved order; gemini, amp keep their incoming order.
        Assert.Equal(["codex", "claude", "gemini", "amp"], ordered);
    }

    [Fact]
    public void OrderedClientsWithEmptyOrderIsIdentity() =>
        Assert.Equal(["b", "a"], ClientRegistry.OrderedClients(["b", "a"], orderRaw: ""));

    [Fact]
    public void DisplayClientsFiltersHiddenThenOrders()
    {
        var display = ClientRegistry.DisplayClients(
            present: ["gemini", "claude", "codex"], hiddenRaw: "gemini", orderRaw: "codex,claude");
        Assert.Equal(["codex", "claude"], display);
    }

    [Fact]
    public void DisplayClientsStoreOverloadReadsBothKeys()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "gemini");
        store.SetString(ClientRegistry.TabOrderKey, "codex,claude");

        Assert.Equal(
            ["codex", "claude"],
            ClientRegistry.DisplayClients(["gemini", "claude", "codex"], store));
    }

    [Fact]
    public void ResolveSelectionCanonicalizesPresentAndDefaultsToOverview()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude-code", "claude", "codex-cli"],
            quotaIds: [],
            hiddenRaw: "",
            orderRaw: "codex,claude",
            activeTab: null);

        Assert.Equal(ClientRegistry.OverviewTab, selection.ActiveTab);
        Assert.Equal(["codex", "claude"], selection.DisplayClients);
        Assert.Equal(selection.DisplayClients, selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionKeepsVisibleCanonicalActiveClient()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            quotaIds: [],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "codex-cli");

        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionNormalizesHiddenOrMissingActiveClientToOverview()
    {
        var hidden = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            quotaIds: [],
            hiddenRaw: "codex",
            orderRaw: "",
            activeTab: "codex");
        var missing = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            quotaIds: [],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "gemini");

        Assert.Equal(ClientRegistry.OverviewTab, hidden.ActiveTab);
        Assert.Equal(["claude"], hidden.SelectedClients);
        Assert.Equal(ClientRegistry.OverviewTab, missing.ActiveTab);
        Assert.Equal(["claude", "codex"], missing.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionKeepsAllHiddenAsEmptyOverview()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex"],
            quotaIds: [],
            hiddenRaw: "claude,codex",
            orderRaw: "",
            activeTab: "codex");

        Assert.Equal(ClientRegistry.OverviewTab, selection.ActiveTab);
        Assert.Empty(selection.DisplayClients);
        Assert.Empty(selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionReorderDoesNotChangeActiveMembership()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "codex", "gemini"],
            quotaIds: [],
            hiddenRaw: "",
            orderRaw: "gemini,claude,codex",
            activeTab: "codex");

        Assert.Equal(["gemini", "claude", "codex"], selection.DisplayClients);
        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionStoreOverloadReadsOnlyTabKeys()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "gemini");
        store.SetString(ClientRegistry.TabOrderKey, "codex,claude");
        store.SetString(ClientRegistry.ActiveTabKey, "codex");
        store.SetString(ClientRegistry.LimitsHiddenKey, "codex");

        var selection = ClientRegistry.ResolveSelection(
            ["gemini", "claude", "codex"], quotaIds: [], store);

        Assert.Equal(["codex", "claude"], selection.DisplayClients);
        Assert.Equal("codex", selection.ActiveTab);
        Assert.Equal(["codex"], selection.SelectedClients);
    }

    // --- Grouped tabs (Antigravity IDE + CLI) & quota-only tab sources ---

    [Fact]
    public void TabClientsAddsConfiguredQuotaOnlySourcesAndFoldsAntigravity()
    {
        // present carries local usage (claude, antigravity-cli); quotaIds adds
        // a configured quota-only source (copilot) and antigravity's own
        // quota card — the CLI and the IDE client fold onto one "antigravity"
        // tab rather than emitting two.
        var tabs = ClientRegistry.TabClients(
            present: ["claude", "antigravity-cli"],
            quotaIds: ["antigravity", "copilot"]);

        Assert.Equal(["claude", "antigravity", "copilot"], tabs);
    }

    [Fact]
    public void TabSliceAndLabelIdentifyOnlyTheAntigravityGroup()
    {
        Assert.Equal(["antigravity", "antigravity-cli"], ClientRegistry.TabSlice("antigravity"));
        Assert.Equal(["claude"], ClientRegistry.TabSlice("claude"));
        Assert.Equal("Antigravity", ClientRegistry.TabLabel("antigravity"));
        Assert.Equal("Claude", ClientRegistry.TabLabel("claude")); // ShortName fallback
        Assert.Equal("Antigravity", ClientRegistry.TabDisplayName("antigravity"));
        Assert.Equal("Claude Code", ClientRegistry.TabDisplayName("claude")); // full Style name
    }

    [Fact]
    public void HiddenTabClientsFoldsALegacyMemberEntryOntoTheGroupAndExpandsBack()
    {
        // Legacy: only "antigravity-cli" was ever stored (from before the
        // fold existed). It must still exclude the group's tab id.
        var fromMember = ClientRegistry.HiddenTabClients(
            new HashSet<string> { "antigravity-cli" });
        Assert.Equal(
            new HashSet<string> { "antigravity", "antigravity-cli" }, fromMember);

        // Fresh: the tab id was stored directly. It must still expand to
        // cover the CLI's usage rows.
        var fromTab = ClientRegistry.HiddenTabClients(new HashSet<string> { "antigravity" });
        Assert.Equal(new HashSet<string> { "antigravity", "antigravity-cli" }, fromTab);

        // An ungrouped id passes through unchanged in both directions.
        Assert.Equal(new HashSet<string> { "claude" }, ClientRegistry.HiddenTabClients(
            new HashSet<string> { "claude" }));
    }

    [Fact]
    public void QuotaExcludedClientsFoldsTabHiddenMemberEntryOntoTheGroup()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "antigravity-cli");
        Assert.Contains("antigravity", ClientRegistry.QuotaExcludedClients(store));
    }

    [Fact]
    public void QuotaExcludedClientsKeepsLimitsHiddenMemberSpecific()
    {
        // Limits-hidden must NOT fold: hiding only the CLI's limits card must
        // not also exclude the IDE client's quota card.
        var store = NewStore();
        store.SetString(ClientRegistry.LimitsHiddenKey, "antigravity-cli");
        Assert.DoesNotContain("antigravity", ClientRegistry.QuotaExcludedClients(store));
    }

    [Fact]
    public void TabOrderFoldsMemberIdsToTheGroupDeduped()
    {
        Assert.Equal(
            ["claude", "antigravity", "codex"],
            ClientRegistry.TabOrder("claude,antigravity-cli,antigravity,codex"));
    }

    [Fact]
    public void UnhidingTheAntigravityTabRestoresItToTheRow()
    {
        var store = NewStore();
        store.SetString(ClientRegistry.TabHiddenKey, "antigravity-cli");

        // Still resolvable as hidden beforehand.
        Assert.Contains("antigravity", ClientRegistry.HiddenTabClients(store));

        // Un-hide by clearing every member of the group, as the Settings
        // toggle does.
        store.SetString(ClientRegistry.TabHiddenKey, "");
        var tabs = ClientRegistry.DisplayClients(
            ClientRegistry.TabClients(present: [], quotaIds: ["antigravity"]), store);
        Assert.Contains("antigravity", tabs);
    }

    [Fact]
    public void ResolveSelectionForOverviewIsPresentUsageMinusHiddenNotTheTabRow()
    {
        // present = local usage clients only (antigravity-cli carries usage,
        // not the IDE's "antigravity"); configured = a quota-only source
        // (copilot) with no usage lens of its own.
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "antigravity-cli"],
            quotaIds: ["copilot"],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: null);

        Assert.Equal(ClientRegistry.OverviewTab, selection.ActiveTab);
        // Tab row: usage ∪ configured, grouped.
        Assert.Equal(["claude", "antigravity", "copilot"], selection.DisplayClients);
        // Overview selection: present usage clients only — never a quota-only
        // id with no usage lens.
        Assert.Equal(["claude", "antigravity-cli"], selection.SelectedClients);
        Assert.DoesNotContain("copilot", selection.SelectedClients);

        // With the Antigravity tab hidden, Overview drops both members.
        var withHiddenGroup = ClientRegistry.ResolveSelection(
            present: ["claude", "antigravity-cli"],
            quotaIds: ["copilot"],
            hiddenRaw: "antigravity",
            orderRaw: "",
            activeTab: null);
        Assert.DoesNotContain("antigravity-cli", withHiddenGroup.SelectedClients);
        Assert.DoesNotContain("antigravity", withHiddenGroup.DisplayClients);
    }

    [Fact]
    public void ResolveSelectionOnTheGroupedTabSelectsBothMembers()
    {
        var selection = ClientRegistry.ResolveSelection(
            present: ["claude", "antigravity-cli"],
            quotaIds: ["antigravity"],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "antigravity-cli"); // legacy stored id still resolves

        Assert.Equal("antigravity", selection.ActiveTab);
        Assert.Equal(["antigravity", "antigravity-cli"], selection.SelectedClients);
    }

    [Fact]
    public void ResolveSelectionQuotaOnlyActiveTabFallsBackToOverviewUntilConfigured()
    {
        // The active tab names a quota-only provider that has not been
        // reported configured yet (quotaIds empty pre-attempt) — it cannot
        // resolve, so the render falls back to Overview.
        var beforeConfigured = ClientRegistry.ResolveSelection(
            present: ["claude"],
            quotaIds: [],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "copilot");
        Assert.Equal(ClientRegistry.OverviewTab, beforeConfigured.ActiveTab);

        // Once the quota payload lists it configured, the same stored tab id
        // resolves normally.
        var afterConfigured = ClientRegistry.ResolveSelection(
            present: ["claude"],
            quotaIds: ["copilot"],
            hiddenRaw: "",
            orderRaw: "",
            activeTab: "copilot");
        Assert.Equal("copilot", afterConfigured.ActiveTab);
        Assert.Equal(["copilot"], afterConfigured.SelectedClients);
    }

    [Theory]
    [InlineData("a", "c", "b,c,a,d")] // drag down: insert after target
    [InlineData("d", "b", "a,d,b,c")] // drag up: insert before target
    [InlineData("a", "a", "a,b,c,d")] // same id: unchanged
    public void ReorderIsDirectionAware(string from, string to, string expected)
    {
        var result = ClientRegistry.Reorder(["a", "b", "c", "d"], from, to);
        Assert.Equal(expected.Split(','), result);
    }

    [Fact]
    public void MergeReorderPreservesOffscreenPositions()
    {
        // full universe a..e; visible subset only a,c,e (b,d hidden off-screen).
        // Drag a after e within the visible subset → a,c,e becomes c,e,a.
        var merged = ClientRegistry.MergeReorder(
            full: ["a", "b", "c", "d", "e"], visible: ["a", "c", "e"], from: "a", to: "e");
        // b stays at slot 1, d stays at slot 3; visible slots refill c,e,a.
        Assert.Equal(["c", "b", "e", "d", "a"], merged);
    }

    [Fact]
    public void MigrateLegacyOrderKeyFoldsOnceThenIsIdempotent()
    {
        var store = NewStore();
        store.SetString("tokenbar.limits.order", "codex,claude");

        ClientRegistry.MigrateLegacyOrderKey(store);
        Assert.Equal("codex,claude", store.GetString(ClientRegistry.TabOrderKey));

        // Idempotent: a second run must not overwrite a user-changed new value.
        store.SetString(ClientRegistry.TabOrderKey, "claude");
        ClientRegistry.MigrateLegacyOrderKey(store);
        Assert.Equal("claude", store.GetString(ClientRegistry.TabOrderKey));
    }
}
