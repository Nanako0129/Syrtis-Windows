using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

// The stateful device overlay of the dashboard (SA-4e2), ported from macOS
// DeviceOverlayState (DashboardModel.swift :2914-3801, Syrtis f8a1a0ae). No UI
// in this file. SA-4g wires it: App builds the one instance of the process,
// DashboardView ensures through DeviceOverlaySync (once per key, a poll while
// the flyout is open, PopoverClosed on hide) and Settings → Devices passes a
// link or removal to RefreshPeers.

/// <summary>The device-aware pages and the peer list. Every member BLOCKS (a
/// Refresh pulls each selected producer, up to one fetch deadline each), so the
/// overlay calls them on the thread pool, never on the UI thread. Cross-source
/// merging is the core's: these return what <c>tb_*_devices</c> computed.</summary>
public interface IDeviceSource
{
    IReadOnlyList<RemotePeer> RemotePeers();
    DeviceAwarePayload<UsagePayload> GraphDevices(string? year, DeviceSelection selection, DeviceFetchMode mode);
    DeviceAwarePayload<ModelReport> ModelReportDevices(string? year, DeviceSelection selection, DeviceFetchMode mode);
    DeviceAwarePayload<HourlyReport> HourlyReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode);
    DeviceAwarePayload<AgentsReport> AgentsReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode);
}

/// <summary>The production source: the 4d <see cref="TbCore"/> wrappers, which
/// pass <see cref="TbCore.LocalIanaTimeZoneId"/> themselves and throw
/// <c>timezoneUnavailable</c> before the native call. Tests never construct
/// it (no native call from a test).</summary>
public sealed class TbDeviceSource : IDeviceSource
{
    public IReadOnlyList<RemotePeer> RemotePeers() => TbCore.RemotePeers();

    public DeviceAwarePayload<UsagePayload> GraphDevices(
        string? year, DeviceSelection selection, DeviceFetchMode mode) =>
        TbCore.GraphDevices(year, selection, mode);

    public DeviceAwarePayload<ModelReport> ModelReportDevices(
        string? year, DeviceSelection selection, DeviceFetchMode mode) =>
        TbCore.ModelReportDevices(year, selection, mode);

    public DeviceAwarePayload<HourlyReport> HourlyReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode) =>
        TbCore.HourlyReportDevices(year, clients, selection, mode);

    public DeviceAwarePayload<AgentsReport> AgentsReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode) =>
        TbCore.AgentsReportDevices(year, clients, selection, mode);
}

/// <summary>Time for the overlay: "now" (the 60 s freshness window, the age in
/// the status lines) and delays (the 300 s poll, the 65 s retry). A seam so a
/// test advances time instead of sleeping.</summary>
public interface IDeviceClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellation);
}

public sealed class SystemDeviceClock : IDeviceClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
}

/// <summary>What an overlay was fetched for (macOS DeviceOverlayKey :2921). A
/// response lands only while its key is still the one the lens wants; a slot
/// is shown only while its key is the one the lens wants now.</summary>
public sealed class DeviceOverlayKey : IEquatable<DeviceOverlayKey>
{
    public DeviceOverlayKey(
        DeviceOverlayKind kind, string year, IReadOnlyList<string>? clients, bool local, IReadOnlyList<string> peers)
    {
        Kind = kind;
        Year = year;
        Clients = clients;
        Local = local;
        Peers = peers;
    }

    public DeviceOverlayKind Kind { get; }

    /// <summary>"" = all time.</summary>
    public string Year { get; }

    /// <summary>Hourly and Agents only; null = every client. Sorted and unique.</summary>
    public IReadOnlyList<string>? Clients { get; }

    public bool Local { get; }

    /// <summary>Sorted <c>ProducerStatic</c> values.</summary>
    public IReadOnlyList<string> Peers { get; }

    public DeviceSelection Selection => new(Local, Peers);

    public string? YearArgument => Year.Length == 0 ? null : Year;

    public bool Equals(DeviceOverlayKey? other) =>
        other is not null && Kind == other.Kind && Year == other.Year && Local == other.Local
        && Peers.SequenceEqual(other.Peers)
        && (Clients is null ? other.Clients is null : other.Clients is not null && Clients.SequenceEqual(other.Clients));

    public override bool Equals(object? obj) => Equals(obj as DeviceOverlayKey);

    public override int GetHashCode() => HashCode.Combine(Kind, Year, Local, Peers.Count, Clients?.Count);
}

/// <summary>One overlay: the core's merged page for <see cref="Key"/>, kept
/// beside (never in place of) the local data. <see cref="Failed"/>: the last
/// refresh for this key failed; <see cref="Data"/> is then the last good
/// answer, or null when there never was one.</summary>
public sealed record DeviceOverlaySlot<T>(DeviceOverlayKey Key, DeviceAwarePayload<T>? Data, bool Failed);

/// <summary>The persisted choice (<c>tokenbar.dashboard.deviceSelection</c>,
/// JSON <c>{"local":true,"peers":[...]}</c>, the macOS shape). Peers are
/// <c>ProducerStatic</c> values.</summary>
public sealed class StoredDeviceSelection : IEquatable<StoredDeviceSelection>
{
    public bool Local { get; init; } = true;
    public IReadOnlyList<string> Peers { get; init; } = [];

    public bool Equals(StoredDeviceSelection? other) =>
        other is not null && Local == other.Local && Peers.SequenceEqual(other.Peers);

    public override bool Equals(object? obj) => Equals(obj as StoredDeviceSelection);

    public override int GetHashCode() => HashCode.Combine(Local, Peers.Count);
}

/// <summary>What Overview shows of the selected devices' combined pages
/// (macOS OverviewMerge, OverviewView.swift :9-42; the pure parts are
/// <see cref="OverviewMerge"/>). A page that has not arrived is null; this PC's
/// own numbers are never its stand-in.</summary>
public sealed record OverviewMergeState(
    int DeviceCount,
    (UsagePayload Payload, UsageStats Stats)? Graph,
    ModelReport? Models,
    ModelColorMap Colors,
    IReadOnlyList<string> ClientIds,
    IReadOnlyList<string> ModelClientIds,
    bool GraphFailed,
    bool ModelsFailed,
    bool KeptLast,
    bool Saturated)
{
    public IReadOnlyList<string> NoteLines => OverviewMerge.NoteLines(Saturated, KeptLast);
}

/// <summary>The device overlay of the Graph (Stats, Daily, Monthly), Models,
/// Hourly and Agents lenses.
///
/// Isolation, by construction: it holds the core's merged pages and nothing
/// else, and takes no reference to <c>DashboardModel</c>; nothing here adds,
/// subtracts or splices a local value with a remote one.
///
/// Threading: every member is callable from any thread. One process-wide lock
/// guards the state of every instance (the single-flight refresh spans
/// instances, so per-instance locks would need an order). Blocking source calls
/// run on the thread pool outside the lock; each result is applied under the
/// lock, and <see cref="Changed"/> / <see cref="SnapshotChanged"/> are raised
/// after it is released, on whichever thread applied the change, so a UI
/// handler must marshal to its dispatcher itself.</summary>
public sealed class DeviceOverlayState
{
    public const string SelectionKey = "tokenbar.dashboard.deviceSelection";

