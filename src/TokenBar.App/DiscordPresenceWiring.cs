using TokenBar.Core;
using TokenBar.Interop;
using Component = TokenBar.Core.DiscordPresence.Component;
using CostStyle = TokenBar.Core.DiscordPresence.CostStyle;
using Selection = TokenBar.Core.DiscordPresence.Selection;
using VisibilityChange = TokenBar.Core.DiscordIpc.VisibilityChange;

namespace TokenBar.App;

// Every decision the Discord wiring makes, kept free of WinUI so
// TokenBar.Core.Tests can link this file (same reason as UpdateFlow.cs):
// TrayService, App and SettingsWindow compile under no test project.

/// <summary>How a preference write changed what may be published. Port of
/// macOS AppDelegate.swift :175-313. The classification decides only whether
/// earlier queued work is stale — never how fast the change reaches the wire;
/// every change waits out the publish floor, which the consent copy
/// states.</summary>
internal static class DiscordClassifiers
{
    /// <summary>Did the user take a published client off the profile, or put
    /// one back? Asked against the clients published at EITHER endpoint, so
    /// switching selection while hiding the old one in the same write still
    /// reads as a reduction. A write that hides one client and unhides another
    /// is reducing: content the user removed outranks the sampling rate
    /// (AppDelegate.swift :175-219).</summary>
    internal static VisibilityChange Visibility(
        string previousHiddenRaw, string hiddenRaw,
        Selection previousSelection, Selection selection,
        IReadOnlySet<string>? contributors = null)
    {
        var previousHidden = ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet(previousHiddenRaw));
        var currentHidden = ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet(hiddenRaw));
        var wasPublished = EffectivePublished(previousSelection, previousHidden, contributors);
        var isPublished = EffectivePublished(selection, currentHidden, contributors);
        if (wasPublished.Overlaps(currentHidden.Except(previousHidden)))
        {
            return VisibilityChange.Reducing;
        }

        return isPublished.Overlaps(previousHidden.Except(currentHidden))
            ? VisibilityChange.Increasing
            : VisibilityChange.None;
    }

    /// <summary>The one direction test both set-shaped preferences use; the
    /// first set is the one whose GROWTH means less is published
    /// (AppDelegate.swift :221-231).</summary>
    internal static VisibilityChange Subset<T>(IReadOnlySet<T> grownMeansLessPrevious, IReadOnlySet<T> current)
    {
        if (!current.IsSubsetOf(grownMeansLessPrevious))
        {
            return VisibilityChange.Reducing;
        }

        return !grownMeansLessPrevious.IsSubsetOf(current)
            ? VisibilityChange.Increasing
            : VisibilityChange.None;
    }

    /// <summary>What a selection would actually publish under a hidden set,
    /// narrowed to today's contributors when known, so hiding a registered
    /// client with no usage today is not mistaken for a reduction
    /// (AppDelegate.swift :245-269).</summary>
    internal static HashSet<string> EffectivePublished(
        Selection selection, IReadOnlySet<string> hidden, IReadOnlySet<string>? contributors = null)
    {
        var registered = ClientRegistry.AllIds;
        IEnumerable<string> selected = selection switch
        {
            Selection.MostUsed => registered,
            Selection.Only only when registered.Contains(only.Id) => [only.Id],
            _ => [],
        };
        var visible = new HashSet<string>(selected, StringComparer.Ordinal);
        visible.ExceptWith(hidden);
        if (contributors is not null)
        {
            visible.IntersectWith(contributors);
        }

        return visible;
    }

    /// <summary>A selection change replaces what is published, so earlier work
    /// is stale — unless both select the same effective set
    /// (AppDelegate.swift :271-280).</summary>
    internal static VisibilityChange SelectionChange(
        Selection previous, Selection current, IReadOnlySet<string> hidden) =>
        EffectivePublished(previous, hidden).SetEquals(EffectivePublished(current, hidden))
            ? VisibilityChange.None
            : VisibilityChange.Retiring;

    /// <summary>Unticking a component is the reduction — the mirror of the
    /// hidden set, so the arguments go in swapped (AppDelegate.swift
    /// :282-286).</summary>
    internal static VisibilityChange ComponentsChange(
        IReadOnlySet<Component> previous, IReadOnlySet<Component> current) =>
        Subset(current, previous);

    /// <summary>Whole dollars → banded is a reduction; the reverse adds
    /// precision back. No change unless cost is published on both sides
    /// (AppDelegate.swift :288-313).</summary>
    internal static VisibilityChange CostStyleChange(
        CostStyle previous, CostStyle current, bool publishedInBoth = true)
    {
        if (!publishedInBoth)
        {
            return VisibilityChange.None;
        }

        return (previous, current) switch
        {
            (CostStyle.WholeDollars, CostStyle.Banded) => VisibilityChange.Reducing,
            (CostStyle.Banded, CostStyle.WholeDollars) => VisibilityChange.Increasing,
            _ => VisibilityChange.None,
        };
    }
}

