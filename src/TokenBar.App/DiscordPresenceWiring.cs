using System.Text.Json;
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

/// <summary>Which graph Discord is built from, and when to ask the core for a
/// graph merged with the dashboard's selected devices. Pure; the controller
/// carries the decisions out. Port of macOS <c>DiscordPresence.DeviceMerge</c>
/// (DiscordPresence.swift :139-295, Syrtis 161f7c23).
///
/// Merging this PC with other devices happens ONLY in the core
/// (<see cref="IDeviceSource.GraphDevices"/>); nothing here adds or splices
/// numbers. The tray title, client trays and every other consumer of the local
/// graph never see the merged one.</summary>
internal static class DiscordDeviceMerge
{
    /// <summary>Same value as <see cref="DeviceOverlayState.RemotePoll"/>, for
    /// the same reason: a producer grants a burst of 2 and refills one per
    /// 60 s.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(300);

    /// <summary>How much earlier than <see cref="RefreshInterval"/> a tick
    /// counts as due. Windows: Decide runs in OnGraph, after TrayFeed's fixed
    /// 300 s timer has fetched and accepted a graph, so consecutive calls are
    /// 300 s apart plus the difference of two graph-fetch durations, which can
    /// be a few seconds short (macOS sleeps 300 s AFTER its work, so it is never
    /// short). Without a margin a tick 297 s after the last refresh would be
    /// cached and the next refresh would wait a whole extra tick (~600 s).
    /// 10 s absorbs that jitter while keeping two refreshes at least 290 s
    /// apart, inside a producer's 2-burst/60 s refill. Resulting cadence with
    /// the flyout closed: one refresh per slow tick (~300 s).</summary>
    internal static readonly TimeSpan DueMargin = TimeSpan.FromSeconds(10);

    internal sealed record State
    {
        /// <summary>The merged <see cref="UsagePayload"/> only. The per-device
        /// rows are dropped: their label and address are the other owner's
        /// free text.</summary>
        public UsagePayload? Merged { get; init; }

        /// <summary>The peers <see cref="Merged"/> was fetched for.</summary>
        public IReadOnlyList<string> MergedPeers { get; init; } = [];

        public ulong Generation { get; init; }
        public DateTimeOffset? LastRefresh { get; init; }
        public bool InFlight { get; init; }

        // The inputs last seen, so any change bumps Generation.
        public bool IncludeDevices { get; init; }
        public bool DiscordOn { get; init; }
        public IReadOnlyList<string> Peers { get; init; } = [];
    }

    /// <param name="Selection">Always <c>Local: true</c>, whatever the
    /// dashboard's own tick says.</param>
    internal sealed record Call(DeviceFetchMode Mode, DeviceSelection Selection, ulong Generation);

    /// <param name="Graph">What Discord is built from this moment (never the
    /// tray's input).</param>
    internal sealed record Decision(UsagePayload? Graph, Call? Call, State State);

    /// <summary>The peers the dashboard has selected
    /// (<see cref="DeviceOverlayState.SelectionKey"/>, the
    /// <see cref="StoredDeviceSelection"/> shape it writes): deduplicated,
    /// sorted, at most <see cref="DeviceOverlayState.MaxSelectedPeers"/>.
    /// Missing or undecodable means none, as DeviceOverlayState decodes it.
    /// <c>Local</c> is ignored on purpose: Discord always counts this PC.</summary>
    internal static IReadOnlyList<string> SelectedPeers(SettingsStore store)
    {
        if (store.GetString(DeviceOverlayState.SelectionKey) is not { } json)
        {
            return [];
        }

        StoredDeviceSelection? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredDeviceSelection>(json, SelectionJson);
        }
        catch (JsonException)
        {
            return [];
        }

