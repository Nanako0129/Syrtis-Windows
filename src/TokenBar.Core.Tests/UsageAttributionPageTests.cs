using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>Every decision the usage-attribution settings page makes, ported
/// from <c>SettingsPanel.swift</c>'s <c>usageAttributionPage</c> and
/// <c>attributionRow</c>. The page itself is in <c>SettingsWindow.cs</c>, which
/// no test project compiles — which is why the decisions are not in it.</summary>
public class UsageAttributionPageTests : IDisposable
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

    /// <summary>A payload that names one subscription. The opencode route is
    /// the shortest one to a target list, and it is a real one: an
    /// <c>auth.json</c> oauth entry is how a subscription with no meter of its
    /// own becomes a target.</summary>
    private static AgentUsagePayload Payload(params string[] labels) =>
        new("now", [], labels);

    // The documented example throughout: a Claude Code session routed through a
    // gateway to an OpenAI model, whose spend is the Codex subscription's.
    private static ModelReportEntry[] Entries() => [Entry("claude", "openai", "gpt-5")];

    private static UsageAttribution.Record CodexProposal() =>
        new("claude", "openai", UsageAttribution.State.Assigned("codex"));

    // MARK: - the suppression rule (SettingsPanel.swift:641-647)

    /// <summary>A stored suggestion proposes a target, and until the quota
    /// payload says which subscriptions exist there is nothing to check that
    /// target against. A payload that has not arrived is not the same as a
    /// source having no suggestion.</summary>
    [Fact]
    public void AMissingQuotaPayloadHidesEveryStoredSuggestion()
    {
        var suggestions = new UsageAttribution.Table([CodexProposal()], true);

        var suppressed = UsageAttributionPage.Resolve(
            Entries(), agentUsage: null, UsageAttribution.Table.Empty, suggestions, isLoading: false);
        Assert.Null(Assert.Single(suppressed.Rows).SuggestedState);
        Assert.Equal(0, suppressed.SuggestionCount);

        // The same table, the same rows, with the payload present.
        var shown = UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"), UsageAttribution.Table.Empty, suggestions, isLoading: false);
        Assert.Equal(
            UsageAttribution.State.Assigned("codex"),
            Assert.Single(shown.Rows).SuggestedState);
        Assert.Equal(1, shown.SuggestionCount);
    }

    /// <summary>Suppressing is not deleting. The alternative destroys real
    /// proposals every time the request happens to be in flight, so the
    /// reconciliation refuses to run at all without a payload.</summary>
    [Fact]
    public void AMissingQuotaPayloadWritesNothingOverTheStoredSuggestions()
    {
        var store = new SettingsStore(StorePath);
        Assert.Null(UsageAttributionSettings.WriteRecords(
            store, UsageAttribution.SuggestionsKey, [CodexProposal()]));
        var before = File.ReadAllText(StorePath);

        Assert.Null(UsageAttributionPage.RefreshSuggestions(store, Entries(), agentUsage: null));

        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(
            CodexProposal(),
            Assert.Single(UsageAttribution.Suggestions(new SettingsStore(StorePath)).Records));
    }

    /// <summary>And with a payload it does run: the proposal is derived and
    /// stored, and only then does the page show it.</summary>
    [Fact]
    public void AQuotaPayloadReconcilesTheProposalsAndTheyBecomeVisible()
    {
        var store = new SettingsStore(StorePath);
        Assert.Null(UsageAttributionPage.RefreshSuggestions(store, Entries(), Payload("Codex")));

        Assert.Equal(CodexProposal(), Assert.Single(UsageAttribution.Suggestions(store).Records));
        var view = UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Confirmed(store), UsageAttribution.Suggestions(store),
            isLoading: false);
        Assert.Equal(1, view.SuggestionCount);
        Assert.Contains("codex", view.TargetClients);
    }

    // MARK: - the accept-all count

    [Fact]
    public void TheAcceptAllCountIsTheNumberOfRowsCarryingASuggestion()
    {
        ModelReportEntry[] entries =
        [
            Entry("claude", "openai", "gpt-5"),      // proposal, unassigned
            Entry("opencode", "openai", "gpt-5"),    // proposal, unassigned
            Entry("zed", "openai", "gpt-5"),         // already classified
            Entry("crush", "cohere", "command"),     // nothing to propose
        ];
        var suggestions = new UsageAttribution.Table(
            [
                CodexProposal(),
                new("opencode", "openai", UsageAttribution.State.Excluded),
                new("zed", "openai", UsageAttribution.State.Assigned("codex")),
            ],
            true);
        var confirmed = new UsageAttribution.Table(
            [new("zed", "openai", UsageAttribution.State.Assigned("zed"))], true);

        var view = UsageAttributionPage.Resolve(
            entries, Payload("Codex"), confirmed, suggestions, isLoading: false);

        Assert.Equal(4, view.Rows.Count);
        Assert.Equal(2, view.SuggestionCount);
        // The count is the button's label, and it has to be the number of rows
        // the acceptance would actually write — a classified row keeps its
        // classification, whatever the suggestion table still holds for it.
        Assert.Equal(
            view.SuggestionCount,
            UsageAttributionSettings.AcceptanceRecords(view.Rows).Count);
    }

    // MARK: - the four page states

    [Fact]
    public void EachPageStateMapsToItsOwnContent()
    {
        var empty = UsageAttribution.Table.Empty;

        // No report and the request still running.
        Assert.Equal(
            UsageAttributionSettings.PageState.Loading,
            UsageAttributionPage.Resolve(null, Payload(), empty, empty, isLoading: true).State);

        // No report and the request finished without one. Distinct from Empty,
        // which is an answer about a report that did arrive.
        Assert.Equal(
            UsageAttributionSettings.PageState.Unavailable,
            UsageAttributionPage.Resolve(null, Payload(), empty, empty, isLoading: false).State);

        // A report that folded to no classifiable source.
        Assert.Equal(
            UsageAttributionSettings.PageState.Empty,
            UsageAttributionPage.Resolve(
                [Entry("claude", "openai", "gpt-5", total: 0, cost: 0)],
                Payload(), empty, empty, isLoading: false).State);

        var rows = UsageAttributionPage.Resolve(
            Entries(), Payload(), empty, empty, isLoading: false);
        Assert.Equal(UsageAttributionSettings.PageState.Rows, rows.State);
        Assert.Single(rows.Rows);
    }

    /// <summary>A report that arrives while the fetch flag is still set is
    /// still a report: loading only decides between the two null-report
    /// states.</summary>
    [Fact]
    public void AReportOutranksTheLoadingFlag()
    {
        Assert.Equal(
            UsageAttributionSettings.PageState.Rows,
            UsageAttributionPage.Resolve(
                Entries(), Payload(), UsageAttribution.Table.Empty, UsageAttribution.Table.Empty,
                isLoading: true).State);
    }

    // MARK: - acceptance writes through 5a's write path

    [Fact]
    public void AcceptAllConfirmsEveryProposalAndStopsProposingIt()
    {
        var store = new SettingsStore(StorePath);
        Assert.Null(UsageAttributionPage.RefreshSuggestions(store, Entries(), Payload("Codex")));
        var view = UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Confirmed(store), UsageAttribution.Suggestions(store),
            isLoading: false);

        Assert.Null(UsageAttributionPage.AcceptAll(store, view.Rows));

        var reread = new SettingsStore(StorePath);
        Assert.Equal(
            UsageAttribution.State.Assigned("codex"),
            UsageAttribution.EffectiveState(Entries()[0], reread));
        // An accepted proposal has stopped being one.
        Assert.Empty(UsageAttribution.Suggestions(reread).Records);
    }

    [Fact]
    public void AcceptAllWithNothingToAcceptTouchesNothing()
    {
        var store = new SettingsStore(StorePath);
        store.SetString("tokenbar.unrelated", "x");
        var before = File.ReadAllText(StorePath);

        var view = UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Table.Empty, UsageAttribution.Table.Empty, isLoading: false);
        Assert.Null(UsageAttributionPage.AcceptAll(store, view.Rows));

        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    /// <summary>The fail-closed boundary, reached through the page: a stored
    /// value this codec did not write is not replaced, and the refusal is
    /// reported rather than swallowed.</summary>
    [Fact]
    public void AForeignConfirmedValueRefusesTheAcceptanceAndKeepsItsBytes()
    {
        Directory.CreateDirectory(_dir);
        const string raw = "{\"tokenbar.usage.attribution.confirmed\": {\"a\": 1}}";
        File.WriteAllText(StorePath, raw);
        var store = new SettingsStore(StorePath);

        var view = UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Confirmed(store),
            new UsageAttribution.Table([CodexProposal()], true),
            isLoading: false);
        Assert.Equal(1, view.SuggestionCount);

        Assert.Equal(
            UsageAttributionSettings.WriteFailure.InvalidExistingValue,
            UsageAttributionPage.AcceptAll(store, view.Rows));
        Assert.Equal(raw, File.ReadAllText(StorePath));
    }

    [Fact]
    public void AForeignConfirmedValueRefusesASinglePickerChoiceToo()
    {
        Directory.CreateDirectory(_dir);
        const string raw = "{\"tokenbar.usage.attribution.confirmed\": 7}";
        File.WriteAllText(StorePath, raw);
        var store = new SettingsStore(StorePath);
        var row = Assert.Single(UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Confirmed(store), UsageAttribution.Table.Empty,
            isLoading: false).Rows);

        Assert.Equal(
            UsageAttributionSettings.WriteFailure.InvalidExistingValue,
            UsageAttributionPage.Save(store, row, UsageAttribution.State.Excluded));
        Assert.Equal(raw, File.ReadAllText(StorePath));
    }

    [Fact]
    public void APickerChoiceIsWrittenAsAConfirmedRecord()
    {
        var store = new SettingsStore(StorePath);
        var row = Assert.Single(UsageAttributionPage.Resolve(
            Entries(), Payload("Codex"),
            UsageAttribution.Table.Empty, UsageAttribution.Table.Empty, isLoading: false).Rows);

        Assert.Null(UsageAttributionPage.Save(store, row, UsageAttribution.State.Excluded));

        Assert.Equal(
            UsageAttribution.State.Excluded,
            UsageAttribution.EffectiveState(Entries()[0], new SettingsStore(StorePath)));
    }

    // MARK: - the picker's target list

    /// <summary>A target already confirmed, and one being suggested for a plan
    /// TokenBar draws no meter for, both fall outside the snapshot-derived
    /// list. Offering the suggestion while the picker cannot select it leaves
    /// "Accept all" as the only way to take it.</summary>
    [Fact]
    public void ThePickerOffersOutOfBandTargetsTheRowAlreadyCarries()
    {
        var row = new UsageAttributionSettings.Row(
            "claude", "anthropic", 1, 1,
            UsageAttribution.State.Assigned("zed"),
            UsageAttribution.State.Assigned("cursor"));

        var targets = UsageAttributionPage.PickerTargets(row, ["codex"]);

        Assert.Equal(["codex", "zed", "cursor"], targets);
    }

    [Fact]
    public void ThePickerDoesNotRepeatATargetItAlreadyLists()
    {
        var row = new UsageAttributionSettings.Row(
            "claude", "openai", 1, 1,
            UsageAttribution.State.Assigned("codex"), UsageAttribution.State.Assigned("codex"));

        Assert.Equal(["codex"], UsageAttributionPage.PickerTargets(row, ["codex"]));
    }

    // MARK: - copy

    /// <summary>A suggestion can propose "not a subscription" as readily as a
    /// target, so the label follows the proposed state rather than assuming an
    /// assignment.</summary>
    [Fact]
    public void TheSuggestionLabelFollowsTheProposedState()
    {
        Assert.Equal(
            "Suggested: counts toward Codex",
            UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Assigned("codex")));
        Assert.Equal(
            "Suggested: not a subscription",
            UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Excluded));
        Assert.Equal(
            "Unassigned",
            UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Unassigned));
    }

    [Fact]
    public void AnEmptyProviderIsNamedRatherThanLeftBlank()
    {
        Assert.Equal("Unspecified provider", UsageAttributionPage.ProviderLabel(""));
        Assert.Equal("openai", UsageAttributionPage.ProviderLabel("openai"));
        Assert.Equal(
            "Classification for Unspecified provider",
            UsageAttributionPage.ClassificationForLabel(new UsageAttributionSettings.Row(
                "claude", "", 1, 1, UsageAttribution.State.Unassigned, null)));
    }

    // ---- i18n ----------------------------------------------------------
    //
    // Against the *shipped* strings-zh-Hant.json (the csproj copies it beside
    // the test assembly), not a fixture: the failure being guarded against is a
    // Localized() call site whose key was never added to that file, and against
    // a fixture written next to the test that failure is unreachable. The page
    // shows one body state at a time, so no screenshot can prove the other
    // three have entries — only driving each branch can.
    [Fact]
    public void EveryStringThePageCanShowHasATableEntry()
    {
        var row = new UsageAttributionSettings.Row(
            "claude", "openai", 1_234_567, 12.5,
            UsageAttribution.State.Unassigned, UsageAttribution.State.Assigned("codex"));
        Func<string>[] surfaces =
        [
            () => UsageAttributionPage.Copy.Section.Localized(),
            () => UsageAttributionPage.Copy.ClassifyHint.Localized(),
            () => UsageAttributionPage.Copy.CanonicalizationHint.Localized(),
            () => UsageAttributionPage.Copy.DeclarationHint.Localized(),
            () => UsageAttributionPage.Copy.NoRows.Localized(),
            () => UsageAttributionPage.Copy.Unavailable.Localized(),
            () => UsageAttributionPage.Copy.LoadingUsage.Localized(),
            () => UsageAttributionPage.Copy.SuggestionsHint.Localized(),
            () => UsageAttributionPage.Copy.Classification.Localized(),
            () => UsageAttributionPage.AcceptAllLabel(3),
            () => UsageAttributionPage.ObservedLine(row),
            () => UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Assigned("codex")),
            () => UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Excluded),
            () => UsageAttributionPage.SuggestionLabel(UsageAttribution.State.Unassigned),
            () => UsageAttributionPage.StateLabel(UsageAttribution.State.Assigned("codex")),
            () => UsageAttributionPage.StateLabel(UsageAttribution.State.Excluded),
            () => UsageAttributionPage.StateLabel(UsageAttribution.State.Unassigned),
            () => UsageAttributionPage.ProviderLabel(""),
            () => UsageAttributionPage.ClassificationForLabel(row),
            // All three, because these are the only strings on this page that
            // appear when something has gone wrong with the user's stored
            // classification — the moment English fallback is least affordable.
            () => UsageAttributionPage.FailureMessage(
                UsageAttributionSettings.WriteFailure.InvalidExistingValue),
            () => UsageAttributionPage.FailureMessage(
                UsageAttributionSettings.WriteFailure.EntryLimit),
            () => UsageAttributionPage.FailureMessage(
                UsageAttributionSettings.WriteFailure.SizeOrInvalidRecord),
            // NOT here, and for a reason rather than an oversight:
            //   SourceTitle — its format string is "{0} · {1}" both sides, so
            //     an identical result proves nothing. macOS needs the entry
            //     because %@ has to become positional %1$@; C# is positional
            //     already.
        ];

        var english = surfaces.Select(surface => surface()).ToList();
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.NotEqual(english[i], surfaces[i]());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }


    // An observed source whose usage the engine could not price reads "—"
    // beside its tokens, not "$0.00": this page asks the user which
    // subscription the usage belongs to, and a false "free" is the one answer
    // that would make classifying it look pointless.
    [Fact]
    public void AnUnpricedSourceReadsAsUnpricedNotFree()
    {
        var row = new UsageAttributionSettings.Row(
            "claude", "anthropic", 1_234_567, 0,
            UsageAttribution.State.Unassigned, UsageAttribution.State.Unassigned);

        var line = UsageAttributionPage.ObservedLine(row);

        Assert.EndsWith("· —", line, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.00", line, StringComparison.Ordinal);
    }
}