/// <summary>Reconciles the presence with the settings and the latest accepted
/// graph. Start, stop and publish all run through <see cref="Apply"/>, so
/// there is a single gated path and every entry point is only a trigger
/// (AppDelegate.swift :315-339). Thread-safe: settings writes arrive on the
/// writing thread, graphs on the UI thread.</summary>
internal sealed class DiscordPresenceController
{
    /// <summary>The five values whose change re-publishes immediately
    /// (AppDelegate.swift :397-442). The hidden set is the TRAY's
    /// (<see cref="ClientRegistry.TabHiddenKey"/>, read through
    /// <see cref="ClientRegistry.HiddenTabClients(SettingsStore)"/> exactly as
    /// TrayFeed does), not the limits or quota exclusions.</summary>
    internal static readonly IReadOnlySet<string> WatchedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        ClientRegistry.TabHiddenKey,
        DiscordPresence.EnabledKey,
        DiscordPresence.WholeDollarsKey,
        DiscordPresence.ComponentsKey,
        DiscordPresence.SelectionKey,
    };

    private readonly object _gate = new();
    private readonly SettingsStore _store;
    private readonly IReadOnlyList<string> _arguments;
    private readonly Func<DiscordIpcClient> _makeClient;
    private readonly Func<string> _today;
    private DiscordIpcClient? _client;
    // True once Stop() has been sent for the current off period, so repeated
    // triggers while off (every graph, every watched write) do not each queue
    // another worker item and bump the epoch again.
    private bool _stopped;
    // The latest graph that was cost-authoritative when it arrived. Never a
    // LocalFirst (unpriced) graph; see OnGraph.
    private UsagePayload? _graph;
    private Snapshot _last;

    private sealed record Snapshot(
        string HiddenRaw, bool Enabled, CostStyle CostStyle, string ComponentsRaw,
        IReadOnlySet<Component> Components, Selection Selection)
    {
        public static Snapshot Read(SettingsStore store)
        {
            var components = DiscordPresence.Components(store);
            return new Snapshot(
                store.GetString(ClientRegistry.TabHiddenKey) ?? string.Empty,
                DiscordPresence.Enabled(store),
                DiscordPresence.ReadCostStyle(store),
                // Compared in canonical form, so a reordered or respaced write
                // is not read as a change to what gets published.
                DiscordPresence.RawComponents(components),
                components,
                DiscordPresence.ReadSelection(store));
        }

        public bool SameAs(Snapshot other) =>
            HiddenRaw == other.HiddenRaw && Enabled == other.Enabled && CostStyle == other.CostStyle
            && ComponentsRaw == other.ComponentsRaw && Selection == other.Selection;
    }

    /// <param name="makeClient">Called at most once, and only after
    /// <see cref="DiscordPresence.MayConnect"/> said yes — the one place a
    /// connection can come into existence.</param>
    internal DiscordPresenceController(
        SettingsStore store, IReadOnlyList<string> arguments,
        Func<DiscordIpcClient> makeClient, Func<string>? today = null)
    {
        _store = store;
        _arguments = arguments;
        _makeClient = makeClient;
        _today = today ?? (() => Format.TodayKey());
        _last = Snapshot.Read(store);
    }

    /// <summary>Launch: if enabled, start the worker so the connection is up
    /// when the first graph lands (AppDelegate.swift :150-154). Nothing is
    /// published until then.</summary>
    internal void Launch()
    {
        lock (_gate)
        {
            Apply(VisibilityChange.None);
        }
    }

    /// <summary>Every accepted graph republishes with no visibility change —
    /// "on the same cadence the tray title refreshes on" (AppDelegate.swift
    /// :573-576). The client coalesces an unchanged payload.
    ///
    /// Gated on a NEW graph instance: TrayFeed.Changed also fires on every
    /// 30 s trace tick and quota fetch, and each republish also restarts a
    /// client that spent its reconnect budget. Reacting to those would retry a
    /// missing Discord every 30 s instead of once per graph refresh.
    ///
    /// Gated on cost authority too, decided from THIS graph instance by the
    /// rule TrayFeed applies (<see cref="CostSurfaceProjection.IsAuthoritative"/>).
    /// Not from TrayFeed.CostAuthoritative: that flag is a separate read that
    /// GraphConsumerState.TryAcceptGraph rewrites on a background thread, so a
    /// LocalFirst graph could be paired with the flag a concurrent Richer
    /// accept just set. Deriving it from the instance cannot disagree with the
    /// graph. A LocalFirst graph skips
    /// pricing on purpose (TbCore.GraphLocalFirst), so its cost would publish a
    /// fabricated "&lt;$10" or "$0" to a public profile until the Richer graph
    /// corrected it a floor later. Such a graph is ignored ALTOGETHER — not
    /// just its cost — so the payload never mixes two stages; the controller
    /// keeps the latest authoritative graph, which settings-triggered
    /// republishes also use, and the next authoritative graph publishes as
    /// usual. A profile whose Richer graph has no priced message at all
    /// (coverage None) is never authoritative and so never publishes, the same
    /// state in which the tray reads "Checking".</summary>
    internal void OnGraph(UsagePayload? graph)
    {
        if (graph is null || !CostSurfaceProjection.IsAuthoritative(graph))
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(graph, _graph))
            {
                return;
            }

            _graph = graph;
            Apply(VisibilityChange.None);
        }
    }

    /// <summary>A settings write. Value-gated: only a real change to one of
    /// the five watched values re-publishes, classified, immediately.</summary>
    internal void OnSettingChanged(string key)
    {
        if (!WatchedKeys.Contains(key))
        {
            return;
        }

        lock (_gate)
        {
            var next = Snapshot.Read(_store);
            var previous = _last;
            if (next.SameAs(previous))
            {
                return;
            }

            _last = next;
            // Today's actual contributors, so hiding a client with no usage
            // today is not mistaken for taking something down. Null (no graph
            // yet) means do not narrow.
            var contributors = _graph is null ? null : TodayContributors(_graph, _today());
            var change = DiscordClassifiers.Visibility(
                    previous.HiddenRaw, next.HiddenRaw, previous.Selection, next.Selection, contributors)
                .Combined(DiscordClassifiers.CostStyleChange(
                    previous.CostStyle, next.CostStyle,
                    previous.Components.Contains(Component.Cost) && next.Components.Contains(Component.Cost)))
                .Combined(DiscordClassifiers.ComponentsChange(previous.Components, next.Components))
                .Combined(DiscordClassifiers.SelectionChange(
                    previous.Selection, next.Selection,
                    ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet(next.HiddenRaw))));
            Apply(change);
        }
    }

    /// <summary>The canonical ids with a stripe today, over EVERY contribution
    /// dated today — the same stripes and the same canonicalization the payload
    /// is folded from (UsagePayloadExtensions.TodayAllowlisted), so an alias
    /// stripe (<c>claude-code</c>) makes hiding <c>claude</c> a reduction.</summary>
    internal static IReadOnlySet<string> TodayContributors(UsagePayload graph, string today) =>
        graph.Contributions
            .Where(c => c.Date == today)
            .SelectMany(c => c.Clients)
            .Select(c => ClientRegistry.CanonicalClient(c.Client))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Quit: queue the clear and wait for it, bounded on THIS side
    /// (AppDelegate.swift :468-495). The clear is the mechanism and the pipe
    /// closing the backstop; whether Discord drops an activity when the pipe
    /// just closes is unmeasured.</summary>
    internal void Quit(TimeSpan wait)
    {
        DiscordIpcClient? client;
        lock (_gate)
        {
            client = _client;
        }

        if (client is null)
        {
            return;
        }

        client.Stop();
        try
        {
            _ = client.DrainAsync().Wait(wait);
        }
        catch
        {
            // Quit must not be the thing that throws.
        }
    }

    private void Apply(VisibilityChange change)
    {
        if (!DiscordPresence.MayConnect(_arguments, DiscordPresence.Enabled(_store)))
        {
            // The client is kept, not dropped: Stop() only QUEUES the clear,
            // and Quit's bounded drain needs the reference to wait on it
            // (AppDelegate.swift :319-335).
            if (_client is not null && !_stopped)
            {
                _client.Stop();
                _stopped = true;
            }

            return;
        }

        _client ??= _makeClient();
        _stopped = false;
        _client.Start();
        if (_graph is null)
        {
            return; // nothing publishes before the first accepted graph
        }

        _client.Publish(
            DiscordPresence.Build(
                _graph,
                ClientRegistry.HiddenTabClients(_store),
                _today(),
                DiscordPresence.ReadCostStyle(_store),
                DiscordPresence.Components(_store),
                DiscordPresence.ReadSelection(_store)),
            change);
    }
}

