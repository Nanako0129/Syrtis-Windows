using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Every decision the usage-attribution settings page makes, and the copy it
/// makes them with (port of <c>SettingsPanel.swift</c>'s
/// <c>usageAttributionPage</c> / <c>attributionRow</c> plus
/// <c>UsageAttributionSettings.Copy</c>). The page in
/// <see cref="SettingsWindow"/> calls this and lays out what comes back; it
/// decides nothing itself.
/// <para>
/// Pulled into TokenBar.Core.Tests via &lt;Compile Include&gt;, the same way
/// <see cref="QuotaLensText"/> and <see cref="QuotaSummaryText"/> are and for
/// the same reason: <c>SettingsWindow.cs</c> is compiled by no test project, so
/// a rule left inside it — the suppression rule below above all — would not be
/// asserted at all.
/// </para>
/// </summary>
public static class UsageAttributionPage
{
    /// <summary>The page's strings, transcribed from
    /// <c>UsageAttributionSettings.Copy</c>. Every one is a
    /// <c>.Localized()</c> key; <c>%@</c>/<c>%lld</c> become <c>{0}</c>, and
    /// the positional <c>%1$@ · %2$@</c> becomes <c>{0} · {1}</c>.</summary>
    public static class Copy
    {
        public const string Section = "Usage attribution";

        public const string ClassifyHint =
            "Classify each observed client/provider source against the subscription it "
            + "should count toward. Nothing here is inferred as a billing event.";

        public const string CanonicalizationHint =
            "Provider IDs are compared exactly as the source emitted them, so "
            + "related-looking routes may appear as separate rows and be classified "
            + "independently. Some clients merge them before reporting — Vertex AI "
            + "arriving as Anthropic, Codex as OpenAI — and a row that arrived merged "
            + "cannot be split here.";

        public const string DeclarationHint =
            "A declaration is your classification, not a billing fact.";

        public const string NoRows = "No provider-split usage in this range.";

        /// <summary>The report request finished without one. Distinct from
        /// <see cref="NoRows"/>, which is an answer about a report that did
        /// arrive.</summary>
        public const string Unavailable =
            "Usage could not be loaded, so there is nothing to classify yet.";

        public const string AcceptSuggestions = "Accept all suggestions ({0})";

        public const string SuggestionsHint =
            "Suggestions are proposals; they do not change your classification until accepted.";

        public const string Source = "{0} · {1}";

        public const string Observed = "Observed {0} tokens · {1}";

        public const string Classification = "Classification";

        public const string Unassigned = "Unassigned";

        public const string Excluded = "Not a subscription";

        public const string Assigned = "Counts toward {0}";

        public const string Suggested = "Suggested: counts toward {0}";

        public const string SuggestedExcluded = "Suggested: not a subscription";

        public const string UnspecifiedProvider = "Unspecified provider";

        public const string ClassificationFor = "Classification for {0}";

        /// <summary>The loading line. Not one of macOS's Copy constants — its
        /// <c>LoadingLine(title:)</c> takes the literal — and the Windows string
        /// table already carries this exact key for the dashboard footer, so the
        /// page reuses it rather than adding a second spelling.</summary>
        public const string LoadingUsage = "loading usage…";

        // NOT localized. UsageAttributionSettings.WriteFailure.message's three
        // strings are in macOS's Copy.all but not in the zh-Hant extract this
        // slice was given, and inventing a translation for a data-loss notice is
        // worse than falling back to English. Reported as an open item; adding
        // the three entries to strings-zh-Hant.json is all that is left.
        public const string InvalidExistingValue =
            "Could not save this classification: existing attribution data is invalid or foreign.";

        public const string EntryLimit =
            "Could not save this classification: the attribution entry limit was reached.";

        public const string SizeOrInvalidRecord =
            "Could not save this classification: the new value is too large or unsupported.";
    }