        return stored?.Peers is { } peers
            ? [.. peers.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Take(DeviceOverlayState.MaxSelectedPeers)]
            : [];
    }

    // DeviceOverlayState's options (JsonSerializerDefaults.Web), so the
    // lower-case keys it writes decode the same.
    private static readonly JsonSerializerOptions SelectionJson = new(JsonSerializerDefaults.Web);

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>Merge is wanted: switched on, this process may connect (the
    /// <see cref="DiscordPresence.MayConnect"/> gate) and at least one other
    /// device is selected.</summary>
    internal static bool Active(bool includeDevices, bool canConnect, IReadOnlyList<string> peers) =>
        includeDevices && canConnect && peers.Count > 0;

    /// <summary>The inputs moved: a new generation, and the old result goes
    /// now.</summary>
    internal static State Observe(State state, bool includeDevices, bool discordOn, IReadOnlyList<string> peers)
    {
        var changed = includeDevices != state.IncludeDevices || discordOn != state.DiscordOn
            || !Same(peers, state.Peers);
        var next = changed ? Invalidate(state) : state;
        return next with { IncludeDevices = includeDevices, DiscordOn = discordOn, Peers = peers };
    }

    /// <summary>A linked device was removed or re-paired: drop the result at
    /// once.</summary>
    internal static State Invalidate(State state) =>
        state with { Generation = unchecked(state.Generation + 1), Merged = null, MergedPeers = [] };

    /// <summary>The graph to publish from, re-checked against the conditions
    /// of the moment of publishing. Windows also re-checks cost authority, the
    /// gate the controller applies to this PC's graph.</summary>
    internal static UsagePayload? Graph(
        UsagePayload? local, State state, bool includeDevices, bool canConnect, IReadOnlyList<string> peers) =>
        Active(includeDevices, canConnect, peers) && state.Merged is { } merged
        && Same(state.MergedPeers, peers) && CostSurfaceProjection.IsAuthoritative(merged)
            ? merged
            : local;

    internal static Decision Decide(
        UsagePayload? local, State state, bool includeDevices, bool discordOn, bool canConnect,
        IReadOnlyList<string> peers, bool flyoutOpen, DateTimeOffset now)
    {
        var next = Observe(state, includeDevices, discordOn, peers);
        var wanted = Active(includeDevices, canConnect, peers);
        if (!wanted)
        {
            next = next with { Merged = null, MergedPeers = [] };
        }

        var used = Graph(local, next, includeDevices, canConnect, peers);
        // One call at a time; a tick that finds one running is skipped.
        if (!wanted || next.InFlight)
        {
            return new Decision(used, null, next);
        }

        // Refresh pulls every device and blocks: only while the flyout is
        // closed (the dashboard keeps last-good fresh while it is open) and at
        // most once per interval. Otherwise cached: this PC recomputed, each
        // device's last good, no connection. LastRefresh is stamped when the
        // refresh is issued, not when it succeeds: a failed or busy refresh is
        // never retried before the next due tick.
        var due = next.LastRefresh is not { } last || now - last >= RefreshInterval - DueMargin;
        var mode = !flyoutOpen && due ? DeviceFetchMode.Refresh : DeviceFetchMode.Cached;
        if (mode == DeviceFetchMode.Refresh)
        {
            next = next with { LastRefresh = now };
        }

        next = next with { InFlight = true };
        return new Decision(used, new Call(mode, new DeviceSelection(true, peers), next.Generation), next);
    }

    /// <summary>A call came back. A result from an older generation is
    /// dropped; a failure keeps whatever was there. A merged answer that is
    /// not cost-authoritative counts as a failure (its cost would publish a
    /// made-up "&lt;$10" or "$0").</summary>
    internal static State Arrived(State state, Call call, UsagePayload? payload)
    {
        var next = state with { InFlight = false };
        return payload is not null && call.Generation == state.Generation
            && CostSurfaceProjection.IsAuthoritative(payload)
            ? next with { Merged = payload, MergedPeers = call.Selection.Peers }
            : next;
    }

    /// <summary>Which direction the set of other devices on the profile moved.
    /// Removing one retires earlier queued work (it counted a device that is
    /// no longer wanted); adding one does not.</summary>
    internal static VisibilityChange Visibility(
        bool previousActive, IReadOnlyList<string> previousPeers, bool currentActive, IReadOnlyList<string> currentPeers)
    {
        IReadOnlyList<string> before = previousActive ? previousPeers : [];
        IReadOnlyList<string> current = currentActive ? currentPeers : [];
        var after = new HashSet<string>(current, StringComparer.Ordinal);
        return before.All(after.Contains) ? VisibilityChange.None : VisibilityChange.Reducing;
    }
}

