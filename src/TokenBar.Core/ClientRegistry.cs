namespace TokenBar.Core;

// Client (agent) display registry, ported from TokenBarCore/ClientRegistry.swift
// (originally the Tauri app's src/lib/clients.ts). Carries the display name +
// brand disc color used by chart legends and model rows; icons come later.

public sealed record ClientStyle(string Id, string DisplayName, string Color);

public sealed record ClientSelection(
    IReadOnlyList<string> DisplayClients,
    IReadOnlyList<string> SelectedClients,
    string ActiveTab);

public static class ClientRegistry
{
    private static readonly Dictionary<string, (string DisplayName, string Color)> Entries = new()
    {
        ["claude"] = ("Claude Code", "#d97706"),
        ["openclaw"] = ("OpenClaw", "#dc2626"),
        ["gemini"] = ("Gemini CLI", "#60a5fa"),
        ["opencode"] = ("OpenCode", "#1f2937"),
        // No form-factor suffix on these three: their sources are not
        // surface-scoped. `codex` reads ~/.codex/sessions, written by Codex
        // Desktop / the IDE extension / the CLI alike (a 1733-file sample was
        // 70% "Codex Desktop", 4% CLI). `copilot` merges the CLI/VS Code OTel
        // export with the desktop app's ~/.copilot/data.db. `cursor` is not a
        // session parser at all — it reads Cursor's account usage export CSV,
        // which bills IDE, cursor-agent and cloud agents into one
        // undifferentiated ledger. Ported from macOS ClientRegistry.swift,
        // which dropped these suffixes in v1.13.3 for the same reason.
        ["codex"] = ("Codex", "#9ca3af"),
        ["copilot"] = ("Copilot", "#1f2937"),
        ["cursor"] = ("Cursor", "#0ea5e9"),
        ["amp"] = ("Amp", "#10b981"),
        ["droid"] = ("Droid", "#22c55e"),
        ["hermes"] = ("Hermes", "#a78bfa"),
        ["pi"] = ("Pi", "#f472b6"),
        ["kimi"] = ("Kimi", "#fbbf24"),
        // Both take the same neutral grey the unregistered fallback uses, so
        // "junie" already rendered correctly by accident. Registering them is
        // still not a no-op: AllIds is the canonical universe demo fixtures
        // draw from, and RegisteredNames guards ShortName from collapsing one
        // client's name onto another's.
        ["junie"] = ("Junie", "#6b7280"),
        ["opencodereview"] = ("OpenCodeReview", "#6b7280"),
        ["qwen"] = ("Qwen CLI", "#7c3aed"),
        ["roocode"] = ("Roo Code", "#ef4444"),
        ["kilocode"] = ("KiloCode", "#f97316"),
        ["kilo"] = ("Kilo CLI", "#f59e0b"),
        ["mux"] = ("Mux", "#06b6d4"),
        ["crush"] = ("Crush", "#ec4899"),
        ["synthetic"] = ("Synthetic", "#64748b"),
        ["goose"] = ("Goose", "#14b8a6"),
        ["codebuff"] = ("Codebuff", "#8b5cf6"),
        ["antigravity"] = ("Antigravity", "#3b82f6"),
        ["zed"] = ("Zed", "#084fff"),
        ["kiro"] = ("Kiro", "#9046ff"),
        ["trae"] = ("Trae", "#ef4444"),
        ["warp"] = ("Warp", "#01a4ff"),
        ["cline"] = ("Cline", "#5b8def"),
        ["antigravity-cli"] = ("Antigravity CLI", "#6366f1"),
        ["jcode"] = ("Jcode", "#84cc16"),
        ["micode"] = ("MiMo Code", "#fb923c"),
        ["gjc"] = ("gjc", "#e11d48"),
        ["grok"] = ("Grok Build", "#1f2937"),
    };

    /// <summary>Every registered client id, sorted. Demo fixtures use this
    /// canonical universe so every usage surface renders the same client
    /// set.</summary>
    public static IReadOnlyList<string> AllIds =>
        [.. Entries.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    private static readonly HashSet<string> RegisteredNames =
        [.. Entries.Values.Select(e => e.DisplayName)];

    public static ClientStyle Style(string id)
    {
        if (Entries.TryGetValue(id, out var entry))
        {
            return new ClientStyle(id, entry.DisplayName, entry.Color);
        }

        // Fallback: title-case the id, neutral grey disc.
        var displayName = id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];
        return new ClientStyle(id, displayName, "#6b7280");
    }