/// <summary>The one-time card that makes the default-off feature findable.
/// It enables nothing and offers no path to on: the only route is the
/// Settings toggle, where the full disclosure is (DiscordIntro.swift
/// :5-19).</summary>
internal static class DiscordIntro
{
    /// <summary>Set when the card is PRESENTED, not when it is acted on, and
    /// deliberately not versioned (DiscordIntro.swift :21-31).</summary>
    internal const string ShownKey = "tokenbar.discord.introShown";

    /// <summary>Whether to present — and it CONSUMES the flag either way, so
    /// someone who already had the feature on is never introduced to it later
    /// after switching it off (DiscordIntro.swift :39-51).</summary>
    internal static bool Consume(SettingsStore store)
    {
        if (store.GetBool(ShownKey, false))
        {
            return false;
        }

        store.SetBool(ShownKey, true);
        return !DiscordPresence.Enabled(store);
    }
}

/// <summary>Every string the Discord section and the intro show, as English
/// keys into strings-zh-Hant.json. The consent copy is macOS SettingsPanel
/// :944-1006 and DiscordIntro.swift :104-108, and its zh-Hant is macOS
/// Localizable.strings :267-281, verbatim except that the product-name
/// occurrences read "Syrtis" (user decision 2026-09-26). The Discord activity
/// title stays "TokenBar". DiscordWiringTests.ConsentCopyIsMacOsWithTheProductRenamed
/// pins both languages against the macOS text and the exact list of
/// renamed occurrences.</summary>
internal static class DiscordCopy
{
    internal const string Section = "Discord";