    /// <summary>Peers this app has already offered for selection (JSON array of
    /// <c>ProducerStatic</c>). Apart from <see cref="SelectionKey"/> so an older
    /// selection decodes unchanged. A device the user unchecked stays in it; a
    /// device that was checked when it stopped being active leaves it, so
    /// linking it again checks it again.</summary>
    public const string SeenPeersKey = "tokenbar.dashboard.deviceSeenPeers";

    /// <summary>A key refreshed this recently is not asked for again when a lens
    /// comes on screen: a producer grants a burst of 2 and refills one per 60 s.
    /// A lens asks for its own page(s) plus the header's graph page, so a lens
    /// with one page of its own uses the whole burst. A manual refresh, a poll
    /// and a retry still ask.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>How often the lens on screen is refreshed (a producer refills one
    /// request per 60 s and a refresh asks for the lens's page(s) plus the
    /// header's graph page: two requests on Overview, Stats, Models, Hourly
    /// and Agents, one on Daily, Monthly and Quota).</summary>
    public static readonly TimeSpan RemotePoll = TimeSpan.FromSeconds(300);

    /// <summary>One delayed retry after a refresh that left a selected device
    /// without new data: 60 s refill plus a 5 s margin.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(65);

    /// <summary>The core refuses more than 16 producers in one call.</summary>
    public const int MaxSelectedPeers = 16;

    private static readonly DeviceOverlayKind[] Order =
        [DeviceOverlayKind.Graph, DeviceOverlayKind.Models, DeviceOverlayKind.Hourly, DeviceOverlayKind.Agents];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ---- process-wide state (all under Gate) -----------------------------

    private static readonly object Gate = new();
    private static readonly HashSet<DeviceOverlayState> Touched = [];

    // One refresh at a time in the whole process: a refresh can hold the
    // agent's read lock for up to 145 s per producer. A trigger during a
    // refresh keeps only the newest request and runs it once afterwards.
    private sealed record PendingRefresh(WeakReference<DeviceOverlayState> Owner, List<DeviceOverlayKey> Keys, bool IsRetry);

    private static bool _refreshInFlight;
    private static PendingRefresh? _pendingRefresh;
    private static int _retryEpoch;

    // ---- per-instance state (under Gate) ---------------------------------

    private readonly IDeviceSource _source;
    private readonly SettingsStore _settings;
    private readonly IDeviceClock _clock;

    private StoredDeviceSelection _stored;
    private IReadOnlyList<RemotePeer> _selectablePeers = [];
    private DeviceOverlaySlot<UsagePayload>? _graph;
    private DeviceOverlaySlot<ModelReport>? _models;
    private DeviceOverlaySlot<HourlyReport>? _hourly;
    private DeviceOverlaySlot<AgentsReport>? _agents;
    private UsageStats? _graphStats;
    private ModelColorMap _modelsColors = new((ModelReport?)null);
    private Dictionary<DeviceOverlayKind, DeviceOverlayKey> _wanted = [];
    private bool _refreshing;
    private bool _queuedRefresh;
    private readonly Dictionary<int, List<DeviceOverlayKey>> _cachedLoadKeys = [];
    private int _nextCachedLoad;
    private int _rootsEpoch;
    private readonly Dictionary<DeviceOverlayKey, DateTimeOffset> _refreshedAt = [];
    private int? _lastRootsGeneration;
    private HashSet<string>? _seenPeers;
    private CancellationTokenSource? _retryCts;
    private List<DeviceOverlayKey> _retryKeys = [];
    private int _peersGeneration;
    private Dictionary<string, string> _learnedNames = [];
    private (DeviceOverlayKey Key, HashSet<string> Selected, UsageStats Value)? _statsMemo;
    private bool _publishes;
    private DeviceOverlaySnapshot? _lastPublished;

    /// <summary>Anything an observer shows changed (slots, selection, refreshing,
    /// retry armed, wanted keys).</summary>
    public event Action? Changed;

    /// <summary>The snapshot the three short-status places read, raised only
    /// when its content changed, and only once this instance has published
    /// (its first result or selection change): a Settings-only instance never
    /// fetches, so it must not overwrite what the dashboard's published.</summary>
    public event Action<DeviceOverlaySnapshot>? SnapshotChanged;

    public DeviceOverlayState(IDeviceSource source, SettingsStore settings, IDeviceClock? clock = null)
    {
        _source = source;
        _settings = settings;
        _clock = clock ?? new SystemDeviceClock();
        // Under the lock: a write queued by another instance is not on disk yet.
        lock (Gate)
        {
            var storedJson = StoredValue(SelectionKey);
            _stored = DecodeSelection(storedJson) ?? new StoredDeviceSelection();
            if (ReadSeen() is { } seen)
            {
                _seenPeers = seen;
            }
            else if (storedJson is null)
            {
                // Nothing chosen before: every device that appears is new.
                _seenPeers = [];
            }
        }
        // else: a selection from before the seen set existed. The first
        // successful list marks its devices seen without checking them.
    }

    // ---- locking and notification ----------------------------------------

    private void Touch() => Touched.Add(this);

    private void Publish()
    {
        _publishes = true;
        Touch();
    }

    /// <summary>Runs <paramref name="body"/> under the lock, then raises the
    /// events of every instance it touched (this one, or the owner of a queued
    /// refresh) outside it.</summary>
    private static T Locked<T>(Func<T> body)
    {
        T result;
        List<(DeviceOverlayState Owner, DeviceOverlaySnapshot? Snapshot, long Seq)> notify = [];
        lock (Gate)
        {
            result = body();
            foreach (var owner in Touched)
            {
                DeviceOverlaySnapshot? snapshot = null;
                if (owner._publishes)
                {
                    var current = owner.BuildSnapshot();
                    if (!current.Equals(owner._lastPublished))
                    {
                        owner._lastPublished = current;
                        snapshot = current;
                        owner._snapshotSeq++;
                    }
                }
                notify.Add((owner, snapshot, owner._snapshotSeq));
            }
            Touched.Clear();
        }
        FlushWrites();
        foreach (var (owner, snapshot, seq) in notify)
        {
            if (owner.Changed is { } changed) { foreach (Action handler in changed.GetInvocationList()) { Raise(handler); } }
            if (snapshot is not null) { owner.Deliver(snapshot, seq); }
        }
        return result;
    }

