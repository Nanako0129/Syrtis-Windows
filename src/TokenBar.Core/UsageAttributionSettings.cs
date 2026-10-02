using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Pure derivation for the provider-level Usage Attribution settings page.
/// Port of TokenBarCore/UsageAttributionSettings.swift. The display copy stays
/// with the settings page that renders it; everything here is data.
/// </summary>
public static class UsageAttributionSettings
{
    public sealed record Row(
        string Client,
        string Provider,
        long Tokens,
        double Cost,
        UsageAttribution.State State,
        UsageAttribution.State? SuggestedState)
    {
        public string Id => SourceKey(Client, Provider);
    }

    public enum WriteFailure
    {
        InvalidExistingValue,
        EntryLimit,
        SizeOrInvalidRecord,
    }

    /// <summary>What the attribution page shows. A nil report is two states: the
    /// request is still running, or it finished without one. Collapsing the
    /// second into "no provider-split usage" tells the user there is nothing to
    /// classify at exactly the moment the data needed to classify could not be
    /// loaded.</summary>
    public enum PageState
    {
        Loading,
        Unavailable,
        Empty,
        Rows,
    }

    /// <summary>This is auditable product knowledge, not an inference from local
    /// usage or quota payloads. Providers whose subscription terms allow them to
    /// be reached through some other agent; only these can carry a cross-client
    /// assignment suggestion.
    ///
    /// An allowlist rather than a denylist, because being wrong in the permissive
    /// direction proposes that the user did something their provider forbids.
    /// Extend it per provider, with the terms checked.
    ///
    /// xAI ships OAuth sign-in for SuperGrok / X Premium+ into third-party agents
    /// (Pi, OpenCode) and its own ACP protocol for them, and that usage draws on
    /// the same shared weekly pool as grok.com — so a row of theirs logged
    /// elsewhere really did consume the subscription.</summary>
    public static readonly IReadOnlySet<string> CrossAgentSubscriptionProviders =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "openai", "xai",
            // Vendors whose coding plans are sold to be reached from other agents —
            // that is the product. Reselling them is likewise routine.
            "deepseek", "moonshot", "minimax", "zhipu", "alibaba",
            // Not a vendor relationship at all. `open-weights` is a model the
            // product hosts itself and `own` is a model it trained; either way the
            // subscription that served it is the one that paid, and no third
            // party's terms are involved.
            "open-weights", "own",
            // Sold only inside a bundle (Copilot's MAI, Junie's Amazon models), so
            // a row of theirs is that bundle's spend by construction.
            "microsoft", "amazon",
        };

    /// <summary>Providers whose subscription may only be used by its own client.
    /// A row of theirs logged by a different client is API spend under any
    /// compliant reading, so that is what gets suggested — never their
    /// subscription.</summary>
    public static readonly IReadOnlySet<string> SubscriptionBoundProviders =
        new HashSet<string>(StringComparer.Ordinal) { "anthropic", "google" };

    /// <summary>The registered client that <em>is</em> a vendor's own product,
    /// where one exists.
    ///
    /// Two callers, two reasons. The bound-provider rule uses it to name the
    /// subscription whose terms forbid being driven from elsewhere. Opencode
    /// label resolution uses it because an <c>auth.json</c> oauth entry for a
    /// vendor means the user authed to that vendor directly — so the subscription
    /// is the vendor's own, never a reseller that also happens to carry it.
    ///
    /// A vendor with no first-party client here (<c>microsoft</c>, <c>amazon</c>,
    /// <c>open-weights</c>, <c>own</c>) is sold only inside someone else's
    /// bundle.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderOwnClient =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic"] = "claude",
            ["openai"] = "codex",
            ["google"] = "antigravity",
            ["xai"] = "grok",
            ["moonshot"] = "kimi",
            ["minimax"] = "micode",
            ["alibaba"] = "qwen",
        };

    /// <summary>Which model vendors each subscription's own plan pays for.
    ///
    /// <b>This is a perishable external fact, not something derivable from the
    /// code.</b> Vendors are added and dropped from these plans continuously —
    /// when a suggestion looks obviously wrong, suspect this table before
    /// suspecting the logic that reads it. Last verified 2026-08-07 (transcribed
    /// from TokenBarCore/UsageAttributionSettings.swift:168-201).
    ///
    /// Two rules decide what belongs here, and both were got wrong before:
    /// <list type="bullet">
    /// <item><b>BYO-key does not count.</b> A product that merely supports a
    /// vendor when you supply your own API key did not pay for those tokens; you
    /// paid the vendor. Cursor's BYOK path and Warp's own-key mode are excluded
    /// on this basis.</item>
    /// <item><b>A vendor's open weights hosted by someone else are not that
    /// vendor's subscription.</b> Antigravity serves <c>gpt-oss-120b</c>, but it
    /// is Apache-2.0 weights running on Google's capacity — the money goes to
    /// Google. It is <c>open-weights</c>, never <c>openai</c>.</item>
    /// </list></summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SubscriptionProviderMap =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // Single-vendor plans: the vendor's own product.
            ["claude"] = Providers("anthropic"),
            ["codex"] = Providers("openai"),
            ["grok"] = Providers("xai"),
            ["kimi"] = Providers("moonshot"),
            ["micode"] = Providers("minimax"),

            // Multi-vendor plans. A row of theirs is the subscription's spend,
            // not the underlying vendor's.
            ["copilot"] = Providers("openai", "anthropic", "google", "xai", "microsoft", "moonshot"),
            ["antigravity"] = Providers("google", "anthropic", "open-weights"),
            ["cursor"] = Providers("anthropic", "openai", "google", "xai", "own", "moonshot", "zhipu"),
            ["zed"] = Providers("anthropic", "openai", "google"),
            ["junie"] = Providers("openai", "anthropic", "google", "xai", "amazon"),
            ["trae"] = Providers("openai", "anthropic", "minimax"),
            ["amp"] = Providers("openai", "anthropic", "zhipu", "open-weights"),
            ["droid"] = Providers("anthropic", "openai", "google", "moonshot", "zhipu", "open-weights"),
            ["kiro"] = Providers("anthropic", "open-weights", "alibaba", "deepseek", "minimax"),
            ["warp"] = Providers(
                "openai", "anthropic", "google", "xai", "open-weights",
                "zhipu", "moonshot", "minimax", "alibaba", "deepseek"),
            ["kilo"] = Providers(
                "anthropic", "openai", "google", "xai", "deepseek",
                "moonshot", "minimax", "zhipu", "alibaba", "open-weights"),
            ["kilocode"] = Providers(
                "anthropic", "openai", "google", "xai", "deepseek",
                "moonshot", "minimax", "zhipu", "alibaba", "open-weights"),
            ["cline"] = Providers("zhipu", "moonshot", "deepseek", "minimax", "alibaba", "open-weights"),
            ["qwen"] = Providers("alibaba", "zhipu", "moonshot", "minimax"),

            // Deliberately absent: the clients added by the 2026-10-02 engine
            // sync (zcode, augment, hindsight, muse, reasonix, kimchi, senpi,
            // omp). Which vendors their plans pay for, if they are plans at
            // all, has not been verified, and an unverified row here would
            // misattribute spend; they attribute like any unmapped client
            // until it is.
        };

    /// <summary>Clients that route through subscriptions they do not own, keyed
    /// to the subscription clients they are authed against.</summary>
    public sealed record RoutedSubscriptions(IReadOnlyDictionary<string, IReadOnlyList<string>> Routes)
    {
        public static RoutedSubscriptions None { get; } =
            new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        public IReadOnlyList<string>? For(string client) =>
            Routes.TryGetValue(client, out var routed) ? routed : null;
    }

    /// <summary><c>agentUsage</c> is the capability source for assignment targets.
    /// A transient last-good snapshot keeps its display-ready identity, windows,
    /// or credits, while the Rust producer's required placeholder for an absent
    /// provider has none of them. Exclude only that terminal empty shape so it
    /// cannot invent a subscription the user never configured.
    ///
    /// <c>opencodeSubscriptions</c> is a second, independent statement of
    /// ownership: opencode reports the providers its <c>auth.json</c> holds an
    /// oauth entry for, and a user who reaches a subscription only that way has
    /// no snapshot of their own — just the empty placeholder the filter above
    /// removes. Dropping them would leave exactly those rows unable to name the
    /// subscription they are known to consume, so the labels are folded back in
    /// as targets.</summary>
    public static IReadOnlyList<string> SubscriptionClients(AgentUsagePayload? payload)
    {
        var configured = (payload?.Agents ?? [])
            .Where(snapshot =>
                snapshot.Identity is not null || snapshot.Windows.Count > 0 || snapshot.Credits is not null)
            .Select(snapshot => snapshot.ClientId);
        var viaOpencode = (payload?.OpencodeSubscriptions ?? [])
            .Select(SubscriptionClientForLabel)
            .Where(id => id is not null)
            .Select(id => id!);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return configured.Concat(viaOpencode)
            .Where(id => ClientRegistry.AllIds.Contains(id) && seen.Add(id))
            .ToList();
    }

    /// <summary>The subscription client an opencode label names, or null when
    /// none is.
    ///
    /// Two rules, because the producer's labels are not uniformly invertible.
    /// <c>subscription_label</c> renames four providers outright (<c>openai</c> to
    /// <c>Codex</c> and so on) and otherwise just capitalizes the provider key, so
    /// the renames need an explicit inverse while everything else <em>is</em> a
    /// provider and can be resolved through the map that already says which client
    /// serves it. Writing the second rule as a lookup rather than a second
    /// hand-kept table is what stops a provider added to
    /// <see cref="SubscriptionProviderMap"/> from being invisible here.</summary>
    public static string? SubscriptionClientForLabel(string label)
    {
        if (ClientRegistry.SubscriptionLabelAliases.TryGetValue(label, out var alias))
        {
            return alias;
        }

        // opencode's provider keys carry the plan, not just the vendor:
        // `minimax-coding-plan` becomes the label `Minimax-coding-plan`, and
        // `kimi-for-coding` and the `zai` coding plan are the same shape. The
        // vendor is the leading segment, so the qualifier is trimmed before the
        // lookup rather than each plan being enumerated — these vendors sell
        // per-plan keys and the list would never stay complete.
        var vendor = label.ToLowerInvariant().Split('-', 2)[0];

        // The remaining labels are capitalized provider keys, and an oauth entry
        // for a provider means the user authed to that provider directly — so the
        // subscription is that vendor's own client, not whichever reseller also
        // carries it. A label naming no first-party client names no subscription:
        // `Kiro` lowercases to a registered client, so returning it would put
        // "Counts toward Kiro" in the picker for something the policy can never
        // resolve.
        if (ProviderOwnClient.TryGetValue(vendor, out var own))
        {
            return own;
        }

        // The key can name the product rather than the model vendor: Moonshot
        // sells `kimi-for-coding`, and `kimi` is the registered client while
        // `moonshot` is the vendor ProviderOwnClient is keyed by. Accept the
        // segment as a client only when that client actually sells a plan —
        // otherwise a registered id that owns no subscription (`crush`) would
        // become a target the policy can never resolve.
        return SubscriptionProviderMap.ContainsKey(vendor) ? vendor : null;
    }

    /// <summary>opencode is the only client TokenBar can know this for, because
    /// its <c>auth.json</c> oauth entries are reported as
    /// <c>opencodeSubscriptions</c>. That declaration is what separates it from
    /// every other multi-provider source: a Cursor row is Cursor's own plan, but
    /// an opencode row was paid for by whichever subscription opencode is signed
    /// into.</summary>
    public static RoutedSubscriptions RoutedSubscriptionsFrom(AgentUsagePayload? payload)
    {
        var authed = (payload?.OpencodeSubscriptions ?? [])
            .Select(SubscriptionClientForLabel)
            .Where(id => id is not null && ClientRegistry.AllIds.Contains(id))
            .Select(id => id!)
            .ToList();
        return authed.Count == 0
            ? RoutedSubscriptions.None
            : new RoutedSubscriptions(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["opencode"] = authed,
            });
    }

    public static PageState ResolvePageState(bool hasReport, int rowCount, bool isLoading)
    {
        if (!hasReport)
        {
            return isLoading ? PageState.Loading : PageState.Unavailable;
        }

        return rowCount == 0 ? PageState.Empty : PageState.Rows;
    }

    /// <summary>Consume raw provider-split rows. Folding providers back together
    /// would erase the exact dimension this page classifies.</summary>
    public static IReadOnlyList<Row> Rows(
        IReadOnlyList<ModelReportEntry> entries,
        IReadOnlyList<UsageAttribution.Record> confirmed,
        IReadOnlyList<UsageAttribution.Record> suggestions)
    {
        var aggregate = new Dictionary<string, (string Client, string Provider, long Tokens, double Cost)>(
            StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in entries)
        {
            // Per entry and before folding, which is where the breakdown card
            // applies the same test. Applying it to the aggregate instead would
            // agree on every ordinary input and part on one that cancels: two rows
            // of +5 and -5 sum to an empty source here while the card, having
            // resolved each row first, can still place them in different buckets
            // and report both.
            if (entry.Total == 0 && entry.Cost == 0)
            {
                continue;
            }

            var key = SourceKey(entry.Client, entry.Provider);
            if (aggregate.TryGetValue(key, out var current))
            {
                aggregate[key] = (
                    current.Client,
                    current.Provider,
                    current.Tokens.SaturatingAdd(entry.Total),
                    current.Cost + entry.Cost);
            }
            else
            {
                aggregate[key] = (entry.Client, entry.Provider, entry.Total, entry.Cost);
                order.Add(key);
            }
        }

        var rows = new List<Row>(order.Count);
        foreach (var key in order)
        {
            var value = aggregate[key];
            var state = UsageAttribution.Resolve(value.Client, value.Provider, null, confirmed);
            // A stored suggestion carries its own state: `excluded` is a proposal
            // in its own right, not the absence of one. And it is only ever
            // reported alongside the row, never as the row's state — a suggestion
            // is not a classification.
            UsageAttribution.State? suggestion = null;
            if (state.Kind == UsageAttribution.StateKind.Unassigned)
            {
                foreach (var record in suggestions)
                {
                    if (record.Client == value.Client && record.Provider == value.Provider
                        && record.Model is null)
                    {
                        suggestion = record.State;
                        break;
                    }
                }
            }

            rows.Add(new Row(value.Client, value.Provider, value.Tokens, value.Cost, state, suggestion));
        }

        return rows;
    }

    /// <summary>Which subscription a source row could plausibly belong to, or
    /// null when nothing can be said.
    ///
    /// The question is asked provider-first, not source-first: a row records which
    /// provider served the tokens, and the answer is which of the user's
    /// subscriptions covers that provider — usually <em>not</em> the client that
    /// happened to log it. Routing a Claude Code session through a gateway to an
    /// OpenAI model produces <c>("claude", "openai")</c>, and the subscription it
    /// consumed is Codex.
    ///
    /// Still only a suggestion. The same row on another machine is an OpenAI API
    /// key, whose right answer is excluded, and no field in the data tells the two
    /// apart. This turns N clicks into one confirmation; it never decides.</summary>
    public static UsageAttribution.State? SuggestionTarget(
        string sourceClient,
        string provider,
        IReadOnlyList<string> subscriptionClients,
        RoutedSubscriptions? routedSubscriptions = null)
    {
        var routes = routedSubscriptions ?? RoutedSubscriptions.None;
        var owners = subscriptionClients.Where(Covers).ToList();

        // A client talking to its own provider is the plainest reading, and stays
        // unambiguous even when another subscription also accepts it. "Its own" is
        // asked of the subscription the source draws on, not of its process
        // identity: `antigravity-cli` is a client in its own right but spends the
        // `antigravity` subscription, so comparing the raw id would find no owner
        // and fall through to the subscription-bound branch — declaring the CLI's
        // own subscription usage to be API spend.
        var sourceOwner = ClientRegistry.QuotaOwner(sourceClient);

        // Own subscription wins, and asking the table directly rather than
        // `owners` is the point: `owners` is filtered by `subscriptionClients`,
        // which lists only clients TokenBar has a quota snapshot for. Attribution
        // answers who paid, not who has a gauge.
        if (Covers(sourceOwner))
        {
            return UsageAttribution.State.Assigned(sourceOwner);
        }

        // A declared router spends the subscription it is signed into, so that is
        // the answer before any policy about the provider applies — the question
        // of who may reach a vendor from elsewhere does not arise when the user
        // authed to it here on purpose.
        if (routes.For(sourceOwner) is { } routed)
        {
            var covering = routed.Where(Covers).ToList();
            return covering.Count == 1 ? UsageAttribution.State.Assigned(covering[0]) : null;
        }

        // A source the table says nothing about is not evidence of anything.
        // Absence here means "nobody has established what this product's plan
        // covers", which is a different claim from "this product's plan does not
        // cover it" — and only the second one justifies calling the usage API
        // spend. Refusing to suggest leaves the row unassigned for the user.
        if (!SubscriptionProviderMap.ContainsKey(sourceOwner))
        {
            return null;
        }

        // Which of the covering subscriptions could legitimately have been reached
        // from somewhere else. For a bound provider that is every owner except the
        // provider's own client, whose terms forbid exactly that route; a reseller
        // like Copilot is unaffected. For a permitted provider every owner
        // qualifies.
        var bound = SubscriptionBoundProviders.Contains(provider);
        List<string> eligible;
        if (bound)
        {
            var ownClient = ProviderOwnClient.GetValueOrDefault(provider);
            eligible = owners.Where(id => id != ownClient).ToList();
        }
        else if (CrossAgentSubscriptionProviders.Contains(provider))
        {
            eligible = owners;
        }
        else
        {
            // Neither policy covers this provider. The self-test pins that this
            // cannot happen for a provider a subscription serves; it remains the
            // runtime backstop if that stops holding.
            eligible = [];
        }

        if (eligible.Count != 1)
        {
            // Nothing eligible and the provider is bound: assume the user is
            // complying, so the tokens were bought rather than drawn from the
            // subscription. Nothing eligible otherwise says nothing at all.
            return eligible.Count == 0 && bound ? UsageAttribution.State.Excluded : null;
        }

        return UsageAttribution.State.Assigned(eligible[0]);

        bool Covers(string client) =>
            SubscriptionProviderMap.TryGetValue(client, out var providers) && providers.Contains(provider);
    }

    public static IReadOnlyList<UsageAttribution.Record> SuggestionRecords(
        IReadOnlyList<ModelReportEntry> entries,
        IReadOnlyList<UsageAttribution.Record> confirmed,
        IReadOnlyList<string> subscriptionClients,
        RoutedSubscriptions? routedSubscriptions = null)
    {
        var records = new List<UsageAttribution.Record>();
        foreach (var row in Rows(entries, confirmed, []))
        {
            if (row.State.Kind != UsageAttribution.StateKind.Unassigned)
            {
                continue;
            }

            if (SuggestionTarget(row.Client, row.Provider, subscriptionClients, routedSubscriptions)
                is not { } proposed)
            {
                continue;
            }

            records.Add(new UsageAttribution.Record(row.Client, row.Provider, proposed));
        }

        return records;
    }

    /// <summary>Only provider-level proposals that are still unassigned. The
    /// caller stores them with <c>SuggestionsRaw</c>; only explicit acceptance may
    /// pass the same records to <c>ConfirmedRaw</c>.</summary>
    public static IReadOnlyList<UsageAttribution.Record> AcceptanceRecords(IReadOnlyList<Row> rows)
    {
        var records = new List<UsageAttribution.Record>();
        foreach (var row in rows)
        {
            if (row.State.Kind != UsageAttribution.StateKind.Unassigned
                || row.SuggestedState is not { } proposed)
            {
                continue;
            }

            records.Add(new UsageAttribution.Record(row.Client, row.Provider, proposed));
        }

        return records;
    }

    /// <summary>Why a write did not happen. <paramref name="result"/> is the raw
    /// value the codec produced; a non-null one means the write succeeded and
    /// there is no failure to report.</summary>
    public static WriteFailure? DiagnoseWriteFailure(
        UsageAttribution.Table table, UsageAttribution.Record record, string? result) =>
        DiagnoseWriteFailure(table, [record], result);

    public static WriteFailure? DiagnoseWriteFailure(
        UsageAttribution.Table table, IReadOnlyList<UsageAttribution.Record> updates, string? result)
    {
        if (result is not null)
        {
            return null;
        }

        if (!table.IsWritable)
        {
            return WriteFailure.InvalidExistingValue;
        }

        var records = table.Records.ToList();
        foreach (var update in updates)
        {
            records.RemoveAll(existing =>
                existing.Client == update.Client && existing.Provider == update.Provider
                && existing.Model == update.Model);
            if (update.State.Kind == UsageAttribution.StateKind.Unassigned)
            {
                continue;
            }

            records.Add(update);
        }

        return records.Count > UsageAttribution.MaxEntries
            ? WriteFailure.EntryLimit
            : WriteFailure.SizeOrInvalidRecord;
    }

    /// <summary>The one place a classification write happens. Windows'
    /// <see cref="SettingsStore.SetString"/> takes a non-nullable value, so
    /// "refuse to write" cannot live inside the call: the raw value is computed
    /// first and the store is touched only when the codec produced one. A foreign
    /// or malformed stored value therefore stays byte-for-byte as it was.</summary>
    public static WriteFailure? WriteRecords(
        SettingsStore store, string key, IReadOnlyList<UsageAttribution.Record> updates)
    {
        var stored = UsageAttribution.StoredValue.From(store, key);
        var raw = key == UsageAttribution.SuggestionsKey
            ? UsageAttribution.SuggestionsRaw(stored, updates)
            : UsageAttribution.ConfirmedRaw(stored, updates);
        if (raw is not null)
        {
            store.SetString(key, raw);
            return null;
        }

        return DiagnoseWriteFailure(UsageAttribution.ParseState(stored), updates, null);
    }

    /// <summary><paramref name="targetsKnown"/> distinguishes "no subscriptions"
    /// from "not asked yet". Both render <paramref name="subscriptionClients"/> as
    /// an empty list, so without it a payload that arrives with zero candidates
    /// produces an unchanged signature and the reconciliation that should clear
    /// stale proposals never runs.</summary>
    public static string Signature(
        IReadOnlyList<ModelReportEntry> entries,
        IReadOnlyList<string> subscriptionClients,
        bool targetsKnown = true,
        RoutedSubscriptions? routedSubscriptions = null)
    {
        var routes = (routedSubscriptions ?? RoutedSubscriptions.None).Routes;
        var entrySignature = string.Join('\u001e', entries.Select(entry => string.Join(
            '\u001f',
            entry.Client,
            entry.Provider,
            entry.Model,
            entry.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BitConverter.DoubleToUInt64Bits(entry.Cost).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        // Routing is a second, independent input to every suggestion: opencode
        // gaining or losing an oauth entry changes the answer while the resolved
        // target list can stay identical, because a Codex snapshot already
        // contributes `codex` either way.
        var routing = string.Join(';', routes.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => $"{key}>{string.Join('+', routes[key].OrderBy(v => v, StringComparer.Ordinal))}"));
        return entrySignature + "|" + (targetsKnown ? "known" : "pending")
            + "|" + string.Join(',', subscriptionClients)
            + "|" + routing;
    }

    private static IReadOnlySet<string> Providers(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    private static string SourceKey(string client, string provider) => client + '\0' + provider;
}