    /// <summary>What the page renders this pass.</summary>
    /// <param name="State">Which of the four bodies to show.</param>
    /// <param name="Rows">The classifiable sources, in observed order.</param>
    /// <param name="TargetClients">Subscriptions the picker can offer.</param>
    /// <param name="SuggestionCount">Rows carrying a visible suggestion — the
    /// number in the "Accept all suggestions (N)" button, and zero means the
    /// button is not shown at all.</param>
    public sealed record View(
        UsageAttributionSettings.PageState State,
        IReadOnlyList<UsageAttributionSettings.Row> Rows,
        IReadOnlyList<string> TargetClients,
        int SuggestionCount);

    /// <summary>
    /// Resolve one paint of the page.
    /// <para>
    /// <b>The suppression rule</b> (<c>SettingsPanel.swift:641-647</c>): a
    /// stored suggestion proposes a target, and until the quota payload says
    /// which subscriptions exist there is nothing to check that target against.
    /// A payload that has not arrived is not the same as a source having no
    /// suggestion, so the proposals are hidden from this view and the stored
    /// table is left exactly as it is — suppressing rather than deleting keeps a
    /// valid table intact across a transient failure, where the alternative
    /// destroys real proposals every time the request happens to be in flight.
    /// Nothing here writes; the suppressed state can therefore never be written
    /// back.
    /// </para>
    /// </summary>
    /// <param name="entries">The all-time report's rows, or null when no report
    /// arrived — the distinction <see cref="UsageAttributionSettings.PageState"/>
    /// needs.</param>
    /// <param name="agentUsage">The quota payload. Null is "targets not known
    /// yet", which is what suppresses suggestions.</param>
    public static View Resolve(
        IReadOnlyList<ModelReportEntry>? entries,
        AgentUsagePayload? agentUsage,
        UsageAttribution.Table confirmed,
        UsageAttribution.Table suggestions,
        bool isLoading)
    {
        IReadOnlyList<UsageAttribution.Record> visible =
            agentUsage is null ? [] : suggestions.Records;
        var rows = UsageAttributionSettings.Rows(entries ?? [], confirmed.Records, visible);
        return new View(
            UsageAttributionSettings.ResolvePageState(
                hasReport: entries is not null, rowCount: rows.Count, isLoading: isLoading),
            rows,
            UsageAttributionSettings.SubscriptionClients(agentUsage),
            rows.Count(row => row.SuggestedState is not null));
    }

    /// <summary>Regenerate the stored proposals for the current inputs
    /// (<c>refreshAttributionSuggestions</c>). Returns without touching the
    /// store while the quota payload is missing: the targets a proposal would be
    /// checked against are exactly what is unknown then, so reconciling would
    /// write the suppressed — that is, empty — state over real
    /// proposals.</summary>
    public static UsageAttributionSettings.WriteFailure? RefreshSuggestions(
        SettingsStore store,
        IReadOnlyList<ModelReportEntry> entries,
        AgentUsagePayload? agentUsage)
    {
        if (agentUsage is null)
        {
            return null;
        }

        var proposed = UsageAttributionSettings.SuggestionRecords(
            entries,
            UsageAttribution.Confirmed(store).Records,
            UsageAttributionSettings.SubscriptionClients(agentUsage),
            UsageAttributionSettings.RoutedSubscriptionsFrom(agentUsage));
        var stored = UsageAttribution.StoredValue.From(store, UsageAttribution.SuggestionsKey);
        var raw = UsageAttribution.SuggestionsRawReplacing(stored, proposed);
        if (raw is not null)
        {
            store.SetString(UsageAttribution.SuggestionsKey, raw);
            return null;
        }

        return UsageAttributionSettings.DiagnoseWriteFailure(
            UsageAttribution.ParseState(stored), proposed, null);
    }

    /// <summary>One row's picker choice.</summary>
    public static UsageAttributionSettings.WriteFailure? Save(
        SettingsStore store, UsageAttributionSettings.Row row, UsageAttribution.State state) =>
        UsageAttributionSettings.WriteRecords(
            store,
            UsageAttribution.ConfirmedKey,
            [new UsageAttribution.Record(row.Client, row.Provider, state)]);