    /// <summary>One throwing subscriber must not skip the other owners, abort a
    /// refresh's remaining keys or end the poll loop. ponytail: swallowed, this
    /// layer has no log; the type is not recorded.</summary>
    private static void Raise(Action handler)
    {
        try { handler(); } catch (Exception) { }
    }

    /// <summary>Two completions can reach here out of order (the newer snapshot
    /// first). Each snapshot carries the sequence number it got under the lock,
    /// and anything older than the last one delivered is dropped, under a lock
    /// of its own so deliveries cannot interleave.</summary>
    private void Deliver(DeviceOverlaySnapshot snapshot, long seq)
    {
        BeforeSnapshotDeliveryForTesting?.Invoke(seq);
        lock (_deliverGate)
        {
            if (seq <= _lastDeliveredSeq) { return; }
            _lastDeliveredSeq = seq;
            if (SnapshotChanged is { } changed)
            {
                foreach (Action<DeviceOverlaySnapshot> handler in changed.GetInvocationList()) { Raise(() => handler(snapshot)); }
            }
        }
    }

    private readonly object _deliverGate = new();
    private long _snapshotSeq;
    private long _lastDeliveredSeq;

    /// <summary>Test seam: runs after a snapshot got its number and before it is
    /// delivered, so a test can hold one delivery while another overtakes it.</summary>
    internal Action<long>? BeforeSnapshotDeliveryForTesting { get; set; }

    private static void Locked(Action body) => Locked(() => { body(); return 0; });

    private T Read<T>(Func<T> body)
    {
        lock (Gate) { return body(); }
    }

    // ---- state readable by the lens ---------------------------------------

    public StoredDeviceSelection Stored => Read(() => _stored);
    public IReadOnlyList<RemotePeer> SelectablePeers => Read(() => _selectablePeers);
    public DeviceOverlaySlot<UsagePayload>? Graph => Read(() => _graph);
    public DeviceOverlaySlot<ModelReport>? Models => Read(() => _models);
    public DeviceOverlaySlot<HourlyReport>? Hourly => Read(() => _hourly);
    public DeviceOverlaySlot<AgentsReport>? Agents => Read(() => _agents);

    /// <summary>Of the graph overlay's own payload.</summary>
    public UsageStats? GraphStats => Read(() => _graphStats);

    public ModelColorMap ModelsColors => Read(() => _modelsColors);
    public IReadOnlyDictionary<string, string> LearnedNames => Read(() => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(_learnedNames));

    /// <summary>Whether a refresh this model started has not finished.</summary>
    public bool Refreshing => Read(() => _refreshing);

    /// <summary>A refresh queued behind another in the process.</summary>
    public bool QueuedRefresh => Read(() => _queuedRefresh);

    /// <summary>A retry is armed and has not fired.</summary>
    public bool RetryArmed => Read(() => _retryCts is not null);

    /// <summary>The cached loads whose keys are still the ones wanted.</summary>
    public int CachedLoads => Read(CachedLoadsCore);

    /// <summary>A refresh for the lens on screen is pending or running.</summary>
    public bool Fetching => Read(FetchingCore);

    private int CachedLoadsCore() =>
        _cachedLoadKeys.Values.Count(keys => keys.All(IsWanted));

    private bool FetchingCore() => _refreshing || CachedLoadsCore() > 0 || _queuedRefresh;

    private bool IsWanted(DeviceOverlayKey key) => _wanted.TryGetValue(key.Kind, out var w) && w.Equals(key);

    // ---- selection --------------------------------------------------------

    private DeviceSelection? EffectiveCore()
    {
        var known = _selectablePeers.Select(p => p.ProducerStatic).ToHashSet();
        var chosen = _stored.Peers.Where(known.Contains).Order(StringComparer.Ordinal).Take(MaxSelectedPeers).ToList();
        return chosen.Count == 0 ? null : new DeviceSelection(_stored.Local, chosen);
    }

    /// <summary>The selection that reaches the core, or null when only this PC
    /// is chosen (or the peer list is unknown): then no <c>*Devices</c> call is
    /// made.</summary>
    public DeviceSelection? Effective => Read(EffectiveCore);

    public bool IncludesRemote => Read(() => EffectiveCore() is not null);

    /// <summary>Changes whenever the lens has to ask again.</summary>
    public string Signature => Read(() =>
        EffectiveCore() is { } e ? $"{e.Local}|{string.Join(",", e.Peers)}" : "-");

    private int SelectedPeerCountCore()
    {
        var known = _selectablePeers.Select(p => p.ProducerStatic).ToHashSet();
        return _stored.Peers.Count(known.Contains);
    }

    private int SelectedCountCore() => (_stored.Local ? 1 : 0) + SelectedPeerCountCore();

    /// <summary>Sources currently chosen among those that can be chosen.</summary>
    public int SelectedCount => Read(SelectedCountCore);

    /// <summary>Chosen devices that can be chosen (not counting this PC).</summary>
    public int SelectedPeerCount => Read(SelectedPeerCountCore);

    public bool IsSelected(string peer) => Read(() => _stored.Peers.Contains(peer));

    private bool CanToggleCore(string? peer)
    {
        var selected = peer is null ? _stored.Local : _stored.Peers.Contains(peer);
        if (selected) { return SelectedCountCore() > 1; }
        return peer is null || SelectedPeerCountCore() < MaxSelectedPeers;
    }

    /// <summary>The last selected source cannot be unchecked, and no more than
    /// <see cref="MaxSelectedPeers"/> devices can be checked (otherwise
    /// <c>Effective</c> would drop the extra one silently).</summary>
    public bool CanToggleLocal() => Read(() => CanToggleCore(null));

    public bool CanTogglePeer(string peer) => Read(() => CanToggleCore(peer));

    public void ToggleLocal() => Locked(() =>
    {
        if (!CanToggleCore(null)) { return; }
        _stored = new StoredDeviceSelection { Local = !_stored.Local, Peers = _stored.Peers };
        SelectionChanged();
    });

    public void TogglePeer(string id) => Locked(() =>
    {
        if (!CanToggleCore(id)) { return; }
        var peers = _stored.Peers.ToList();
        if (!peers.Remove(id)) { peers.Add(id); }
        _stored = new StoredDeviceSelection { Local = _stored.Local, Peers = peers };
        SelectionChanged();
    });

    private void SelectionChanged()
    {
        PersistSelection();
        if (EffectiveCore() is null) { ClearOverlaysCore(); }
        Publish();
    }

    // ---- snapshot ---------------------------------------------------------

    /// <summary>What the three short-status places read: the rows of the pages
    /// held for the lens on screen, which include the header's graph page (every held page while none is wanted, as
    /// when the flyout is closed), who is selected, and whether a refresh is
    /// running.</summary>
    public DeviceOverlaySnapshot Snapshot => Read(BuildSnapshot);