    /// <summary>Display name with the trailing form-factor word dropped, as
    /// the chart legend does ("Claude Code" → "Claude").</summary>
    public static string ShortName(string id)
    {
        var name = Style(id).DisplayName;
        foreach (var suffix in new[] { " CLI", " Code", " IDE" })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var baseName = name[..^suffix.Length];
            // Don't collapse onto a base that is itself another client's full
            // name — e.g. "Antigravity CLI" must stay distinct from the IDE
            // client "Antigravity".
            if (!RegisteredNames.Contains(baseName))
            {
                return baseName;
            }
        }

        return name;
    }

    // MARK: - Tab bar display order & visibility (new for tabs improvement)

    public const string TabOrderKey = "tokenbar.tabs.order";
    public const string TabHiddenKey = "tokenbar.tabs.hidden";
    public const string ActiveTabKey = "tokenbar.activeTab";
    public const string OverviewTab = "overview";

    /// <summary>Independent from <see cref="TabHiddenKey"/>: hides a client's
    /// Agent-limits quota card only, leaving its top tab (and cost/token/model
    /// data) visible. Added for accounts whose plan has no OAuth quota (e.g.
    /// Claude Console).</summary>
    public const string LimitsHiddenKey = "tokenbar.limits.hidden";

    /// <summary>Canonicalize a live-tail client id to the registry's short id.
    /// The usage trace reports raw ids (claude-code, codex-cli, gemini-cli)
    /// while the hidden set, quota snapshots, and registry all key on short ids
    /// (claude, codex, gemini). EXPLICIT aliases only — no generic -cli suffix
    /// rule: antigravity-cli is a registered client id distinct from the
    /// antigravity IDE, so stripping -cli would conflate the two (hiding one
    /// would mis-target the other).</summary>
    public static string CanonicalClient(string id) => id switch
    {
        "claude-code" => "claude",
        "codex-cli" => "codex",
        "gemini-cli" => "gemini",
        _ => id,
    };

    /// <summary>The client whose quota snapshot a client's usage is served by,
    /// where the two identities differ. <c>antigravity-cli</c> is a registered
    /// client in its own right — process identity, tab, icon and preferences all
    /// stay distinct — but it draws on the <c>antigravity</c> subscription and the
    /// quota views have always folded it that way. Anything reasoning about which
    /// subscription a client's tokens consume has to fold it too, or it will
    /// conclude the CLI owns no subscription at all.</summary>
    public static string QuotaOwner(string id) => id == "antigravity-cli" ? "antigravity" : id;

    /// <summary>The registered client behind an opencode subscription label.
    /// opencode reports which providers it is authed against as display labels
    /// rather than ids (<c>agent_usage.rs</c> builds them in
    /// <c>subscription_label</c>), so a consumer that needs the id must map them
    /// back here. These are the four labels <c>subscription_label</c> renames
    /// outright; everything else it emits is a capitalized provider key, which
    /// cannot be resolved from this table alone. Kept as data rather than a
    /// switch so a caller can tell a rename from a passthrough — three of the
    /// four lowercase to their own id, so comparing the result against
    /// <c>label.ToLowerInvariant()</c> cannot make that distinction.</summary>
    public static readonly IReadOnlyDictionary<string, string> SubscriptionLabelAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Codex"] = "codex",
            ["Claude"] = "claude",
            ["Copilot"] = "copilot",
            ["Gemini"] = "antigravity",
        };

    public static string ClientIdForSubscriptionLabel(string label) =>
        SubscriptionLabelAliases.TryGetValue(label, out var alias) ? alias : label.ToLowerInvariant();

    // MARK: - Grouped tabs

    /// <summary>Tabs that group more than one client id under a single top
    /// tab: one member carries local session usage (antigravity-cli), the
    /// other only a cloud quota with no usage of its own (antigravity, the
    /// IDE client). Ported from macOS ClientRegistry.swift's <c>tabGroups</c>
    /// (:194-207); Windows has no grok-bot provider, so only the Antigravity
    /// group exists here.</summary>
    private static readonly Dictionary<string, (string[] Members, string Label)> TabGroups = new()
    {
        ["antigravity"] = (["antigravity", "antigravity-cli"], "Antigravity"),
    };

    /// <summary>Reverse lookup built once: a group member's id -> the tab id
    /// it folds into. A group's own tab id is absent here — callers fall back
    /// to the id itself, which is exactly a no-op fold.</summary>
    private static readonly IReadOnlyDictionary<string, string> MemberToTabId =
        TabGroups
            .SelectMany(entry => entry.Value.Members.Select(member => (member, tab: entry.Key)))
            .ToDictionary(pair => pair.member, pair => pair.tab, StringComparer.Ordinal);

    private static string FoldToTabId(string id) =>
        MemberToTabId.TryGetValue(id, out var tab) ? tab : id;

    /// <summary>Client ids behind a top tab. "antigravity": the CLI carries
    /// the usage, the IDE client carries the quota — shown as two sections
    /// under one tab rather than two tabs.</summary>
    public static IReadOnlyList<string> TabSlice(string id) =>
        TabGroups.TryGetValue(id, out var group) ? group.Members : [id];

    /// <summary>Navigation includes configured quota sources even without
    /// local usage. Group members share one tab but retain their provider
    /// identities below it. Ported from macOS <c>tabClients(present:quotaIds:)</c>
    /// (ClientRegistry.swift :219-224).</summary>
    public static IReadOnlyList<string> TabClients(
        IReadOnlyList<string> present, IReadOnlyList<string> quotaIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in present.Concat(quotaIds))
        {
            var tabId = FoldToTabId(id);
            if (seen.Add(tabId))
            {
                result.Add(tabId);
            }
        }
        return result;
    }

    /// <summary>Tab-bar and single-client-title label. Only a grouped tab
    /// differs from its short name; every other tab keeps
    /// <see cref="ShortName"/>.</summary>
    public static string TabLabel(string id) =>
        TabGroups.TryGetValue(id, out var group) ? group.Label : ShortName(id);

    /// <summary>Card titles retain the full client name for ordinary
    /// tabs.</summary>
    public static string TabDisplayName(string id) =>
        TabSlice(id).Count > 1 ? TabLabel(id) : Style(id).DisplayName;

    /// <summary>Expand a set so group members follow their tab: naming the
    /// "antigravity" tab also carries the quota-only "antigravity-cli" row
    /// along (which has no tab of its own). Explicit member entries pass
    /// through unchanged, so an independent limits-toggle on a member row
    /// keeps working.</summary>
    public static IReadOnlySet<string> WithGroupMembers(IReadOnlySet<string> ids)
    {
        var result = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (TabGroups.TryGetValue(id, out var group))
            {
                result.UnionWith(group.Members);
            }
        }
        return result;
    }

    /// <summary>The one reading of <see cref="TabHiddenKey"/> every
    /// tab-visibility consumer takes, closed over both directions of the
    /// grouping: a raw comparison against TAB ids misses a legacy
    /// `antigravity-cli` entry from when the CLI had its own tab, and a raw
    /// comparison against CLIENT ids misses that a fresh hide of
    /// "antigravity" should also exclude "antigravity-cli"'s usage. Folding
    /// members to their group and then expanding back to all members
    /// satisfies both. Tab visibility only — <see cref="HiddenLimitsClients"/>
    /// stays member-specific in both directions, so hiding one member's quota
    /// card never hides the other's. Ported from macOS
    /// <c>hiddenTabClients</c> (ClientRegistry.swift :383-408).</summary>
    public static IReadOnlySet<string> HiddenTabClients(IReadOnlySet<string> raw) =>
        WithGroupMembers(new HashSet<string>(raw.Select(FoldToTabId), StringComparer.Ordinal));

    public static IReadOnlySet<string> HiddenTabClients(SettingsStore store) =>
        HiddenTabClients(HiddenClients(store));

    /// <summary>Folds a saved order id list to tab ids, deduplicated (first
    /// occurrence wins). The ordering counterpart of
    /// <see cref="HiddenTabClients(IReadOnlySet{string})"/>, and for the same
    /// reason: `tokenbar.tabs.order` can hold `antigravity-cli` from when the
    /// CLI had a tab of its own. Deliberately NOT applied inside the raw
    /// <see cref="OrderedClients(IReadOnlyList{string}, string)"/> overload —
    /// several callers (the limits card's rows, the Settings client-tabs
    /// list, the tray) order MEMBER ids, where both Antigravity members must
    /// keep distinct positions; folding there would collapse them onto one
    /// index. Ported from macOS <c>tabOrder(_:)</c> (ClientRegistry.swift
    /// :315-330).</summary>
    public static IReadOnlyList<string> TabOrder(string raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ParseIdList(raw).Select(FoldToTabId))
        {
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    /// <summary>Parses the comma-separated id form persisted by the tab
    /// order/hidden defaults into a set, tolerating an empty string. Single
    /// source of the CSV split so callers all agree on the shape.</summary>
    public static IReadOnlySet<string> ParseIdSet(string raw) =>
        new HashSet<string>(raw.Split(',', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Ordered variant of <see cref="ParseIdSet"/> — keeps the saved
    /// sequence for callers that need positions (reorder/order sorting), not
    /// just membership.</summary>
    public static IReadOnlyList<string> ParseIdList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The set of client ids the user has hidden from the top tabs
    /// (and now also from Agent limits cards), RAW as stored. A caller
    /// comparing against tab ids or expanding to every group member wants
    /// <see cref="HiddenTabClients(SettingsStore)"/> instead — this raw form
    /// remains for the Settings write path and callers that intentionally
    /// operate on member ids.</summary>
    public static IReadOnlySet<string> HiddenClients(SettingsStore store) =>
        ParseIdSet(store.GetString(TabHiddenKey) ?? "");

    /// <summary>The set of client ids whose Agent-limits card the user has
    /// hidden, independent of top-tab visibility.</summary>
    public static IReadOnlySet<string> HiddenLimitsClients(SettingsStore store) =>
        ParseIdSet(store.GetString(LimitsHiddenKey) ?? "");

    /// <summary>Clients excluded from the menu-bar quota AUTO pick: tab-hidden
    /// ∪ limits-hidden. A client hidden from either surface must not drive
    /// the tray quota % (an explicit tray selection is honored separately).
    /// Tab-hidden goes through <see cref="HiddenTabClients(SettingsStore)"/>
    /// so a legacy `antigravity-cli` hide still excludes the `antigravity`
    /// quota card and vice versa; limits-hidden stays member-specific (each
    /// group member keeps its own quota card under the shared
    /// tab).</summary>
    public static IReadOnlySet<string> QuotaExcludedClients(SettingsStore store)
    {
        var excluded = new HashSet<string>(HiddenTabClients(store), StringComparer.Ordinal);
        excluded.UnionWith(HiddenLimitsClients(store));
        return excluded;
    }

    /// <summary>The superset of client ids that can show a row in the multi-agent
    /// Agent-limits card: <paramref name="present"/> clients that carry a known
    /// limit (a placeholder row or a live quota snapshot), unioned with every
    /// client that has a quota snapshot right now. Some agents (e.g. Antigravity)
    /// report OAuth quota with no local session logs, so they are absent from
    /// <paramref name="present"/> yet must still be offered a management row.
    /// <paramref name="quotaIds"/> is the ordered list of snapshot client ids;
    /// <paramref name="placeholders"/> the ids rendered with a placeholder row
    /// even without a snapshot.</summary>
    public static IReadOnlyList<string> KnownLimitsClients(
        IReadOnlyList<string> present, IReadOnlyList<string> quotaIds, IReadOnlySet<string> placeholders)
    {
        var quotaSet = new HashSet<string>(quotaIds);
        bool Known(string id) => placeholders.Contains(id) || quotaSet.Contains(id);
        var seen = new HashSet<string>();
        return present.Where(Known).Concat(quotaIds).Where(id => seen.Add(id)).ToList();
    }

    /// <summary>Sorts <paramref name="ids"/> by the user's saved tab order
    /// (<see cref="TabOrderKey"/>), appending ids not yet in the saved order at
    /// the end in their incoming order.</summary>
    public static IReadOnlyList<string> OrderedClients(IReadOnlyList<string> ids, SettingsStore store) =>
        OrderedClients(ids, store.GetString(TabOrderKey) ?? "");

    /// <summary>Overload taking the saved order string directly, so a reactive
    /// caller re-sorts the instant the order changes without re-reading the
    /// store. Unfolded — orders MEMBER ids as stored. A caller ordering TAB
    /// ids (the tab row) wants the grouped overload below, which folds via
    /// <see cref="TabOrder"/> first.</summary>
    public static IReadOnlyList<string> OrderedClients(IReadOnlyList<string> ids, string orderRaw) =>
        OrderedClients(ids, ParseIdList(orderRaw));

    /// <summary>The sort itself, over an already-parsed order. Split from the
    /// string form so a caller (<see cref="DisplayClients(IReadOnlyList{string}, string, string)"/>)
    /// can fold grouped members onto their tab id first without a re-parse.
    /// Ids absent from <paramref name="order"/> sort last and keep their
    /// incoming relative order, so a newly discovered client appears at the
    /// end rather than at an arbitrary position.</summary>
    public static IReadOnlyList<string> OrderedClients(IReadOnlyList<string> ids, IReadOnlyList<string> order)
    {
        if (order.Count == 0)
        {
            return ids;
        }

        // First-occurrence position of each ordered id (matches Swift
        // firstIndex). A stable OrderBy preserves the incoming order among ids
        // sharing a position (unordered ids all map to int.MaxValue), which
        // reproduces Swift's explicit original-index tiebreak.
        var position = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++)
        {
            if (!position.ContainsKey(order[i]))
            {
                position[order[i]] = i;
            }
        }

        return ids
            .OrderBy(id => position.TryGetValue(id, out var p) ? p : int.MaxValue)
            .ToList();
    }

    /// <summary>Returns the subset of <paramref name="present"/> clients to show
    /// in the top tab bar, filtered by hidden list and sorted according to the
    /// user's saved order. Clients not yet in the saved order are appended at the
    /// end (so newly discovered agents become visible without breaking existing
    /// custom order).</summary>
    public static IReadOnlyList<string> DisplayClients(IReadOnlyList<string> present, SettingsStore store) =>
        DisplayClients(present, store.GetString(TabHiddenKey) ?? "", store.GetString(TabOrderKey) ?? "");

    /// <summary>Overload taking the observed hidden/order raw strings, so a
    /// reactive caller re-renders the instant the user toggles a tab or
    /// reorders instead of waiting for the next poller tick to re-read the
    /// store. Both the hidden filter and the sort go through the grouping
    /// fold (<see cref="HiddenTabClients(IReadOnlySet{string})"/> /
    /// <see cref="TabOrder"/>) — this is the one function shared by the tab
    /// row (fed grouped tab ids via <see cref="TabClients"/>) and the
    /// Overview usage selection (fed raw present client ids), matching macOS
    /// <c>displayClients(present:hiddenRaw:orderRaw:)</c>
    /// (ClientRegistry.swift :410-418).</summary>
    public static IReadOnlyList<string> DisplayClients(
        IReadOnlyList<string> present, string hiddenRaw, string orderRaw)
    {
        var hidden = HiddenTabClients(ParseIdSet(hiddenRaw));
        return OrderedClients(present.Where(id => !hidden.Contains(id)).ToList(), TabOrder(orderRaw));
    }

    /// <summary>Resolves the tab row, the active tab, and the selected client
    /// set. The selected set for Overview is present usage clients minus
    /// <see cref="HiddenTabClients(IReadOnlySet{string})"/> — NOT the tab
    /// row's ids, so Overview keeps counting a group's usage-carrying
    /// member's tokens and never selects a quota-only id (e.g. Copilot) that
    /// has no usage lens of its own. A grouped tab's own selection is its
    /// <see cref="TabSlice"/>, so a usage lens on the Antigravity tab
    /// includes antigravity-cli's usage and the quota lens still resolves
    /// antigravity via <see cref="QuotaOwner"/>. Ported from macOS
    /// `PopoverView`'s `displayUsageClients` / `presentTabClients` /
    /// `lensClientIds` split (:126-129, :137-140, :173).</summary>
    public static ClientSelection ResolveSelection(
        IReadOnlyList<string> present, IReadOnlyList<string> quotaIds,
        string hiddenRaw, string orderRaw, string? activeTab)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonicalPresent = present
            .Select(CanonicalClient)
            .Where(seen.Add)
            .ToList();
        var tabPresent = TabClients(canonicalPresent, quotaIds);
        var display = DisplayClients(tabPresent, hiddenRaw, orderRaw);
        var requested = string.IsNullOrWhiteSpace(activeTab)
            ? OverviewTab
            : FoldToTabId(CanonicalClient(activeTab.Trim()));
        var normalized = requested != OverviewTab
            && display.Contains(requested, StringComparer.Ordinal)
                ? requested
                : OverviewTab;
        var displayUsage = DisplayClients(canonicalPresent, hiddenRaw, orderRaw);
        IReadOnlyList<string> selected = normalized == OverviewTab ? displayUsage : TabSlice(normalized);
        return new ClientSelection(display, selected, normalized);
    }

    public static ClientSelection ResolveSelection(
        IReadOnlyList<string> present, IReadOnlyList<string> quotaIds, SettingsStore store) =>
        ResolveSelection(
            present,
            quotaIds,
            store.GetString(TabHiddenKey) ?? "",
            store.GetString(TabOrderKey) ?? "",
            store.GetString(ActiveTabKey));

    /// <summary>Direction-aware reorder helper (drag down inserts after, up
    /// before). Mirrors the logic used in AgentLimitsCard.</summary>
    public static IReadOnlyList<string> Reorder(IReadOnlyList<string> list, string from, string to)
    {
        var fromI = FirstIndex(list, from);
        var toI = FirstIndex(list, to);
        if (fromI < 0 || toI < 0 || fromI == toI)
        {
            return list;
        }

        var reordered = list.Where(id => id != from).ToList();
        var anchor = reordered.IndexOf(to);
        reordered.Insert(fromI < toI ? anchor + 1 : anchor, from);
        return reordered;
    }

    /// <summary>Reorder a <paramref name="visible"/> subset while preserving the
    /// positions of every id in <paramref name="full"/> that isn't part of that
    /// subset. The drag operates on the on-screen subset, yet the saved order key
    /// drives the whole tab universe: writing only the reordered visible sequence
    /// would silently drop every off-screen id. This recomputes the visible
    /// sequence, then rebuilds the full order by refilling the visible slots in
    /// their new order and leaving non-visible ids exactly where they were.
    /// Visible ids absent from <paramref name="full"/> are appended at the end
    /// (the existing "newly discovered agent" semantics).</summary>
    public static IReadOnlyList<string> MergeReorder(
        IReadOnlyList<string> full, IReadOnlyList<string> visible, string from, string to)
    {
        var newVisible = Reorder(visible, from, to);
        var visibleSet = new HashSet<string>(visible);
        var queue = new Queue<string>(newVisible);
        var merged = new List<string>();
        foreach (var id in full)
        {
            if (visibleSet.Contains(id))
            {
                // Refill this visible slot with the next id from the reordered
                // sequence. queue starts as a permutation of visible, so it has
                // at least as many ids as there are visible slots in full.
                if (queue.Count > 0)
                {
                    merged.Add(queue.Dequeue());
                }
            }
            else
            {
                merged.Add(id);
            }
        }

        // Visible ids that weren't already positioned in full land at the end.
        merged.AddRange(queue);
        return merged;
    }

    /// <summary>One-time migration: the Agent-limits drag order used to persist
    /// under "tokenbar.limits.order". It now shares <see cref="TabOrderKey"/>
    /// with the client tab bar, so fold an existing legacy value across once —
    /// otherwise upgrading users would silently lose their saved card
    /// arrangement. Idempotent: only fires when the new key is unset and a
    /// non-empty legacy value exists.</summary>
    public static void MigrateLegacyOrderKey(SettingsStore store)
    {
        const string legacyKey = "tokenbar.limits.order";
        if (store.GetString(TabOrderKey) is not null)
        {
            return;
        }

        var legacy = store.GetString(legacyKey);
        if (string.IsNullOrEmpty(legacy))
        {
            return;
        }

        store.SetString(TabOrderKey, legacy);
    }

    private static int FirstIndex(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