    /// <summary>Confirm every visible proposal, then drop the proposals that
    /// were taken — an accepted suggestion has stopped being one.
    /// <para><paramref name="rows"/> is the view's row list, so a proposal the
    /// suppression rule hid cannot be accepted by a button the user could not
    /// have seen.</para></summary>
    public static UsageAttributionSettings.WriteFailure? AcceptAll(
        SettingsStore store, IReadOnlyList<UsageAttributionSettings.Row> rows)
    {
        var records = UsageAttributionSettings.AcceptanceRecords(rows);
        if (records.Count == 0)
        {
            return null;
        }

        if (UsageAttributionSettings.WriteRecords(store, UsageAttribution.ConfirmedKey, records)
            is { } failure)
        {
            return failure;
        }

        var removals = records
            .Select(record => record with { State = UsageAttribution.State.Unassigned })
            .ToList();
        return UsageAttributionSettings.WriteRecords(
            store, UsageAttribution.SuggestionsKey, removals);
    }

    /// <summary>Every target this row can legitimately hold has to be
    /// selectable, and <paramref name="targetClients"/> only lists clients with
    /// a quota snapshot. Two kinds fall outside it: one already confirmed, and
    /// one being suggested for a plan TokenBar draws no meter for. Offering the
    /// suggestion while the picker cannot select it leaves "Accept all" as the
    /// only way to take it, and then undoing everything else it
    /// accepted.</summary>
    public static IReadOnlyList<string> PickerTargets(
        UsageAttributionSettings.Row row, IReadOnlyList<string> targetClients)
    {
        var targets = targetClients.ToList();
        foreach (var state in new UsageAttribution.State?[] { row.State, row.SuggestedState })
        {
            if (state is { Kind: UsageAttribution.StateKind.Assigned, Target: { } target }
                && !targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>A suggestion can propose "not a subscription" as readily as a
    /// target, so the label follows the proposed state rather than assuming an
    /// assignment.</summary>
    public static string SuggestionLabel(UsageAttribution.State state) => state.Kind switch
    {
        UsageAttribution.StateKind.Assigned =>
            Copy.Suggested.Localized(ClientRegistry.Style(state.Target!).DisplayName),
        UsageAttribution.StateKind.Excluded => Copy.SuggestedExcluded.Localized(),
        _ => Copy.Unassigned.Localized(),
    };

    /// <summary>What the picker shows for a state.</summary>
    public static string StateLabel(UsageAttribution.State state) => state.Kind switch
    {
        UsageAttribution.StateKind.Assigned =>
            Copy.Assigned.Localized(ClientRegistry.Style(state.Target!).DisplayName),
        UsageAttribution.StateKind.Excluded => Copy.Excluded.Localized(),
        _ => Copy.Unassigned.Localized(),
    };

    public static string ProviderLabel(string provider) =>
        provider.Length == 0 ? Copy.UnspecifiedProvider.Localized() : provider;

    public static string SourceTitle(UsageAttributionSettings.Row row) =>
        Copy.Source.Localized(
            ClientRegistry.Style(row.Client).DisplayName, ProviderLabel(row.Provider));

    public static string ObservedLine(UsageAttributionSettings.Row row) =>
        Copy.Observed.Localized(Format.CompactTokens(row.Tokens), Format.Money(row.Tokens, row.Cost));

    public static string AcceptAllLabel(int count) => Copy.AcceptSuggestions.Localized(count);

    public static string ClassificationForLabel(UsageAttributionSettings.Row row) =>
        Copy.ClassificationFor.Localized(ProviderLabel(row.Provider));

    /// <summary>Why the write did not happen, in the notice's words.</summary>
    public static string FailureMessage(UsageAttributionSettings.WriteFailure failure) =>
        failure switch
        {
            UsageAttributionSettings.WriteFailure.InvalidExistingValue =>
                Copy.InvalidExistingValue.Localized(),
            UsageAttributionSettings.WriteFailure.EntryLimit => Copy.EntryLimit.Localized(),
            _ => Copy.SizeOrInvalidRecord.Localized(),
        };
}