    private DeviceOverlaySnapshot BuildSnapshot()
    {
        IReadOnlyList<RemoteDevice> Rows<T>(DeviceOverlaySlot<T>? slot, DeviceOverlayKind kind) =>
            slot is null || !(_wanted.Count == 0 || (_wanted.TryGetValue(kind, out var w) && w.Equals(slot.Key)))
                ? []
                : slot.Data?.Devices ?? [];

        var selection = new Dictionary<string, bool>();
        foreach (var peer in _selectablePeers)
        {
            // First wins on a repeated fingerprint, as macOS uniquingKeysWith.
            selection.TryAdd(peer.Fingerprint, _stored.Peers.Contains(peer.ProducerStatic));
        }
        return new DeviceOverlaySnapshot(
            [.. Rows(_graph, DeviceOverlayKind.Graph), .. Rows(_models, DeviceOverlayKind.Models),
             .. Rows(_hourly, DeviceOverlayKind.Hourly), .. Rows(_agents, DeviceOverlayKind.Agents)],
            selection, FetchingCore(), new Dictionary<string, string>(_learnedNames));
    }

    // ---- persistence ------------------------------------------------------

    private static T? Decode<T>(string? json) where T : class
    {
        if (json is null) { return null; }
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>A stored selection that does not decode, or decodes with
    /// <c>"peers": null</c>, is the default selection (null here).</summary>
    private static StoredDeviceSelection? DecodeSelection(string? json) =>
        Decode<StoredDeviceSelection>(json) is { } d && (object?)d.Peers is not null ? d : null;

    // Settings writes are file I/O and raise SettingsStore.Changed synchronously,
    // so they are queued under the lock and performed after it is released (see
    // Locked). Until written an entry stays in the list, and reads see it first,
    // so a read never returns older than the state the lock last published.
    private static readonly object WriteGate = new();
    private static readonly List<(SettingsStore Store, string Key, string Value)> PendingWrites = [];

    private void QueueWrite(string key, string value) => PendingWrites.Add((_settings, key, value));

    private string? StoredValue(string key)
    {
        for (var i = PendingWrites.Count - 1; i >= 0; i--)
        {
            if (PendingWrites[i].Store == _settings && PendingWrites[i].Key == key) { return PendingWrites[i].Value; }
        }
        return _settings.GetString(key);
    }

    [ThreadStatic] private static bool _flushing;

    /// <summary>One writer at a time, in queue order, so two threads cannot
    /// reorder writes to one key; no overlay lock is held. A thread that finds
    /// the writer busy returns: the writer re-checks the queue after it lets go,
    /// so the entry is written. (Waiting instead would deadlock a
    /// SettingsStore.Changed subscriber that calls back into the model from
    /// another thread and waits for it.) A subscriber on the writing thread
    /// itself must not start a nested drain, which would write older entries
    /// after newer ones.</summary>
    private static void FlushWrites()
    {
        if (_flushing) { return; }
        do
        {
            if (!Monitor.TryEnter(WriteGate)) { return; }
            _flushing = true;
            try
            {
                while (true)
                {
                    (SettingsStore Store, string Key, string Value)[] batch;
                    lock (Gate) { batch = [.. PendingWrites]; }
                    if (batch.Length == 0) { break; }
                    foreach (var (store, key, value) in batch)
                    {
                        // A throwing SettingsStore.Changed subscriber must not skip the rest.
                        try { store.SetString(key, value); } catch (Exception) { }
                    }
                    lock (Gate) { PendingWrites.RemoveRange(0, batch.Length); }
                }
            }
            finally
            {
                _flushing = false;
                Monitor.Exit(WriteGate);
            }
        }
        while (WritesPendingNow());
    }

    private static bool WritesPendingNow()
    {
        lock (Gate) { return PendingWrites.Count > 0; }
    }

    private HashSet<string>? ReadSeen() =>
        Decode<List<string>>(StoredValue(SeenPeersKey)) is { } list ? [.. list] : null;

    private void PersistSeen(HashSet<string> seen)
    {
        _seenPeers = seen;
        QueueWrite(SeenPeersKey, JsonSerializer.Serialize(seen.Order(StringComparer.Ordinal), JsonOpts));
    }

    private void PersistSelection() =>
        QueueWrite(SelectionKey, JsonSerializer.Serialize(_stored, JsonOpts));

    /// <summary>Back to this PC alone: every overlay goes, and a response still
    /// in flight finds nothing wanted.</summary>
    private void ClearOverlaysCore()
    {
        _wanted = [];
        _graph = null; _models = null; _hourly = null; _agents = null;
        _graphStats = null;
        _modelsColors = new ModelColorMap((ModelReport?)null);
        _statsMemo = null;
        _refreshedAt.Clear();
        CancelRetryCore();
        Touch();
    }

    // ---- peers ------------------------------------------------------------

    /// <summary>Reads the peer list on the thread pool. Only a successful read
    /// can drop a stored peer that is no longer listed; a failed one leaves the
    /// stored choice alone and hides the menu, so a stopped agent does not
    /// forget the user's selection. Reads can overlap (the poll, the flyout
    /// opening, a link or removal in Settings): each takes the next generation
    /// and only the newest read's result is applied, so a late older list cannot
    /// drop a just-linked device from the selection while the seen set keeps it.
    /// 4g calls this after a link or a removal (macOS: the peersChanged
    /// notification).</summary>
    public void RefreshPeers()
    {
        var generation = Locked(() => ++_peersGeneration);
        _ = Task.Run(() =>
        {
            Exception? failure = null;
            IReadOnlyList<RemotePeer> list = [];
            try { list = _source.RemotePeers(); }
            catch (Exception ex) { failure = ex; }
            PeersArrived(failure is null ? list : null, generation);
        });
    }

    internal int BeginPeersReadForTesting() => Locked(() => ++_peersGeneration);

    internal void DeliverPeersForTesting(IReadOnlyList<RemotePeer>? list, int generation) =>
        PeersArrived(list, generation);

    /// <param name="list">The peers, or null when the read failed.</param>
    private void PeersArrived(IReadOnlyList<RemotePeer>? list, int generation) => Locked(() =>
    {
        if (generation != _peersGeneration) { return; }
        if (list is not null)
        {
            _selectablePeers = [.. list.Where(p => p.State == "active")];
            var listed = list.Select(p => p.Fingerprint).ToHashSet();
            _learnedNames = _learnedNames.Where(kv => listed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var known = _selectablePeers.Select(p => p.ProducerStatic).ToHashSet();
            var kept = _stored.Peers.Where(known.Contains).ToList();
            // Checked devices that are no longer active are forgotten as "seen",
            // so they are checked again when they come back. Read from what is
            // persisted, not from this instance's copy: another instance (the
            // Settings window's) may have unchecked a device since this one was
            // loaded, and that device must stay seen.
            var persisted = DecodeSelection(StoredValue(SelectionKey));
            var dropped = (persisted ?? _stored).Peers.Where(p => !known.Contains(p)).ToList();
            if (ReadSeen() is { } onDisk) { _seenPeers = onDisk; }
            // A device never offered before is checked on arrival, once; the
            // first list after an upgrade only records what already exists.
            var ids = _selectablePeers.Select(p => p.ProducerStatic).ToList();
            if (_seenPeers is { } seen)
            {
                var next = new HashSet<string>(seen);
                next.ExceptWith(dropped);
                // `kept` may already hold a device another instance has just
                // forgotten: never list it twice (the core refuses a selection
                // with a repeated key).
                foreach (var id in ids)
                {
                    if (!next.Contains(id) && !kept.Contains(id) && kept.Count < MaxSelectedPeers) { kept.Add(id); }
                }
                // Every listed device is seen from here on, checked or not: one
                // the 16-device cap kept out is the user's to check by hand.
                next.UnionWith(ids);
                if (!next.SetEquals(seen)) { PersistSeen(next); }
            }
            else
            {
                PersistSeen([.. ids]);
            }
            if (!kept.SequenceEqual(_stored.Peers))
            {
                _stored = new StoredDeviceSelection { Local = kept.Count == 0 || _stored.Local, Peers = kept };
                PersistSelection();
            }
        }
        else
        {
            _selectablePeers = [];
        }
        if (EffectiveCore() is null) { ClearOverlaysCore(); }
        Touch();
    });

    // ---- fetching ---------------------------------------------------------

    private DeviceOverlayKey? KeyCore(DeviceOverlayKind kind, string? year, IReadOnlyList<string>? clients)
    {
        if (EffectiveCore() is not { } selection) { return null; }
        var carriesClients = kind is DeviceOverlayKind.Hourly or DeviceOverlayKind.Agents;
        return new DeviceOverlayKey(
            kind, year ?? "",
            carriesClients && clients is not null
                ? [.. clients.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
                : null,
            selection.Local, selection.Peers);
    }

    private DeviceOverlayKey? CurrentKey(DeviceOverlayKind kind) => kind switch
    {
        DeviceOverlayKind.Graph => _graph?.Key,
        DeviceOverlayKind.Models => _models?.Key,
        DeviceOverlayKind.Hourly => _hourly?.Key,
        _ => _agents?.Key,
    };

    private void SetWanted(Dictionary<DeviceOverlayKind, DeviceOverlayKey> next)
    {
        if (next.Count == _wanted.Count && next.All(p => _wanted.TryGetValue(p.Key, out var w) && w.Equals(p.Value)))
        {
            return;
        }
        _wanted = next;
        Touch();
    }

    /// <summary>True while the graph page is wanted only for the header (the
    /// lens has no graph page of its own): it is then issued after the lens's
    /// own pages, so with one producer token left the lens gets it.</summary>
    private bool _graphForHeaderOnly;

    private List<DeviceOverlayKey> OrderedKeys(Dictionary<DeviceOverlayKind, DeviceOverlayKey> wanted)
    {
        var kinds = Order.Where(wanted.ContainsKey).ToList();
        if (_graphForHeaderOnly && kinds.Remove(DeviceOverlayKind.Graph)) { kinds.Add(DeviceOverlayKind.Graph); }
        return [.. kinds.Select(k => wanted[k])];
    }

    private List<DeviceOverlayKey> WantedKeys() => OrderedKeys(_wanted);

    /// <summary>Declares what the lens on screen needs and starts the loads: for
    /// each page without an overlay for its key, one <c>cached</c> call (shown
    /// at once), then a <c>refresh</c>. With <paramref name="withGraph"/> (the
    /// dashboard header) the graph page is wanted on every lens, Quota included,
    /// after the lens's own pages. Safe to call on every
    /// change of lens, year, client slice or selection.
    /// <paramref name="rootsGeneration"/>: the scan-roots generation the caller
    /// is on. When it changed since the last call, this PC's share of every page
    /// is stale, so the visible keys are asked again whatever the 60 s window
    /// says.</summary>
    public void Ensure(
        AppView view, string? year, IReadOnlyList<string>? clients, int? rootsGeneration = null, bool withGraph = false) =>
        Locked(() =>
        {
            if (rootsGeneration is { } generation)
            {
                if (_lastRootsGeneration is { } last && last != generation)
                {
                    _refreshedAt.Clear();
                    _rootsEpoch++;
                }
                _lastRootsGeneration = generation;
            }
            var next = new Dictionary<DeviceOverlayKind, DeviceOverlayKey>();
            // withGraph: the dashboard header shows the combined totals on every
            // lens, so the graph page is wanted beside the lens's own pages.
            var kinds = DeviceOverlayRequests.KindsFor(view);
            _graphForHeaderOnly = withGraph && !kinds.Contains(DeviceOverlayKind.Graph);
            if (_graphForHeaderOnly) { kinds = [.. kinds, DeviceOverlayKind.Graph]; }
            foreach (var kind in kinds)
            {
                if (KeyCore(kind, year, clients) is { } key) { next[kind] = key; }
            }
            SetWanted(next);
            var keys = OrderedKeys(next);
            if (keys.Count == 0) { return; }
            var missing = keys.Where(k => !k.Equals(CurrentKey(k.Kind))).ToList();
            if (missing.Count == 0)
            {
                ScheduleRefreshCore(NotRecentlyRefreshed(keys));
                return;
            }
            var load = ++_nextCachedLoad;
            _cachedLoadKeys[load] = keys;
            Touch();
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var key in missing) { await RunOnce(key, DeviceFetchMode.Cached).ConfigureAwait(false); }
                    // Only while these are still the keys on screen: a stale
                    // request would replace a newer pending one, which
                    // FinishRefresh then drops, leaving the newest lens without
                    // its refresh.
                    Locked(() =>
                    {
                        if (keys.All(IsWanted)) { ScheduleRefreshCore(NotRecentlyRefreshed(keys)); }
                    });
                }
                finally
                {
                    Locked(() => { _cachedLoadKeys.Remove(load); Touch(); });
                }
            });
        });

    /// <summary>Of <paramref name="keys"/>, those not refreshed within
    /// <see cref="FreshWindow"/>. Only a lens coming on screen filters; the
    /// page it shows is the slot already held.</summary>
    private List<DeviceOverlayKey> NotRecentlyRefreshed(List<DeviceOverlayKey> keys)
    {
        var now = _clock.Now;
        return [.. keys.Where(k => !_refreshedAt.TryGetValue(k, out var at) || now - at >= FreshWindow)];
    }

    /// <summary>The flyout closed: nothing is on screen, so a refresh in flight
    /// stops issuing its remaining pages and a pending one is skipped. The
    /// slots stay. A reopened flyout calls <see cref="Ensure"/> again.</summary>
    public void PopoverClosed() => Locked(() =>
    {
        SetWanted([]);
        _graphForHeaderOnly = false;
        CancelRetryCore();
        Touch();
    });

    /// <summary>The poll's tick: refresh the lens on screen. A tick while a
    /// refresh runs is skipped. A poll never arms a retry: an offline device
    /// would otherwise cost a second refresh after every tick.</summary>
    public void PollOnce() => Locked(() =>
    {
        if (EffectiveCore() is null || _refreshInFlight || _wanted.Count == 0) { return; }
        StartRefreshCore(WantedKeys(), armsRetry: false, isRetry: false);
    });

    public void ManualRefresh() => Locked(() =>
    {
        if (EffectiveCore() is null || _wanted.Count == 0) { return; }
        ScheduleRefreshCore(WantedKeys());
    });

    /// <summary>Started by the flyout, ended with it (cancel the token).</summary>
    public async Task PollLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { await _clock.Delay(RemotePoll, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (cancellation.IsCancellationRequested) { break; }
            PollOnce();
        }
    }

    /// <summary>Test seam: settings writes queued and not yet performed.</summary>
    internal static bool WritesPendingForTesting { get { lock (Gate) { return PendingWrites.Count > 0; } } }

    /// <summary>Test seam: whether a refresh is running anywhere in the process.</summary>
    internal static bool RefreshInFlightForTesting { get { lock (Gate) { return _refreshInFlight; } } }

    /// <summary>Test seam: forget the process-wide guard between cases.</summary>
    internal static void ResetSingleFlightForTesting()
    {
        lock (Gate)
        {
            _refreshInFlight = false;
            _pendingRefresh = null;
            _retryEpoch++; // every retry armed so far finds itself void when it wakes
        }
    }

    /// <summary>Test seam: whether this model has a retry armed and not yet fired.</summary>
    internal bool RetryPendingForTesting => RetryArmed;

    private void ScheduleRefreshCore(List<DeviceOverlayKey> keys, bool isRetry = false)
    {
        if (keys.Count == 0) { return; }
        if (_refreshInFlight)
        {
            // A retry never replaces a pending request of the user's.
            if (isRetry && _pendingRefresh is { IsRetry: false }) { return; }
            if (_pendingRefresh is { } old && old.Owner.TryGetTarget(out var oldOwner)) { oldOwner._queuedRefresh = false; oldOwner.Touch(); }
            _pendingRefresh = new PendingRefresh(new WeakReference<DeviceOverlayState>(this), keys, isRetry);
            _queuedRefresh = true;
            Touch();
            return;
        }
        StartRefreshCore(keys, armsRetry: !isRetry, isRetry);
    }

    /// <summary><paramref name="armsRetry"/>: only a refresh the user asked for
    /// (lens shown, selection, manual) may arm a retry; a poll refresh and a
    /// retry never do.</summary>
    private void StartRefreshCore(List<DeviceOverlayKey> keys, bool armsRetry, bool isRetry)
    {
        _refreshInFlight = true;
        _refreshing = true;
        _queuedRefresh = false;
        Touch();
        _ = Task.Run(async () =>
        {
            var stored = new List<(DeviceOverlayKey Key, bool NoAnswer, bool Busy)>();
            try
            {
                foreach (var key in keys)
                {
                    var outcome = await RunOnce(key, DeviceFetchMode.Refresh).ConfigureAwait(false);
                    if (outcome.Stored) { stored.Add((key, outcome.NoAnswer, outcome.Busy)); }
                }
            }
            catch
            {
                // ponytail: a throw past RunOnce's own handling must still end
                // the flight, or the process-wide guard would stay set for good.
            }
            Locked(() =>
            {
                // A completed refresh of a retry's keys makes that retry moot.
                if (_retryKeys.Any(k => stored.Any(s => s.Key.Equals(k)))) { CancelRetryCore(); }
                // Only what this refresh stored decides; the retry asks only for
                // the pages that came back without new data.
                var again = stored.Where(s => s.NoAnswer).Select(s => s.Key).ToList();
                if (armsRetry && again.Count > 0)
                {
                    ScheduleRetryCore(again);
                }
                else if (!isRetry && stored.Any(s => s.Busy))
                {
                    // A busy producer asked for a pause: armed for a poll's
                    // refresh too, and only once (the retry itself arms none).
                    ScheduleRetryCore([.. stored.Where(s => s.Busy).Select(s => s.Key)]);
                }
                _refreshing = false;
                Touch();
                FinishRefreshCore();
            });
        });
    }

    private void CancelRetryCore()
    {
        _retryCts?.Cancel();
        _retryCts = null;
        _retryKeys = [];
        Touch();
    }

    private void ScheduleRetryCore(List<DeviceOverlayKey> keys)
    {
        CancelRetryCore();
        _retryKeys = keys;
        var epoch = _retryEpoch;
        var cts = new CancellationTokenSource();
        _retryCts = cts;
        Touch();
        // Asked for here, under the lock, so a clock that counts delays sees it
        // registered before the caller can look.
        var delay = _clock.Delay(RetryDelay, cts.Token);
        var weak = new WeakReference<DeviceOverlayState>(this); // as macOS [weak self]
        _ = Task.Run(async () =>
        {
            try { await delay.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!weak.TryGetTarget(out var self)) { return; }
            Locked(() =>
            {
                if (cts.IsCancellationRequested) { return; }
                // Armed no longer, whether or not it still runs: a retry voided
                // by the process-wide reset must not keep the sentence up.
                self._retryCts = null;
                self._retryKeys = [];
                self.Touch();
                if (epoch != _retryEpoch) { return; }
                if (!keys.All(self.IsWanted)) { return; }
                self.ScheduleRefreshCore(keys, isRetry: true);
            });
        });
    }

    private static void FinishRefreshCore()
    {
        _refreshInFlight = false;
        if (_pendingRefresh is not { } next) { return; }
        _pendingRefresh = null;
        // Weak, as macOS `weak var owner`: a model that went away has nothing to run.
        if (!next.Owner.TryGetTarget(out var owner)) { return; }
        owner._queuedRefresh = false;
        owner.Touch();
        if (next.Keys.All(owner.IsWanted))
        {
            owner.StartRefreshCore(next.Keys, armsRetry: !next.IsRetry, isRetry: next.IsRetry);
        }
    }

    private readonly record struct RunOutcome(bool Stored, bool NoAnswer, bool Busy);

    private readonly record struct Fetched(
        DeviceAwarePayload<UsagePayload>? Graph,
        DeviceAwarePayload<ModelReport>? Models,
        DeviceAwarePayload<HourlyReport>? Hourly,
        DeviceAwarePayload<AgentsReport>? Agents)
    {
        public IReadOnlyList<RemoteDevice> Devices =>
            Graph?.Devices ?? Models?.Devices ?? Hourly?.Devices ?? Agents?.Devices ?? [];
    }

    private async Task<RunOutcome> RunOnce(DeviceOverlayKey key, DeviceFetchMode mode)
    {
        var epoch = Locked(() => IsWanted(key) ? _rootsEpoch : (int?)null);
        if (epoch is null) { return default; }
        // A device call can block for a fetch deadline: the thread pool, with
        // no lock held. A failure of any kind (a core error, or
        // timezoneUnavailable from the wrapper) is a failed fetch, never
        // thrown into the caller.
        var fetched = await Task.Run(() => FetchOrNull(key, mode)).ConfigureAwait(false);
        return Locked(() =>
        {
            if (!IsWanted(key)) { return default; }
            StoreCore(fetched, key, mode);
            if (fetched is not { } value) { return default; }
            // Only a refresh that brought every selected device back fresh counts
            // against the request budget; a failed or busy one may be asked again.
            if (mode == DeviceFetchMode.Refresh && epoch == _rootsEpoch
                && value.Devices.All(d => d.State is "fresh" or "local"))
            {
                _refreshedAt[key] = _clock.Now;
            }
            return new RunOutcome(
                Stored: true,
                NoAnswer: value.Devices.Any(d => d.State is "lastGood" or "stale" or "unavailable"),
                Busy: value.Devices.Any(d => d.Reason == "busy"));
        });
    }

    private Fetched? FetchOrNull(DeviceOverlayKey key, DeviceFetchMode mode)
    {
        try
        {
            var (year, selection) = (key.YearArgument, key.Selection);
            return key.Kind switch
            {
                DeviceOverlayKind.Graph => new Fetched(_source.GraphDevices(year, selection, mode), null, null, null),
                DeviceOverlayKind.Models => new Fetched(null, _source.ModelReportDevices(year, selection, mode), null, null),
                DeviceOverlayKind.Hourly => new Fetched(
                    null, null, _source.HourlyReportDevices(year, key.Clients, selection, mode), null),
                _ => new Fetched(null, null, null, _source.AgentsReportDevices(year, key.Clients, selection, mode)),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A row has a real name when the core's label is not the address it
    /// falls back to (rows without an address, from a core that predates it, say
    /// nothing).</summary>
    private void LearnNames(IReadOnlyList<RemoteDevice> rows)
    {
        foreach (var row in rows.Where(r => r.Id is not ("local" or "peers")))
        {
            if (row.Label is { } label && row.Address is { } address && label != address)
            {
                _learnedNames[row.Id] = label;
            }
            else if (row.Address is not null)
            {
                _learnedNames.Remove(row.Id);
            }
        }
    }

    /// <summary>A cached miss changes nothing (a refresh follows); a refresh miss
    /// keeps the last good answer for this key and says so.</summary>
    private void StoreCore(Fetched? result, DeviceOverlayKey key, DeviceFetchMode mode)
    {
        Publish();
        if (result is not { } value)
        {
            if (mode != DeviceFetchMode.Refresh) { return; }
            switch (key.Kind)
            {
                case DeviceOverlayKind.Graph:
                    _graph = new(key, _graph?.Key.Equals(key) == true ? _graph.Data : null, true); break;
                case DeviceOverlayKind.Models:
                    _models = new(key, _models?.Key.Equals(key) == true ? _models.Data : null, true); break;
                case DeviceOverlayKind.Hourly:
                    _hourly = new(key, _hourly?.Key.Equals(key) == true ? _hourly.Data : null, true); break;
                default:
                    _agents = new(key, _agents?.Key.Equals(key) == true ? _agents.Data : null, true); break;
            }
            return;
        }
        // A cached answer never replaces a page the slot already holds for this
        // key: a refresh may have stored one while the cached call ran. A slot a
        // failed refresh left without a page takes the cached page and stays
        // marked failed.
        var held = key.Equals(CurrentKey(key.Kind));
        var hasPage = key.Kind switch
        {
            DeviceOverlayKind.Graph => _graph?.Data is not null,
            DeviceOverlayKind.Models => _models?.Data is not null,
            DeviceOverlayKind.Hourly => _hourly?.Data is not null,
            _ => _agents?.Data is not null,
        };
        if (mode != DeviceFetchMode.Refresh && held && hasPage) { return; }
        var failed = mode == DeviceFetchMode.Cached && held;
        LearnNames(value.Devices);
        switch (key.Kind)
        {
            case DeviceOverlayKind.Graph:
                var graph = value.Graph!;
                _graph = new(key, graph, failed);
                _graphStats = new UsageStats(graph.Payload, graph.Payload.Summary.Clients.ToHashSet(StringComparer.Ordinal));
                _statsMemo = null;
                break;
            case DeviceOverlayKind.Models:
                _models = new(key, value.Models, failed);
                _modelsColors = new ModelColorMap(value.Models!.Payload);
                break;
            case DeviceOverlayKind.Hourly:
                _hourly = new(key, value.Hourly, failed);
                break;
            default:
                _agents = new(key, value.Agents, failed);
                break;
        }
    }

    // ---- what a lens shows ------------------------------------------------

    /// <summary>The one place a linked device's name is chosen: the newest name a
    /// page reported, else the peer list's, else its address.</summary>
    public string DisplayName(RemotePeer peer) =>
        Read(() => DeviceOverlayCopy.DisplayName(peer, _learnedNames));

    /// <summary>The same for a <c>devices</c> row.</summary>
    public string DisplayName(RemoteDevice device) =>
        Read(() => _learnedNames.TryGetValue(device.Id, out var name) ? name : DeviceOverlayCopy.Name(device));

    /// <summary>The tab's client set for the overlay page of <paramref name="kind"/>:
    /// the single client's slice, else the clients that page itself holds minus
    /// the hidden ones. Deliberately not intersected with this PC's present
    /// clients: a client only a remote device used belongs on Overview.</summary>
    public IReadOnlyList<string> DisplayClients(
        DeviceOverlayKind kind, string? singleClient, string hiddenRaw, string orderRaw) =>
        Read(() => DisplayClientsCore(kind, singleClient, hiddenRaw, orderRaw));

    private IReadOnlyList<string> DisplayClientsCore(
        DeviceOverlayKind kind, string? singleClient, string hiddenRaw, string orderRaw)
    {
        if (singleClient is not null) { return ClientRegistry.TabSlice(singleClient); }
        IReadOnlyList<string> present = kind switch
        {
            DeviceOverlayKind.Graph => _graphStats?.PresentClients ?? [],
            DeviceOverlayKind.Models =>
                [.. (_models?.Data?.Payload.Entries.Select(e => e.Client) ?? []).Distinct().Order(StringComparer.Ordinal)],
            DeviceOverlayKind.Hourly =>
                [.. (_hourly?.Data?.Payload.Entries.SelectMany(e => e.Clients) ?? []).Distinct().Order(StringComparer.Ordinal)],
            _ =>
                [.. (_agents?.Data?.Payload.Entries.SelectMany(e => e.Clients) ?? []).Distinct().Order(StringComparer.Ordinal)],
        };
        return ClientRegistry.DisplayClients(present, hiddenRaw, orderRaw);
    }

    /// <summary>What a lens may show from <paramref name="slot"/>: the page only
    /// while the slot is for the key the lens wants now. After a year or client
    /// change the previous key's page is not shown while the new one loads.</summary>
    public (DeviceAwarePayload<T>? Page, bool Failed) Visible<T>(
        DeviceOverlaySlot<T>? slot, DeviceOverlayKind kind, string? year, IReadOnlyList<string>? clients) =>
        Read(() => VisibleCore(slot, kind, year, clients));

    private (DeviceAwarePayload<T>? Page, bool Failed) VisibleCore<T>(
        DeviceOverlaySlot<T>? slot, DeviceOverlayKind kind, string? year, IReadOnlyList<string>? clients) =>
        slot is not null && slot.Key.Equals(KeyCore(kind, year, clients)) ? (slot.Data, slot.Failed) : (null, false);

    private IReadOnlyList<RemoteDevice> VisibleRows(DeviceOverlayKind kind, string? year, IReadOnlyList<string>? clients) =>
        kind switch
        {
            DeviceOverlayKind.Graph => VisibleCore(_graph, kind, year, clients).Page?.Devices ?? [],
            DeviceOverlayKind.Models => VisibleCore(_models, kind, year, clients).Page?.Devices ?? [],
            DeviceOverlayKind.Hourly => VisibleCore(_hourly, kind, year, clients).Page?.Devices ?? [],
            _ => VisibleCore(_agents, kind, year, clients).Page?.Devices ?? [],
        };

    /// <summary>Whether a page the lens on screen shows reports
    /// <c>RemoteRangeLimited</c>.</summary>
    public bool RangeLimited(AppView view, string? year, IReadOnlyList<string>? clients) => Read(() =>
        DeviceOverlayRequests.KindsFor(view).Any(kind => kind switch
        {
            DeviceOverlayKind.Graph => VisibleCore(_graph, kind, year, clients).Page?.RemoteRangeLimited == true,
            DeviceOverlayKind.Models => VisibleCore(_models, kind, year, clients).Page?.RemoteRangeLimited == true,
            DeviceOverlayKind.Hourly => VisibleCore(_hourly, kind, year, clients).Page?.RemoteRangeLimited == true,
            _ => VisibleCore(_agents, kind, year, clients).Page?.RemoteRangeLimited == true,
        }));

    /// <summary>The status line's sentences for the lens on screen: one per
    /// selected device that is not fresh, from the pages the lens shows (see
    /// <see cref="DeviceOverlayCopy.StatusLines"/>). Empty when every device is
    /// fresh.</summary>
    public IReadOnlyList<string> StatusLines(AppView view, string? year, IReadOnlyList<string>? clients) => Read(() =>
    {
        var kinds = DeviceOverlayRequests.KindsFor(view);
        if (EffectiveCore() is not { } selection || kinds.Count == 0) { return (IReadOnlyList<string>)[]; }
        var rows = kinds.SelectMany(kind => VisibleRows(kind, year, clients)).ToList();
        var peers = selection.Peers
            .Select(id => _selectablePeers.FirstOrDefault(p => p.ProducerStatic == id))
            .OfType<RemotePeer>()
            .Select(p => (Id: p.Fingerprint, Name: DeviceOverlayCopy.DisplayName(p, _learnedNames)))
            .ToList();
        return DeviceOverlayCopy.StatusLines(rows, peers, FetchingCore(), _retryCts is not null, _clock.Now, _learnedNames);
    });

    /// <summary>What Overview shows while devices other than this PC are
    /// selected: the overlay's own graph (with its own stats for the clients on
    /// screen) and models pages, or null for a page that has not arrived (the
    /// cards then show a loading line; this PC's own data is never the
    /// stand-in). <paramref name="localColors"/> only shades models when the
    /// models page is missing, as the Stats overlay does.</summary>
    public OverviewMergeState? OverviewMergeFor(
        string? year, string? singleClient, string hiddenRaw, string orderRaw, ModelColorMap localColors) =>
        Read(() =>
        {
            if (EffectiveCore() is null) { return null; }
            var graphPage = VisibleCore(_graph, DeviceOverlayKind.Graph, year, null);
            var modelsPage = VisibleCore(_models, DeviceOverlayKind.Models, year, null);
            var ids = DisplayClientsCore(DeviceOverlayKind.Graph, singleClient, hiddenRaw, orderRaw);
            var stats = StatsCore(ids.ToHashSet(StringComparer.Ordinal));
            return new OverviewMergeState(
                DeviceCount: SelectedCountCore(),
                Graph: graphPage.Page is { } page && stats is not null ? (page.Payload, stats) : null,
                Models: modelsPage.Page?.Payload,
                Colors: modelsPage.Page is null ? localColors : _modelsColors,
                ClientIds: ids,
                ModelClientIds: DisplayClientsCore(DeviceOverlayKind.Models, singleClient, hiddenRaw, orderRaw),
                GraphFailed: graphPage.Failed && graphPage.Page is null,
                ModelsFailed: modelsPage.Failed && modelsPage.Page is null,
                KeptLast: (graphPage.Failed && graphPage.Page is not null)
                    || (modelsPage.Failed && modelsPage.Page is not null),
                Saturated: graphPage.Page?.Saturated == true || modelsPage.Page?.Saturated == true);
        });

    /// <summary>What the Stats lens' model sections get: the models overlay's
    /// report for the current key, or nothing and a fixed line while it has not
    /// arrived. This PC's own model report is never the fallback.</summary>
    public (ModelReport? Report, string? Note) StatsModels(string? year) => Read(() =>
    {
        if (_models is { Data: { } page } slot && slot.Key.Equals(KeyCore(DeviceOverlayKind.Models, year, null)))
        {
            return ((ModelReport?)page.Payload, (string?)null);
        }
        return ((ModelReport?)null, (string?)DeviceOverlayCopy.ModelsHidden.Localized());
    });

    /// <summary><see cref="UsageStats"/> for a client slice of the graph overlay
    /// (its own memo, never the local one's).</summary>
    public UsageStats? StatsFor(IReadOnlySet<string> selected) => Read(() => StatsCore(selected));

    private UsageStats? StatsCore(IReadOnlySet<string> selected)
    {
        if (_graph is not { Data: { } page } slot || _graphStats is not { } full) { return null; }
        if (selected.SetEquals(full.PresentClients)) { return full; }
        if (_statsMemo is { } memo && memo.Key.Equals(slot.Key) && memo.Selected.SetEquals(selected)) { return memo.Value; }
        var computed = new UsageStats(page.Payload, selected);
        _statsMemo = (slot.Key, [.. selected], computed);
        return computed;
    }
}