/// <summary>Reconciles the presence with the settings and the latest accepted
/// graph. Start, stop and publish all run through <see cref="Apply"/>, so
/// there is a single gated path and every entry point is only a trigger
/// (AppDelegate.swift :315-339). Thread-safe: settings writes arrive on the
/// writing thread, graphs on the UI thread.</summary>
internal sealed class DiscordPresenceController
{
    /// <summary>The values whose change re-publishes immediately
    /// (AppDelegate.swift :397-442, :540-600). The hidden set is the TRAY's
    /// (<see cref="ClientRegistry.TabHiddenKey"/>, read through
    /// <see cref="ClientRegistry.HiddenTabClients(SettingsStore)"/> exactly as
    /// TrayFeed does), not the limits or quota exclusions. The device switch
    /// and the dashboard's device selection are merge inputs: a change drops
    /// the merged result at once.</summary>
    internal static readonly IReadOnlySet<string> WatchedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        ClientRegistry.TabHiddenKey,
        DiscordPresence.EnabledKey,
        DiscordPresence.WholeDollarsKey,
        DiscordPresence.ComponentsKey,
        DiscordPresence.SelectionKey,
        DiscordPresence.IncludeDevicesKey,
        DeviceOverlayState.SelectionKey,
    };

    private readonly object _gate = new();
    private readonly SettingsStore _store;
    private readonly IReadOnlyList<string> _arguments;
    private readonly Func<DiscordIpcClient> _makeClient;
    private readonly Func<string> _today;
    // Null: the device merge is inert (no overlay in this process).
    private readonly IDeviceSource? _devices;
    private readonly Func<bool> _flyoutOpen;
    private readonly Func<DateTimeOffset> _now;
    // Under _gate. Only DiscordDeviceMerge.Graph reads its result, and only
    // for Discord.
    private DiscordDeviceMerge.State _merge = new();
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
        IReadOnlySet<Component> Components, Selection Selection,
        bool IncludeDevices, IReadOnlyList<string> SelectedPeers)
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
                DiscordPresence.ReadSelection(store),
                DiscordPresence.IncludeDevices(store),
                DiscordDeviceMerge.SelectedPeers(store));
        }

        // SequenceEqual, not ==: == on two lists compares references, and
        // every read builds a new one.
        public bool SameAs(Snapshot other) =>
            HiddenRaw == other.HiddenRaw && Enabled == other.Enabled && CostStyle == other.CostStyle
            && ComponentsRaw == other.ComponentsRaw && Selection == other.Selection
            && IncludeDevices == other.IncludeDevices
            && SelectedPeers.SequenceEqual(other.SelectedPeers, StringComparer.Ordinal);
    }

    /// <param name="makeClient">Called at most once, and only after
    /// <see cref="DiscordPresence.MayConnect"/> said yes — the one place a
    /// connection can come into existence.</param>
    /// <param name="devices">The device source for the merged graph; null
    /// makes the device merge inert.</param>
    /// <param name="flyoutOpen">Read inside <see cref="OnGraph"/> only, on the
    /// thread that calls it (the UI thread in the app).</param>
    internal DiscordPresenceController(
        SettingsStore store, IReadOnlyList<string> arguments,
        Func<DiscordIpcClient> makeClient, Func<string>? today = null,
        IDeviceSource? devices = null, Func<bool>? flyoutOpen = null, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _arguments = arguments;
        _makeClient = makeClient;
        _today = today ?? (() => Format.TodayKey());
        _devices = devices;
        _flyoutOpen = flyoutOpen ?? (() => false);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _last = Snapshot.Read(store);
        _merge = DiscordDeviceMerge.Observe(_merge, _last.IncludeDevices, _last.Enabled, _last.SelectedPeers);
    }

    /// <summary>The last device call started, for tests to await.</summary>
    internal Task? MergeCallForTesting { get; private set; }

    /// <summary>Sees every payload handed to the client, under the lock, with
    /// its change, so a test can read what was published without a
    /// pipe.</summary>
    internal Action<DiscordPresence.Payload?, VisibilityChange>? PublishedForTesting { get; set; }

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
            // The periodic entry of the device merge (macOS runDiscordMerge,
            // AppDelegate.swift :743-749): after the local path, so the local
            // graph never waits on a device.
            DecideMerge();
            Apply(VisibilityChange.None);
        }
    }

    /// <summary>Under <see cref="_gate"/>, from <see cref="OnGraph"/> only.
    /// Decides and, when the decision is a call, runs it on the thread pool
    /// with no lock held. Its completion re-enters under <see cref="_gate"/>
    /// once and, in that single critical section, stores the answer
    /// (<see cref="DiscordDeviceMerge.Arrived"/>: generation and cost
    /// authority) and republishes, so a late answer cannot be published after
    /// a reduction that dropped it.</summary>
    private void DecideMerge()
    {
        if (_devices is not { } source)
        {
            return;
        }

        var enabled = DiscordPresence.Enabled(_store);
        var decision = DiscordDeviceMerge.Decide(
            _graph, _merge, DiscordPresence.IncludeDevices(_store), enabled,
            DiscordPresence.MayConnect(_arguments, enabled), DiscordDeviceMerge.SelectedPeers(_store),
            _flyoutOpen(), _now());
        _merge = decision.State;
        if (decision.Call is not { } call)
        {
            return;
        }

        MergeCallForTesting = Task.Run(() =>
        {
            UsagePayload? payload;
            try
            {
                // Only the UsagePayload: the device rows are the other owner's
                // text and are dropped here.
                payload = source.GraphDevices(null, call.Selection, call.Mode).Payload;
            }
            catch (Exception)
            {
                // Logged nowhere: a message could carry a peer or a figure. A
                // failure keeps the last result.
                payload = null;
            }

            lock (_gate)
            {
                _merge = DiscordDeviceMerge.Arrived(_merge, call, payload);
                // An addition: no retire. Coalesced when nothing changed.
                Apply(VisibilityChange.None);
            }
        });
    }

    /// <summary>A linked device was removed or re-paired (Settings → Devices,
    /// DevicesModel.PeersChanged): the merged result may count it, so it goes
    /// now and what replaces it retires queued work — also with no merged
    /// result yet, since a call started before the removal would otherwise
    /// come back under the same generation (AppDelegate.swift :205-216).</summary>
    internal void OnPeersChanged()
    {
        lock (_gate)
        {
            _merge = DiscordDeviceMerge.Invalidate(_merge);
            Apply(VisibilityChange.Reducing);
        }
    }

    /// <summary>A settings write. Value-gated: only a real change to one of
    /// the watched values re-publishes, classified, immediately. Never starts
    /// a device call and never reads the flyout (only OnGraph does).</summary>
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
            // Every change reaches the merge's inputs: a new generation drops
            // the merged result at once (AppDelegate.swift :540-552).
            var previousMerge = _merge;
            var wasMergeActive = previousMerge.IncludeDevices && previousMerge.DiscordOn
                && previousMerge.Peers.Count > 0;
            var mergeActive = next.IncludeDevices && next.Enabled && next.SelectedPeers.Count > 0;
            _merge = DiscordDeviceMerge.Observe(previousMerge, next.IncludeDevices, next.Enabled, next.SelectedPeers);
            // Today's actual contributors, so hiding a client with no usage
            // today is not mistaken for taking something down. From the graph
            // published before this pass AND the one in use now: a client with
            // usage only on another device is in no local graph, and Observe
            // may already have dropped the merged result (AppDelegate.swift
            // :563-579). Null (no graph yet) means do not narrow.
            var outgoing = wasMergeActive ? (previousMerge.Merged ?? _graph) : _graph;
            var current = DiscordDeviceMerge.Graph(
                _graph, _merge, next.IncludeDevices, DiscordPresence.MayConnect(_arguments, next.Enabled),
                next.SelectedPeers);
            IReadOnlySet<string>? contributors = outgoing is null && current is null
                ? null
                : new[] { outgoing, current }.OfType<UsagePayload>()
                    .SelectMany(graph => TodayContributors(graph, _today()))
                    .ToHashSet(StringComparer.Ordinal);
            var change = DiscordClassifiers.Visibility(
                    previous.HiddenRaw, next.HiddenRaw, previous.Selection, next.Selection, contributors)
                .Combined(DiscordClassifiers.CostStyleChange(
                    previous.CostStyle, next.CostStyle,
                    previous.Components.Contains(Component.Cost) && next.Components.Contains(Component.Cost)))
                .Combined(DiscordClassifiers.ComponentsChange(previous.Components, next.Components))
                .Combined(DiscordClassifiers.SelectionChange(
                    previous.Selection, next.Selection,
                    ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet(next.HiddenRaw))))
                .Combined(DiscordDeviceMerge.Visibility(
                    wasMergeActive, previousMerge.Peers, mergeActive, next.SelectedPeers));
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
        // The merged graph only while every condition still holds at this
        // moment (read from the store now), else this PC's.
        var graph = DiscordDeviceMerge.Graph(
            _graph, _merge, DiscordPresence.IncludeDevices(_store), canConnect: true,
            DiscordDeviceMerge.SelectedPeers(_store));
        if (graph is null)
        {
            return; // nothing publishes before the first accepted graph
        }

        var payload = DiscordPresence.Build(
            graph,
            ClientRegistry.HiddenTabClients(_store),
            _today(),
            DiscordPresence.ReadCostStyle(_store),
            DiscordPresence.Components(_store),
            DiscordPresence.ReadSelection(_store));
        PublishedForTesting?.Invoke(payload, change);
        _client.Publish(payload, change);
    }
}

/// <summary>Every string the Discord section shows, as English
/// keys into strings-zh-Hant.json. The consent copy is macOS SettingsPanel
/// :944-1006, and its zh-Hant is macOS Localizable.strings, verbatim except
/// that the product-name occurrences read "Syrtis" (user decision
/// 2026-09-26). The launch-time intro's strings went with the intro (macOS
/// 2e25c7ff). DiscordWiringTests.ConsentCopyIsMacOsWithTheProductRenamed
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

    // macOS SettingsPanel.swift :1019-1020; zh from Localizable.strings
    // zh-Hant :275-276, zh-Hans :274-275.
    internal const string IncludeDevices = "Include other devices selected in the dashboard";

    internal const string IncludeDevicesHint = "Other devices' usage for today updates about every 5 minutes.";

    internal const string WholeDollarsHint =
        "A range keeps you among everyone else in that band. A figure is rounded to the dollar, never cents, but still says more about you — every day. With one client named above, it becomes that tool's daily spend.";
}