    internal const string Toggle = "Show today's usage on Discord";

    internal const string Consent =
        "Off by default. Publishes what you pick below — today's tokens, a client name, a cost range or rounded figure — for whichever client you choose, and a link to Syrtis's source to your Discord profile. It updates while you work, so your active hours show too. Anyone who can see your profile can read and keep every update; switching this off stops new ones but cannot unshare what already went out. Hidden clients are never included, and a change here reaches your profile within about 15 seconds.";

    internal const string IncludeTokens = "Include today's tokens";

    internal const string IncludeClient = "Include the client name";

    internal const string IncludeCost = "Include cost";

    internal const string UntickHint = "Untick everything and nothing is published at all.";

    internal const string MostUsed = "Whichever client you used most";

    internal const string NamingHint =
        "Naming one client publishes only its usage, so the totals can differ from the menu bar, which counts every client including ones Syrtis does not recognise. The cost becomes that one tool's daily spend rather than the whole day's.";

    internal const string WholeDollars = "Show cost as a figure instead of a range";

    internal const string WholeDollarsHint =
        "A range keeps you among everyone else in that band. A figure is rounded to the dollar, never cents, but still says more about you — every day. With one client named above, it becomes that tool's daily spend.";

    internal const string IntroBody =
        "Your Discord profile can show what you have been building today. Pick exactly what appears — or nothing at all — in Settings.";

    internal const string OpenSettings = "Open Settings";

    internal const string NotNow = "Not now";

    /// <summary>The intro's mock activity: representative values labelled as
    /// a preview, never the user's figures (DiscordIntro.swift :76-95). The
    /// title is the Discord portal application's name, which is what Discord
    /// shows, so it is not translated.</summary>
    // The Discord portal application's name, which is what Discord shows as
    // the activity title. Renamed from TokenBar to Syrtis on the portal on
    // 2026-09-26 (GET /api/v10/applications/1534085299163107348/rpc returned
    // name "Syrtis" that day); keep the two in step.
    internal const string PreviewTitle = "Syrtis";

    internal const string PreviewDetails = "1.2M tokens today";

    internal const string PreviewState = "Claude Code · $10-50";
}
