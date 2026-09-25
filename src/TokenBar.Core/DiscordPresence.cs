using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Pure payload construction and preference reads for the opt-in,
/// default-off Discord Rich Presence feature. Port of macOS
/// <c>DiscordPresence.swift</c>.
///
/// Nothing here opens a pipe, touches the network or writes a file. The
/// payload builder performs no preference lookup at all: every input arrives
/// as a parameter, so the privacy tests cannot come to depend on the settings
/// of whatever machine runs them (DiscordPresence.swift :312-320). The
/// preference readers take the store as a parameter for the same reason.</summary>
public static class DiscordPresence
{
    /// <summary>Everything about the USER that is published — and only that.
    /// The transport adds four leaves of its own (pid, nonce, button label and
    /// URL), all constants or process facts, declared and pinned in
    /// <see cref="DiscordIpc"/>. An audit needs both halves
    /// (DiscordPresence.swift :13-21).</summary>
    public sealed record Payload(string Details, string State, string LargeImageKey)
    {
        /// <summary>The published surface derived from the user. The transport
        /// may rename a key on the way out (Discord nests the asset key as
        /// <c>assets.large_image</c>); it may not drop a field or add one
        /// derived from the user. <c>state</c> is omitted when empty rather than
        /// published as a blank field (DiscordPresence.swift :56-64).</summary>
        public IReadOnlyDictionary<string, string> Fields
        {
            get
            {
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["details"] = Details,
                    ["largeImageKey"] = LargeImageKey,
                };
                if (State.Length > 0)
                {
                    fields["state"] = State;
                }

                return fields;
            }
        }
    }

    /// <summary>Shown instead of an unregistered client id. Unreachable from
    /// <see cref="Build"/> — its positive allowlist drops unregistered ids
    /// before a label is chosen — and kept so <see cref="SafeClientLabel"/> is
    /// total on its own.</summary>
    public const string NeutralClientLabel = "an AI tool";

    /// <summary>Discord asset key, decided by the user on 2026-09-25 and
    /// verified present on application 1534085299163107348. Renaming it is an
    /// ADD, never a replace: a key Discord cannot resolve does not error, the
    /// image silently disappears for every build still asking for the old one
    /// (DiscordPresence.swift :70-88).</summary>
    public const string LargeImageKey = "syrtis";

    /// <summary>The opt-in switch. Default off.</summary>
    public const string EnabledKey = "tokenbar.discord.enabled";

    /// <summary>The cost-display switch. Default off means banded, the safe
    /// direction (DiscordPresence.swift :119-122).</summary>
    public const string WholeDollarsKey = "tokenbar.discord.wholeDollars";

    public const string ComponentsKey = "tokenbar.discord.components";

    public const string SelectionKey = "tokenbar.discord.client";

    /// <summary>The arguments under which this process must never connect,
    /// whatever the settings say — every non-user run mode App.xaml.cs knows:
    /// the startup probe, the icon dump, the synthetic update dialog and the
    /// two 3D dev harnesses. <c>--settings</c> and <c>--open-flyout</c> are
    /// deliberately absent: they open real UI on real data, which is a real run
    /// of the app (the line macOS draws, DiscordPresence.swift :93-104).</summary>
    public static readonly IReadOnlyList<string> TestArguments =
    [
        "--startup-smoke", "--dump-tray-icons", "--update-dialog-demo", "--graph3d", "--soak3d",
    ];

    /// <summary>The single authoritative read of the opt-in switch. Strict:
    /// only a JSON <c>true</c> counts (<see cref="SettingsStore.GetBool"/>
    /// accepts nothing but the two JSON literals), so a string "true" or a
    /// number 1 reads as off (DiscordPresence.swift :106-117, :265-282).</summary>
    public static bool Enabled(SettingsStore store) => store.GetBool(EnabledKey, false);

    /// <summary>The single authoritative read of the cost-display switch.
    /// Everything below <see cref="Build"/> takes the style as a
    /// parameter.</summary>
    public static CostStyle ReadCostStyle(SettingsStore store) =>
        store.GetBool(WholeDollarsKey, false) ? CostStyle.WholeDollars : CostStyle.Banded;

    /// <summary>The one place that decides whether this process may ever
    /// connect. Test arguments outrank the preference: fixture or harness runs
    /// on a real Discord profile are the one failure this feature cannot take
    /// back (DiscordPresence.swift :284-295).</summary>
    public static bool MayConnect(IEnumerable<string> arguments, bool enabled) =>
        !arguments.Any(TestArguments.Contains) && enabled;

    /// <summary>What the presence may be built from. Declaration ORDER is the
    /// published order: the first selected component becomes <c>details</c>,
    /// the rest join into <c>state</c> (DiscordPresence.swift :131-139).</summary>
    public enum Component
    {
        Tokens,
        Client,
        Cost,
    }

    private static readonly Component[] AllComponents = [Component.Tokens, Component.Client, Component.Cost];

    private static string Raw(Component component) => component switch
    {
        Component.Tokens => "tokens",
        Component.Client => "client",
        _ => "cost",
    };

    /// <summary>Absent means all three: an absent key must not silently empty
    /// a presence the user already consented to.</summary>
    public static IReadOnlySet<Component> DefaultComponents { get; } = new HashSet<Component>(AllComponents);

    /// <summary>Absent → all three; present but not a string → nothing; a
    /// string → its parse. Absent and malformed are different answers on
    /// purpose (DiscordPresence.swift :177-191).</summary>
    public static IReadOnlySet<Component> Components(SettingsStore store)
    {
        if (!store.TryGetString(ComponentsKey, out var raw))
        {
            return DefaultComponents;
        }

        return raw is null ? new HashSet<Component>() : ParseComponents(raw);
    }

    /// <summary>A fixed allowlist by construction: an unknown token produces
    /// nothing, is never echoed and has no fallback branch
    /// (DiscordPresence.swift :193-208).</summary>
    public static IReadOnlySet<Component> ParseComponents(string raw)
    {
        var parsed = new HashSet<Component>();
        foreach (var token in raw.Split(','))
        {
            var trimmed = token.Trim(' ', '\t');
            foreach (var component in AllComponents)
            {
                if (string.Equals(Raw(component), trimmed, StringComparison.Ordinal))
                {
                    parsed.Add(component);
                }
            }
        }

        return parsed;
    }

    /// <summary>Canonical form: declaration order, no spaces, so a reordering
    /// is never stored and never read as a change.</summary>
    public static string RawComponents(IReadOnlySet<Component> components) =>
        string.Join(',', AllComponents.Where(components.Contains).Select(Raw));

    /// <summary>Which client's usage is published (DiscordPresence.swift
    /// :141-149). A record hierarchy so equality is by value.</summary>
    public abstract record Selection
    {
        private Selection()
        {
        }

        /// <summary>The busiest visible registered client.</summary>
        public sealed record MostUsed : Selection;

        /// <summary>One named client.</summary>
        public sealed record Only(string Id) : Selection;

        /// <summary>The key holds something that is not a string. Publishes
        /// nothing, never widening to every client.</summary>
        public sealed record Malformed : Selection;
    }

    /// <summary>Matches no radio option, so a malformed stored value ticks
    /// nothing rather than claiming a selection the payload path does not
    /// agree with.</summary>
    public const string MalformedSelectionLabel = "\0malformed";

    /// <summary>Absent or empty → most used; present non-string → malformed;
    /// otherwise the named id (DiscordPresence.swift :157-168).</summary>
    public static Selection ReadSelection(SettingsStore store)
    {
        if (!store.TryGetString(SelectionKey, out var id))
        {
            return new Selection.MostUsed();
        }

        if (id is null)
        {
            return new Selection.Malformed();
        }

        return id.Length == 0 ? new Selection.MostUsed() : new Selection.Only(id);
    }

    /// <summary>The agents the Settings picker may offer, in the user's tab
    /// order: only rows that can actually publish — registered and not hidden
    /// (DiscordPresence.swift :219-263). <paramref name="present"/> null means
    /// no graph has loaded yet, which is distinct from an empty graph.</summary>
    public static IReadOnlyList<string> SelectableClients(
        IReadOnlyList<string>? present, string hiddenRaw, string orderRaw, Selection selection)
    {
        var registered = new HashSet<string>(ClientRegistry.AllIds, StringComparer.Ordinal);
        List<string> result;
        if (present is null)
        {
            var hidden = ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet(hiddenRaw));
            result = [.. ClientRegistry.OrderedClients(
                ClientRegistry.AllIds.Where(id => !hidden.Contains(id)).ToList(), orderRaw)];
        }
        else
        {
            result = ClientRegistry.DisplayClients(present, hiddenRaw, orderRaw)
                .Where(registered.Contains)
                .ToList();
        }

        // On BOTH paths: a stored selection that stopped qualifying (hidden,
        // or no usage) stays listed while it is still registered, so the
        // picker never ticks nothing while the preference names something
        // (DiscordPresence.swift :252-261).
        if (selection is Selection.Only only && registered.Contains(only.Id) && !result.Contains(only.Id))
        {
            result.Add(only.Id);
        }

        return result;
    }

    /// <summary>Fixed allowlist: a registered id gets its registry display
    /// name, anything else the neutral constant, so the registry's title-case
    /// fallback is unreachable from here (DiscordPresence.swift :297-310).</summary>
    public static string SafeClientLabel(string? id) =>
        id is not null && ClientRegistry.AllIds.Contains(id)
            ? ClientRegistry.Style(id).DisplayName
            : NeutralClientLabel;

    /// <summary>How a cost is rendered. A parameter, never a lookup, and with
    /// no default value: a default is how "the implementation ignores the
    /// setting" compiles (DiscordPresence.swift :312-324).</summary>
    public enum CostStyle
    {
        Banded,
        WholeDollars,
    }

    /// <summary>Coarse, logarithmic, open-topped cost band. Literal bounds
    /// rather than log10 so zero and negatives land in the lowest band;
    /// half-open, lower-inclusive (DiscordPresence.swift :326-364).</summary>
    public static string CostBucket(double cost)
    {
        // Before the comparisons: NaN matches no range and would otherwise
        // reach the top band.
        if (!double.IsFinite(cost))
        {
            return "<$10";
        }

        return cost switch
        {
            < 10 => "<$10",
            < 50 => "$10-50",
            < 100 => "$50-100",
            < 250 => "$100-250",
            < 500 => "$250-500",
            < 1000 => "$500-1000",
            _ => "$1000+",
        };
    }

    /// <summary>Opt-in whole dollars, never cents; total over every double.
    /// Rounded BEFORE the cap is judged, half away from zero (Swift's
    /// <c>rounded()</c>, not .NET's default banker's rounding), capped at
    /// $1000000+ (DiscordPresence.swift :366-394).</summary>
    public static string WholeDollars(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0)
        {
            return "$0";
        }

        var dollars = Math.Round(cost, MidpointRounding.AwayFromZero);
        return dollars < 1_000_000
            ? "$" + ((long)dollars).ToString(CultureInfo.InvariantCulture)
            : "$1000000+";
    }

    public static string CostText(double cost, CostStyle style) =>
        style == CostStyle.WholeDollars ? WholeDollars(cost) : CostBucket(cost);

    /// <summary>Coarse token band: never an exact count, and a negative total
    /// never publishes its digits. Do not "fix" this by changing
    /// <see cref="Format.CompactTokens"/>; the tray wants the exact small
    /// number (DiscordPresence.swift :403-415).</summary>
    public static string TokenBand(long tokens) =>
        tokens < 1_000 ? "<1K" : Format.CompactTokens(tokens);

    /// <summary>The publishable payload for <paramref name="today"/>, or null
    /// when nothing may be published. <paramref name="hidden"/> must be the
    /// tray's tab-hidden set (<see cref="ClientRegistry.HiddenTabClients(SettingsStore)"/>),
    /// which <see cref="UsagePayloadExtensions.TodayAllowlisted"/> applies to the
    /// same stripes the top client is folded from, with every stripe id
    /// canonicalized (<see cref="ClientRegistry.CanonicalClient"/>) the way the
    /// picker and the other graph consumers read them (DiscordPresence.swift
    /// :417-493).</summary>
    public static Payload? Build(
        UsagePayload graph, IReadOnlySet<string> hidden, string today, CostStyle costStyle,
        IReadOnlySet<Component> components, Selection selection)
    {
        // A positive allowlist in BOTH modes, derived here rather than in the
        // wiring: an unregistered id (cc-mirror/<user-chosen name>, or an agent
        // the aggregator knows before the registry does) contributes nothing
        // at all, rather than publishing under a neutral label.
        IReadOnlySet<string> only;
        switch (selection)
        {
            case Selection.MostUsed:
                only = new HashSet<string>(ClientRegistry.AllIds, StringComparer.Ordinal);
                break;
            case Selection.Only named:
                // Not a fallback to most-used: an unknown selection must never
                // widen "one agent" to "all of them".
                if (!ClientRegistry.AllIds.Contains(named.Id))
                {
                    return null;
                }

                only = new HashSet<string>(StringComparer.Ordinal) { named.Id };
                break;
            default:
                return null;
        }

        var totals = graph.TodayAllowlisted(hidden, today, only, ClientRegistry.CanonicalClient);
        // Non-finite numbers are never published — scoped to a cost that will
        // actually be serialized.
        if (components.Contains(Component.Cost) && !double.IsFinite(totals.Cost))
        {
            return null;
        }

        // Zero usage would publish a "this machine is on" beacon. `||`, not
        // `&&`: a day can carry cost with no tokens.
        if (!(totals.Tokens > 0 || totals.Cost > 0))
        {
            return null;
        }

        var parts = AllComponents.Where(components.Contains).Select(component => component switch
        {
            Component.Tokens => TokenBand(totals.Tokens) + " tokens today",
            Component.Client => SafeClientLabel(totals.TopClient),
            _ => CostText(totals.Cost, costStyle),
        }).ToList();
        // An empty composition publishes nothing at all; enforced here, not in
        // the Settings UI, because the store can be written by hand.
        if (parts.Count == 0)
        {
            return null;
        }

        return new Payload(parts[0], string.Join(" · ", parts.Skip(1)), LargeImageKey);
    }
}
