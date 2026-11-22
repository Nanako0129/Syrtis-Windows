using System.Text.Json;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using C = TokenBar.Core.DeviceOverlayCopy;
using static TokenBar.Core.Tests.DeviceOverlayTestSupport;

namespace TokenBar.Core.Tests;

/// <summary>The stateful device overlay against a fake source and a clock the
/// test moves, ported from the macOS self-tests (SelfTest.swift, Syrtis
/// f8a1a0ae; each test cites its lines). The macOS tests pump a run loop and
/// shorten the retry delay and the freshness window; here time is
/// <see cref="FakeDeviceClock"/> and the real delays (65 s retry, 60 s window,
/// 300 s poll) are what the tests assert. The source blocks on a thread-pool
/// thread, so "wait" means polling a condition for up to 5 s (a few ms in
/// practice); "nothing happens" is a short quiet period after the state that
/// would start it has been reached.</summary>
public class DeviceOverlayStateTests : IDisposable
{
    private const string PeerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string PeerB = new('b', 64);
    private static readonly string PeerC = new('c', 64);
    private const string Name = "10.0.0.2:47000";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));
    private readonly List<DeviceOverlayState> _made = [];
    private readonly List<FakeDeviceSource> _sources = [];
    private int _stores;

    public DeviceOverlayStateTests()
    {
        Load("en");
        DeviceOverlayState.ResetSingleFlightForTesting();
    }

    public void Dispose()
    {
        foreach (var o in _made) { o.PopoverClosed(); }
        foreach (var s in _sources)
        {
            s.Set(st => { st.HoldRefresh = false; st.HoldCached = false; });
            s.Release(50);
            s.ReleaseCached(50);
        }
        DeviceOverlayState.ResetSingleFlightForTesting();
        Load("en");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- helpers -----------------------------------------------------------

    private static RemotePeer Peer(string id, string state = "active", string? label = null) =>
        new(ProducerStatic: id, Fingerprint: id[..16], Host: "10.0.0.2", Port: 47000,
            CreatedAt: 1, ExpiresAt: 4102444800000, State: state, NodeLabel: label);

    private static bool Pump(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) { Thread.Sleep(2); }
        return condition();
    }

    /// <summary>The state a "nothing happens" check waits out: long enough for a
    /// call that was going to start to have started.</summary>
    private static void Quiet() => Thread.Sleep(40);

    private static bool Settled() => Pump(() => !DeviceOverlayState.RefreshInFlightForTesting);

    /// <summary>No refresh running or pending and no cached load out.</summary>
    private static bool Idle(DeviceOverlayState overlay) =>
        Pump(() => !overlay.Fetching && !DeviceOverlayState.RefreshInFlightForTesting);

    private SettingsStore NewStore(bool seeded = true)
    {
        var store = new SettingsStore(Path.Combine(_dir, $"settings-{_stores++}.json"));
        if (seeded)
        {
            // The devices the cases list have already been offered, so a listed
            // device is not checked on arrival (DR-2a R4, :23293-23297).
            List<string> known = [PeerA, PeerB, PeerC, .. Enumerable.Range(1, 17).Select(i => i.ToString("x64"))];
            store.SetString(DeviceOverlayState.SeenPeersKey, JsonSerializer.Serialize(known));
        }
        return store;
    }

    private FakeDeviceSource NewSource(params RemotePeer[] peers)
    {
        var source = new FakeDeviceSource();
        source.Set(s => s.Peers = peers);
        _sources.Add(source);
        return source;
    }

    private DeviceOverlayState NewOverlay(FakeDeviceSource source, SettingsStore store, FakeDeviceClock? clock = null)
    {
        var overlay = new DeviceOverlayState(source, store, clock ?? new FakeDeviceClock());
        _made.Add(overlay);
        return overlay;
    }

    private static void ReadPeers(DeviceOverlayState overlay, int expectedSelectable = 1)
    {
        overlay.RefreshPeers();
        Assert.True(Pump(() => overlay.SelectablePeers.Count == expectedSelectable));
    }

    /// <summary>A model with device A listed and chosen, ready for a lens.</summary>
    private (DeviceOverlayState Overlay, FakeDeviceSource Source, FakeDeviceClock Clock, SettingsStore Store) Chosen(
        Action<FakeDeviceSource.State>? configure = null, params RemotePeer[] peers)
    {
        var source = NewSource(peers.Length == 0 ? [Peer(PeerA)] : peers);
        if (configure is not null) { source.Set(configure); }
        var clock = new FakeDeviceClock();
        var store = NewStore();
        var overlay = NewOverlay(source, store, clock);
        ReadPeers(overlay);
        overlay.TogglePeer(PeerA);
        DeviceOverlayState.ResetSingleFlightForTesting();
        return (overlay, source, clock, store);
    }

    private static StoredDeviceSelection? Persisted(SettingsStore store) =>
        store.GetString(DeviceOverlayState.SelectionKey) is { } json
            ? JsonSerializer.Deserialize<StoredDeviceSelection>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;

    private static HashSet<string>? Seen(SettingsStore store) =>
        store.GetString(DeviceOverlayState.SeenPeersKey) is { } json
            ? [.. JsonSerializer.Deserialize<List<string>>(json)!]
            : null;

    private static void SameSet(IEnumerable<string> expected, HashSet<string>? actual)
    {
        Assert.NotNull(actual);
        Assert.True(actual!.SetEquals(expected));
    }

    private static void SameList(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected, actual);

    // ---- SA3D (1), (2), (9), (4), (5), (10) ---------------------------------

    [Fact] // :23352-23374 (1): this PC alone makes no device call on any lens, poll or refresh
    public void OnlyThisPcMakesNoDeviceCallWhateverTheLens()
    {
        var source = NewSource(Peer(PeerA));
        var overlay = NewOverlay(source, NewStore());
        ReadPeers(overlay); // "the peer list is read and one active device can be chosen"
        foreach (var view in new[] { AppView.Stats, AppView.Daily, AppView.Models, AppView.Hourly, AppView.Agents })
        {
            overlay.Ensure(view, null, null);
        }
        overlay.PollOnce();
        overlay.ManualRefresh();
        Quiet();
        Assert.Empty(source.Calls);
        Assert.False(overlay.IncludesRemote);
        Assert.Null(overlay.Graph);
    }

    [Fact] // :23379-23380 (2): choosing a device keeps this PC and is saved
    public void ChoosingADeviceKeepsThisPcAndIsSaved()
    {
        var (overlay, _, _, store) = Chosen();
        Assert.Equal(new StoredDeviceSelection { Local = true, Peers = [PeerA] }, overlay.Stored);
        Assert.Equal(overlay.Stored, Persisted(store));
    }

    [Fact] // :23381-23393 (2): cached before refresh, carrying the selection, year and sorted clients
    public void TheHourlyLensMakesACachedAndARefreshCallWithTheSelectionYearAndClients()
    {
        var (overlay, source, _, _) = Chosen();
        overlay.Ensure(AppView.Hourly, "2025", ["codex", "claude"]);
        Assert.True(Pump(() => source.Calls.Count == 2) && Settled());
        Assert.Equal<FakeDeviceSource.Call>(
            [new FakeDeviceSource.Call("hourly", DeviceFetchMode.Cached, "2025", "claude,codex", true, PeerA),
             new FakeDeviceSource.Call("hourly", DeviceFetchMode.Refresh, "2025", "claude,codex", true, PeerA)],
            source.Calls);
    }

    [Fact] // :23394-23399 (2): Stats asks the graph and models pages, each cached then refreshed, no clients
    public void StatsAsksForTheGraphAndModelsPagesEachCachedThenRefreshedWithoutClients()
    {
        var (overlay, source, _, _) = Chosen();
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.Calls.Count == 4) && Settled());
        SameList(["graph-cached", "models-cached", "graph-refresh", "models-refresh"], source.Calls.Select(c => c.Tag));
        Assert.All(source.Calls, c => { Assert.Null(c.Year); Assert.Null(c.Clients); });
    }

    [Fact] // :23400-23403 (2): a page that already has an overlay for its key is only refreshed
    public void APageThatAlreadyHasAnOverlayForItsKeyIsOnlyRefreshed()
    {
        var (overlay, source, clock, _) = Chosen();
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.Calls.Count == 4) && Settled());
        clock.Advance(DeviceOverlayState.FreshWindow); // macOS runs this with the 60 s window set to 0
        source.Set(s => s.Calls = []);
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.Calls.Count == 2) && Settled());
        Assert.All(source.Calls, c => Assert.Equal(DeviceFetchMode.Refresh, c.Mode));
    }

    [Fact] // :23410-23417 (9): a failed refresh keeps the last overlay and marks it failed
    public void AFailedRefreshKeepsThePreviousOverlayForTheKeyAndMarksItFailed()
    {
        var (overlay, source, clock, _) = Chosen();
        overlay.Ensure(AppView.Hourly, "2025", ["codex", "claude"]);
        Assert.True(Pump(() => overlay.Hourly?.Data is not null) && Idle(overlay));
        var keptKey = overlay.Hourly!.Key;
        clock.Advance(DeviceOverlayState.FreshWindow);
        source.Set(s => s.FailRefresh = true);
        overlay.Ensure(AppView.Hourly, "2025", ["codex", "claude"]);
        Assert.True(Pump(() => overlay.Hourly?.Failed == true) && Idle(overlay));
        Assert.NotNull(overlay.Hourly!.Data);
        Assert.Equal(keptKey, overlay.Hourly.Key);
    }

    [Fact] // :23410-23417 (9): a failure with no earlier answer leaves no data and a failed slot
    public void AFailureWithNoEarlierAnswerLeavesNoDataAndAFailedSlot()
    {
        var (overlay, source, _, _) = Chosen();
        source.Set(s => s.FailAll = true);
        overlay.Ensure(AppView.Hourly, "1990", null);
        Assert.True(Pump(() => overlay.Hourly?.Key.Year == "1990" && overlay.Hourly.Failed) && Idle(overlay));
        Assert.Null(overlay.Hourly!.Data);
    }

    [Fact] // :23423-23442 (4): a response for a key no longer wanted is dropped
    public void AResponseForAKeyThatIsNoLongerWantedIsDropped()
    {
        var (overlay, source, _, _) = Chosen();
        source.Set(s => s.HoldRefresh = true);
        overlay.Ensure(AppView.Hourly, "2025", null);
        Assert.True(Pump(() => source.RefreshStarted == 1)); // the first refresh is under way
        overlay.Ensure(AppView.Hourly, "2024", null);
        // The 2024 cached page lands first; then the 2025 refresh ends while the
        // 2024 refresh (held again) has not answered: the slot must keep the 2024
        // page, not take the 2025 refresh that has just come back.
        Assert.True(Pump(() => overlay.Hourly?.Key.Year == "2024"));
        source.Release();
        Assert.True(Pump(() => source.RefreshStarted == 2)); // the newer key is refreshed once the old one ends
        Quiet();
        Assert.Equal("2024", overlay.Hourly!.Key.Year);
        SameList(["cached-2024"], overlay.Hourly.Data!.Payload.Entries[0].Models);
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Settled() && Pump(() => overlay.Hourly!.Data!.Payload.Entries[0].Models[0] == "refresh-2024"));
    }

    [Fact] // :23449-23455 (5): back to this PC only, every overlay is cleared and saved
    public void ChoosingThisPcAloneClearsAllFourOverlays()
    {
        var (overlay, source, clock, store) = Chosen();
        foreach (var view in new[] { AppView.Stats, AppView.Hourly, AppView.Agents })
        {
            clock.Advance(DeviceOverlayState.FreshWindow);
            overlay.Ensure(view, null, null);
            Assert.True(Idle(overlay));
        }
        Assert.NotNull(overlay.Graph);
        Assert.NotNull(overlay.Models);
        Assert.NotNull(overlay.Hourly);
        Assert.NotNull(overlay.Agents);
        overlay.TogglePeer(PeerA);
        Assert.False(overlay.IncludesRemote);
        Assert.Null(overlay.Graph);
        Assert.Null(overlay.Models);
        Assert.Null(overlay.Hourly);
        Assert.Null(overlay.Agents);
        Assert.Equal(new StoredDeviceSelection { Local = true, Peers = [] }, Persisted(store));
    }

    [Fact] // :23461-23484 (10): single flight across models, newest request runs once afterwards
    public void SingleFlightStartsNoSecondRefreshAndTheNewestRequestRunsOnceAfterwards()
    {
        var (overlay, source, _, store) = Chosen();
        var second = NewOverlay(source, store, new FakeDeviceClock());
        second.RefreshPeers();
        Assert.True(Pump(() => second.IncludesRemote));
        source.Set(s => { s.Calls = []; s.RefreshStarted = 0; s.HoldRefresh = true; });
        DeviceOverlayState.ResetSingleFlightForTesting();
        overlay.Ensure(AppView.Agents, "2022", null);
        Assert.True(Pump(() => source.RefreshStarted == 1)); // one refresh is held in flight
        foreach (var year in new[] { "2021", "2020" })
        {
            overlay.Ensure(AppView.Agents, year, null);
            Assert.True(Pump(() => overlay.CachedLoads == 0));
        }
        overlay.PollOnce();
        overlay.ManualRefresh();
        second.Ensure(AppView.Agents, "2019", null);
        Assert.True(Pump(() => second.CachedLoads == 0));
        second.PollOnce();
        Quiet();
        Assert.Equal(1, source.RefreshStarted); // triggers during a refresh start no second refresh
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled()); // the newest request ran
        Quiet();
        Assert.Equal(2, source.RefreshStarted); // one in flight and one catch-up
        SameList(["2022", "2019"], source.Refreshes.Select(c => c.Year!));
        Assert.Equal("2019", second.Agents!.Key.Year);
        Assert.Equal("refresh-2019", second.Agents.Data!.Payload.Entries[0].Agent);
    }

    // ---- SA3D (retry) -------------------------------------------------------

    [Fact] // :23499-23527 (retry): one delayed retry after a refresh left a device without data
    public void AnUnavailableDeviceGetsExactlyOneMoreRefreshAfterTheDelay()
    {
        var (overlay, source, clock, _) = Chosen(s => s.RemoteState = "unavailable");
        source.Set(s => { s.Calls = []; s.RefreshStarted = 0; });
        overlay.Ensure(AppView.Hourly, "2013", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled());
        Assert.True(Pump(() => overlay.RetryPendingForTesting && clock.PendingCount == 1)); // a retry is armed
        Assert.Equal<TimeSpan>([DeviceOverlayState.RetryDelay], clock.Requested); // 65 s: a producer's refill plus the margin
        clock.Advance(DeviceOverlayState.RetryDelay - TimeSpan.FromSeconds(1));
        Quiet();
        Assert.Equal(1, source.RefreshStarted); // not before its delay
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled()); // exactly one more refresh
        Quiet();
        var refreshes = source.Refreshes;
        Assert.Equal(2, source.RefreshStarted);
        Assert.Equal(2, refreshes.Count);
        Assert.Equal(refreshes[0], refreshes[1]); // the same keys, and its own unavailable result starts no third
    }

    [Fact] // :23528-23538 (retry): a manual refresh runs; closing the flyout before the delay cancels the retry
    public void ClosingTheFlyoutBeforeTheDelayCancelsTheRetry()
    {
        var (overlay, source, clock, _) = Chosen(s => s.RemoteState = "unavailable");
        overlay.Ensure(AppView.Hourly, "2013", null);
        Assert.True(Idle(overlay));
        source.Set(s => s.RefreshStarted = 0);
        overlay.ManualRefresh();
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled()); // a manual refresh runs
        overlay.PopoverClosed();
        clock.Advance(TimeSpan.FromMinutes(5));
        Quiet();
        Assert.Equal(1, source.RefreshStarted);
    }

    [Theory] // :23539-23547 (retry): fresh and refused devices are not retried
    [InlineData("fresh")]
    [InlineData("refused")]
    public void AFreshOrRefusedDeviceIsNotRetried(string state)
    {
        var (overlay, source, clock, _) = Chosen(s => s.RemoteState = state);
        overlay.Ensure(AppView.Hourly, "2013", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled());
        clock.Advance(TimeSpan.FromMinutes(5));
        Quiet();
        Assert.Equal(1, source.RefreshStarted);
    }

    [Fact] // :23549-23560 (retry 1): a fresh refresh of the same key cancels the pending retry
    public void AFreshRefreshOfTheSameKeyCancelsThePendingRetry()
    {
        var (overlay, source, clock, _) = Chosen(s => s.RemoteState = "unavailable");
        overlay.Ensure(AppView.Hourly, "2013", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled());
        Assert.True(Pump(() => overlay.RetryPendingForTesting));
        source.Set(s => s.RemoteState = "fresh");
        overlay.ManualRefresh();
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        Assert.True(Pump(() => !overlay.RetryPendingForTesting));
        clock.Advance(TimeSpan.FromMinutes(5));
        Quiet();
        Assert.Equal(2, source.RefreshStarted); // and no call is made when the delay passes
    }

    [Fact] // :23562-23575 (retry 2): graph fresh and models unavailable, the retry asks for models only
    public void TheRetryAsksOnlyForThePageThatCameBackWithoutNewData()
    {
        var (overlay, source, clock, _) = Chosen(s => { s.RemoteState = "fresh"; s.RemoteStateByKind["models"] = "unavailable"; });
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        Assert.True(Pump(() => overlay.RetryPendingForTesting && clock.PendingCount == 1));
        clock.Advance(DeviceOverlayState.RetryDelay);
        Assert.True(Pump(() => source.RefreshStarted == 3) && Settled());
        SameList(["graph", "models", "models"], source.Refreshes.Select(c => c.Kind));
    }

    [Fact] // :23577-23590 (retry 3): a poll never arms a retry
    public void APollRefreshThatLeavesADeviceUnavailableArmsNoRetry()
    {
        var (overlay, source, clock, _) = Chosen(s => s.RemoteState = "fresh");
        overlay.Ensure(AppView.Hourly, "2012", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled());
        source.Set(s => s.RemoteState = "unavailable");
        overlay.PollOnce();
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        Assert.False(overlay.RetryPendingForTesting);
        clock.Advance(TimeSpan.FromMinutes(5));
        Quiet();
        Assert.Equal(2, source.RefreshStarted);
    }

    [Fact] // :23592-23607 (retry 4): a refresh whose result was dropped decides nothing
    public void ARefreshWhoseResultWasDroppedArmsNoRetry()
    {
        var (overlay, source, clock, _) = Chosen(s => { s.RemoteState = "unavailable"; s.HoldRefresh = true; });
        overlay.Ensure(AppView.Hourly, "2011", null);
        Assert.True(Pump(() => source.RefreshStarted == 1 && overlay.Hourly?.Data is not null)); // the cached page is in the slot
        overlay.PopoverClosed();
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Settled());
        Assert.False(overlay.RetryPendingForTesting);
        clock.Advance(TimeSpan.FromMinutes(5));
        Quiet();
        Assert.Equal(1, source.RefreshStarted);
    }

    [Fact] // :23609-23638 (retry 5): a retry that fires during another refresh does not replace a user's pending request
    public void ARetryThatFiresDuringAnotherRefreshDoesNotReplaceAUsersPendingRequest()
    {
        var source = NewSource(Peer(PeerA));
        source.Set(s => s.RemoteState = "unavailable");
        var clock = new FakeDeviceClock();
        var first = NewOverlay(source, NewStore(), clock);
        ReadPeers(first);
        first.TogglePeer(PeerA);
        DeviceOverlayState.ResetSingleFlightForTesting();
        first.Ensure(AppView.Hourly, "2013", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Settled());
        Assert.True(Pump(() => first.RetryPendingForTesting && clock.PendingCount == 1)); // the first model has a retry armed
        var user = NewOverlay(source, NewStore(), new FakeDeviceClock());
        ReadPeers(user);
        user.TogglePeer(PeerA);
        source.Set(s => { s.RefreshStarted = 0; s.HoldRefresh = true; });
        user.Ensure(AppView.Agents, "2014", null);
        Assert.True(Pump(() => source.RefreshStarted == 1)); // another refresh is held in flight
        user.ManualRefresh();
        clock.Advance(DeviceOverlayState.RetryDelay); // the first model's retry fires here and must not replace the manual request
        Assert.True(Pump(() => !first.RetryPendingForTesting));
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        // It is the user's request (agents), run as a non-retry that arms its own retry.
        SameList(["hourly", "agents", "agents"], source.Refreshes.Select(c => c.Kind));
        Assert.True(Pump(() => user.RetryPendingForTesting));
    }

    // ---- SA3D (6), (7), (13), (14), (15) -------------------------------------

    [Fact] // :23661-23683 (6): pruning follows a SUCCESSFUL list only
    public void PruningFollowsASuccessfulPeerListOnly()
    {
        var source = NewSource(Peer(PeerA));
        var store = NewStore();
        store.SetString(DeviceOverlayState.SelectionKey,
            JsonSerializer.Serialize(new StoredDeviceSelection { Local = true, Peers = [PeerA, PeerB] },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var overlay = NewOverlay(source, store);
        overlay.RefreshPeers();
        // A successful list without a stored device drops it from the stored selection.
        Assert.True(Pump(() => overlay.SelectablePeers.Count == 1 && overlay.Stored.Peers.SequenceEqual([PeerA])));
        SameList([PeerA], Persisted(store)!.Peers);
        source.Set(s => s.PeersError = new FakeDeviceSource.FakeFailure());
        overlay.RefreshPeers();
        // A failed list hides the menu and leaves the stored selection alone.
        Assert.True(Pump(() => overlay.SelectablePeers.Count == 0));
        SameList([PeerA], overlay.Stored.Peers);
        SameList([PeerA], Persisted(store)!.Peers);
        source.Set(s => s.Calls = []);
        overlay.Ensure(AppView.Hourly, null, null);
        Quiet();
        Assert.Empty(source.Calls); // with the list unreadable only this PC's path runs
        Assert.False(overlay.IncludesRemote);
        source.Set(s => s.PeersError = null);
        overlay.RefreshPeers();
        Assert.True(Pump(() => overlay.IncludesRemote)); // the next successful list restores the selection
        SameList([PeerA], overlay.Effective!.Peers);
        overlay.Ensure(AppView.Hourly, null, null);
        Assert.True(Pump(() => source.Calls.Count == 2) && Settled()); // and the device calls resume
        source.Set(s => s.Peers = [Peer(PeerA, state: "revoked")]);
        overlay.RefreshPeers();
        // A device that is no longer active is dropped and this PC is chosen again.
        Assert.True(Pump(() => overlay.SelectablePeers.Count == 0));
        Assert.Empty(overlay.Stored.Peers);
        Assert.True(overlay.Stored.Local);
    }

    [Fact] // :23695-23704 (7): the last chosen source cannot be unchecked
    public void TheLastChosenSourceCannotBeUnchecked()
    {
        var source = NewSource(Peer(PeerA));
        var store = NewStore();
        store.SetString(DeviceOverlayState.SelectionKey,
            JsonSerializer.Serialize(new StoredDeviceSelection { Local = true, Peers = [PeerA] },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var overlay = NewOverlay(source, store);
        ReadPeers(overlay);
        overlay.ToggleLocal(); // with two chosen, this PC can be unchecked
        Assert.False(overlay.Stored.Local);
        SameList([PeerA], overlay.Stored.Peers);
        overlay.TogglePeer(PeerA); // the last remaining device cannot be unchecked
        SameList([PeerA], overlay.Stored.Peers);
        Assert.False(overlay.CanTogglePeer(PeerA));
        overlay.ToggleLocal();
        overlay.TogglePeer(PeerA);
        overlay.ToggleLocal();
        Assert.True(overlay.Stored.Local); // nor can this PC when it is the last
        Assert.Empty(overlay.Stored.Peers);
        Assert.False(overlay.CanToggleLocal());
    }

    private (DeviceOverlayState Overlay, FakeDeviceSource Source, FakeDeviceClock Clock) WithRemoteClients()
    {
        var (overlay, source, clock, _) = Chosen();
        foreach (var (view, year) in new[] { (AppView.Stats, (string?)null), (AppView.Hourly, null), (AppView.Agents, null) })
        {
            clock.Advance(DeviceOverlayState.FreshWindow);
            overlay.Ensure(view, year, null);
            Assert.True(Idle(overlay));
        }
        return (overlay, source, clock);
    }

    [Fact] // :23852-23876 (11): the request carries the registry minus the hidden, not this PC's present list
    public void TheHiddenClientsRequestSendsTheRegistryIdsMinusTheHiddenOnes()
    {
        var (overlay, source, clock) = WithRemoteClients();
        var hiddenArgument = DeviceOverlayRequests.ClientsArgument(null, new HashSet<string> { "claude" });
        var expected = ClientRegistry.AllIds
            .Where(id => !ClientRegistry.HiddenTabClients(new HashSet<string> { "claude" }).Contains(id))
            .Order(StringComparer.Ordinal).ToList();
        source.Set(s => s.Calls = []);
        clock.Advance(DeviceOverlayState.FreshWindow);
        overlay.Ensure(AppView.Hourly, "2018", hiddenArgument);
        Assert.True(Pump(() => source.Refreshes.Any(c => c.Kind == "hourly")) && Idle(overlay));
        Assert.NotEmpty(source.Calls);
        Assert.All(source.Calls, c => Assert.Equal(string.Join(",", expected), c.Clients));
        // What the lens selects: only the page for the key it wants now (:23878-23883 SA3D (5)).
        Assert.NotNull(overlay.Visible(overlay.Hourly, DeviceOverlayKind.Hourly, "2018", expected).Page);
        Assert.Null(overlay.Visible(overlay.Hourly, DeviceOverlayKind.Hourly, "2017", expected).Page);
        Assert.Null(overlay.Visible(overlay.Hourly, DeviceOverlayKind.Hourly, "2018", null).Page);
        Assert.Null(overlay.Visible(overlay.Graph, DeviceOverlayKind.Graph, "2017", null).Page);
    }

    [Theory] // :23852-23876 (11): a client only a remote device used is shown, the hidden one dropped
    [InlineData(DeviceOverlayKind.Graph)]
    [InlineData(DeviceOverlayKind.Models)]
    [InlineData(DeviceOverlayKind.Hourly)]
    [InlineData(DeviceOverlayKind.Agents)]
    public void OnOverviewTheDisplaySetHoldsTheRemoteOnlyClientAndDropsTheHiddenOne(DeviceOverlayKind kind)
    {
        var (overlay, _, _) = WithRemoteClients();
        var shown = overlay.DisplayClients(kind, null, "", "");
        var hidden = overlay.DisplayClients(kind, null, "claude", "");
        Assert.Contains(FakeDeviceSource.RemoteClient, shown);
        Assert.Contains(FakeDeviceSource.RemoteClient, hidden);
        Assert.DoesNotContain("claude", hidden);
    }

    [Fact] // :23852-23876 (11): a single-client tab keeps its own slice
    public void ASingleClientTabKeepsItsOwnSlice()
    {
        var (overlay, _, _) = WithRemoteClients();
        SameList(ClientRegistry.TabSlice("codex"), overlay.DisplayClients(DeviceOverlayKind.Hourly, "codex", "", ""));
    }

    [Fact] // :23896-23907 (12): Stats with a graph overlay and no models overlay shows the fixed line
    public void StatsWithoutAModelsOverlayShowsTheFixedLineAndTakesTheOverlaysReportOnceItArrives()
    {
        var (overlay, source, clock, _) = Chosen(s => s.FailModels = true);
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => overlay.Graph?.Data is not null && overlay.Models?.Failed == true) && Idle(overlay));
        var hidden = overlay.StatsModels(null);
        Assert.Null(hidden.Report);
        Assert.Equal(C.ModelsHidden.Localized(), hidden.Note);
        source.Set(s => s.FailModels = false);
        overlay.ManualRefresh();
        Assert.True(Pump(() => overlay.StatsModels(null).Report is not null));
        Assert.Null(overlay.StatsModels(null).Note); // once the models overlay arrives the report is the overlay's
        Assert.Null(overlay.StatsModels("2000").Report); // a models overlay for another year is not shown
        Assert.Equal(C.ModelsHidden.Localized(), overlay.StatsModels("2000").Note);
        Assert.True(Idle(overlay));
    }

    [Fact] // :23919-23928 (13): at most 16 devices can be checked
    public void AtMostSixteenDevicesCanBeChecked()
    {
        var many = Enumerable.Range(1, 17).Select(i => i.ToString("x64")).ToList();
        var source = NewSource([.. many.Select(id => Peer(id))]);
        var overlay = NewOverlay(source, NewStore());
        ReadPeers(overlay, 17);
        foreach (var id in many) { overlay.TogglePeer(id); }
        SameList(many.Take(16), overlay.Stored.Peers);
        Assert.False(overlay.CanTogglePeer(many[16]));
        Assert.Equal(16, overlay.Effective!.Peers.Count);
        Assert.True(overlay.CanTogglePeer(many[0]));
        overlay.TogglePeer(many[0]); // unchecking one frees a slot for the 17th
        overlay.TogglePeer(many[16]);
        Assert.Equal(16, overlay.Stored.Peers.Count);
        Assert.Contains(many[16], overlay.Stored.Peers);
        Assert.DoesNotContain(many[0], overlay.Stored.Peers);
    }

    [Fact] // :23953-23957 (14): a cached load that ends after a newer lens took over must not replace the newer pending refresh
    public void ACachedLoadThatEndsAfterANewerLensTookOverDoesNotReplaceTheNewerPendingRefresh()
    {
        var (overlay, source, _, _) = Chosen();
        source.Set(s => s.HoldRefresh = true);
        overlay.Ensure(AppView.Agents, "2031", null);
        Assert.True(Pump(() => source.RefreshStarted == 1));
        source.Set(s => s.HoldCached = true);
        overlay.Ensure(AppView.Agents, "2032", null);
        Assert.True(Pump(() => source.Calls.Any(c => c.Year == "2032" && c.Mode == DeviceFetchMode.Cached)));
        source.Set(s => s.HoldCached = false);
        overlay.Ensure(AppView.Agents, "2033", null);
        Assert.True(Pump(() => overlay.Agents?.Key.Year == "2033"));
        Quiet();
        source.ReleaseCached();
        Quiet();
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled()); // a catch-up refresh runs after the held one
        SameList(["2031", "2033"], source.Refreshes.Select(c => c.Year!)); // the newest lens, not the one whose cached load ended last
        Assert.True(Pump(() => overlay.Agents?.Data?.Payload.Entries[0].Agent == "refresh-2033"));
    }

    [Fact] // :23978-23983 (15): closing the flyout stops what is on screen, the slots stay
    public void ClosingTheFlyoutStopsAHeldTwoPageRefreshAndSkipsAPendingOneWhileTheSlotsStay()
    {
        var (overlay, source, _, _) = Chosen();
        source.Set(s => s.HoldRefresh = true);
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.RefreshStarted == 1));
        overlay.ManualRefresh();
        overlay.PopoverClosed();
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Settled();
        Quiet();
        Assert.Equal(1, source.RefreshStarted); // no further call, nothing pending runs
        Assert.NotNull(overlay.Graph?.Data);
        Assert.NotNull(overlay.Models?.Data);
        overlay.Ensure(AppView.Stats, null, null); // a reopened flyout's Ensure refreshes again
        Assert.True(Pump(() => source.RefreshStarted == 3) && Settled());
    }

    // ---- DR2C: snapshot publishing -----------------------------------------

    [Fact] // :24204-24218 (DR2C): the overlay's result reaches what Settings reads, and only when it changed
    public void TheOverlaysResultIsPublishedAsASnapshotOnlyWhenItChanges()
    {
        var peer = Peer(PeerA);
        var (overlay, source, _, _) = Chosen(peers: peer);
        var published = new List<DeviceOverlaySnapshot>();
        overlay.SnapshotChanged += s => { lock (published) { published.Add(s); } };
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => overlay.Graph?.Data is not null) && Idle(overlay));
        // The event is raised just after the state it describes is visible.
        Assert.True(Pump(() => { lock (published) { return published.Count > 0 && published[^1].Equals(overlay.Snapshot); } }));
        DeviceOverlaySnapshot last;
        lock (published) { last = published[^1]; }
        Assert.True(last.Selection[Fp]);
        Assert.NotNull(C.WorstRow(Fp, last.Rows));
        Assert.NotNull(last.SettingsStatus(peer, Now));
        lock (published)
        {
            for (var i = 1; i < published.Count; i++) { Assert.NotEqual(published[i - 1], published[i]); }
        }
        overlay.PopoverClosed();
    }

    [Fact] // :3216-3219, :3225 (publishes): an instance that never fetched or changed a selection publishes nothing
    public void AnInstanceThatHasNotPublishedMustNotOverwriteWhatTheDashboardPublished()
    {
        var source = NewSource(Peer(PeerA));
        var overlay = NewOverlay(source, NewStore());
        var raised = 0;
        overlay.SnapshotChanged += _ => Interlocked.Increment(ref raised);
        ReadPeers(overlay);
        Quiet();
        Assert.Equal(0, raised);
        overlay.TogglePeer(PeerA); // a selection change is a publish
        Assert.Equal(1, raised);
    }

    // ---- DR2B2: names, generations ------------------------------------------

    [Fact] // :24309-24311 (DR2B2): a selected device with no page row yet is in the line by the name on its peer record
    public void ASelectedDeviceWithNoPageRowYetIsNamedByItsPeerRecord()
    {
        var (overlay, source, _, _) = Chosen(s => s.HoldCached = true, Peer(PeerA, label: "Studio Mac"));
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Calls.Count > 0));
        SameList([C.GettingUsage.Localized("Studio Mac")], overlay.StatusLines(AppView.Overview, null, null));
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached(2);
        Assert.True(Idle(overlay));
    }

    [Fact] // :24397-24432 (DR2B2 D1): the newest name a page reported wins over the peer list's
    public void TheNameAPageReportedIsUsedEverywhereAndALaterRowWithoutOneDropsIt()
    {
        var bare = Peer(PeerA);
        var (overlay, source, _, _) = Chosen(
            s => { s.RemoteState = "lastGood"; s.RemoteReason = "busy"; s.RemoteLabel = "Studio Mac"; }, bare);
        var peerRow = overlay.SelectablePeers[0];
        Assert.Equal(Name, overlay.DisplayName(peerRow)); // before any page, a device without a name is its address
        Assert.Empty(overlay.LearnedNames);
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => overlay.Graph?.Data is not null && overlay.Models?.Data is not null) && Idle(overlay));
        RemoteDevice Row(string state, string? label) =>
            new(Fp, state, label, T0, null, null, Address, false, true);
        Assert.Equal("Studio Mac", overlay.DisplayName(peerRow));
        Assert.Equal("Studio Mac", C.MenuTitle(1, false, overlay.DisplayName(peerRow)));
        Assert.Equal("Studio Mac", DeviceTotals.Rows([Row("fresh", Address)], [], overlay.LearnedNames)[0].Name);
        Assert.Equal("Studio Mac", overlay.DisplayName(Row("fresh", Address)));
        // A year no page was loaded for: the line falls back to the peer, by the learned name too.
        source.Set(s => s.HoldCached = true);
        overlay.Ensure(AppView.Overview, "2000", null);
        Assert.True(Pump(() => source.Calls.Count > 4));
        SameList([C.GettingUsage.Localized("Studio Mac")], overlay.StatusLines(AppView.Overview, "2000", null));
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached(2);
        Assert.True(Idle(overlay));
        // The owner clears the name: the next page says so.
        source.Set(s => s.RemoteLabel = null);
        overlay.ManualRefresh();
        Assert.True(Pump(() => overlay.DisplayName(peerRow) == Name));
        Assert.True(Idle(overlay));
    }

    [Fact] // :24464-24488 (DR2B2): only the newest peer read is applied
    public void AnOlderPeerReadThatFinishesAfterANewerOneIsIgnored()
    {
        var source = NewSource();
        var overlay = NewOverlay(source, NewStore(seeded: false));
        var bare = Peer(PeerA);
        var readA = overlay.BeginPeersReadForTesting();
        var readB = overlay.BeginPeersReadForTesting();
        overlay.DeliverPeersForTesting([bare], readB);
        var afterB = overlay.Stored.Peers;
        overlay.DeliverPeersForTesting([], readA);
        SameList([PeerA], afterB);
        SameList([PeerA], overlay.Stored.Peers); // the new device stays listed and ticked
        SameList([PeerA], overlay.SelectablePeers.Select(p => p.ProducerStatic));
        Assert.True(overlay.IncludesRemote);
        var readC = overlay.BeginPeersReadForTesting(); // control: the newest read's own result still applies
        overlay.DeliverPeersForTesting([], readC);
        Assert.Empty(overlay.SelectablePeers);
        Assert.Empty(overlay.Stored.Peers);
        var readD = overlay.BeginPeersReadForTesting(); // a read superseded while in flight is not applied
        _ = overlay.BeginPeersReadForTesting();
        overlay.DeliverPeersForTesting([bare], readD);
        Assert.Empty(overlay.SelectablePeers);
    }

    [Fact] // :24488 (DR2B2): in flight, a device whose page row says it never answered shows the first-scan sentence
    public void InFlightADeviceThatNeverAnsweredShowsTheFirstScanSentence()
    {
        var (overlay, source, _, _) = Chosen(
            s => { s.RemoteState = "unavailable"; s.RemoteLabel = "Studio Mac"; s.RemoteEverAnswered = false; },
            Peer(PeerA, label: "Studio Mac"));
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.RefreshStarted >= 1) && Settled());
        source.Set(s => s.HoldRefresh = true);
        overlay.ManualRefresh();
        Assert.True(Pump(() => source.RefreshStarted >= 3));
        Assert.True(overlay.Refreshing);
        SameList([C.GettingUsageFirstScan.Localized("Studio Mac")], overlay.StatusLines(AppView.Overview, null, null));
        source.Set(s => { s.HoldRefresh = false; s.RemoteEverAnswered = true; });
        source.Release();
        Assert.True(Pump(() => !overlay.Refreshing) && Settled());
    }

    // ---- FOLLOWUP: re-link ----------------------------------------------------

    [Fact] // :23152-23183 FOLLOWUP: a revoked device is checked again on re-link; one the user unchecked is not
    public void ACheckedRevokedDeviceIsCheckedAgainWhenLinkedAgainButAnUncheckedOneStaysUnchecked()
    {
        var store = NewStore(seeded: false);
        var source = NewSource();
        DeviceOverlayState Instance() => NewOverlay(source, store);
        void Reload(DeviceOverlayState o, string state) =>
            o.DeliverPeersForTesting([Peer(PeerA, state)], o.BeginPeersReadForTesting());

        var overlay = Instance();
        Reload(overlay, "active");
        var ticked = overlay.IncludesRemote;
        Reload(overlay, "revoked");
        var gone = overlay.SelectablePeers.Count == 0 && !overlay.IncludesRemote;
        Reload(overlay, "active");
        Assert.True(ticked && gone && overlay.IncludesRemote); // :23152

        overlay.TogglePeer(PeerA);
        var unticked = !overlay.IncludesRemote;
        Reload(overlay, "revoked");
        Reload(overlay, "active");
        Assert.True(unticked && overlay.SelectablePeers.Count == 1 && !overlay.IncludesRemote); // :23158

        // A second instance (the Settings window's) loaded while the device was
        // still checked must not undo that (:23172).
        overlay.TogglePeer(PeerA);
        var stale = Instance();
        overlay.TogglePeer(PeerA);
        Reload(stale, "revoked");
        var fresh = Instance();
        Reload(fresh, "active");
        Assert.False(fresh.IncludesRemote);
        Assert.Single(fresh.SelectablePeers);

        // Two instances hold the device checked; one sees it revoked (and forgets
        // it), the other, which never saw that, then sees it active: one entry,
        // not two (:23183).
        fresh.TogglePeer(PeerA);
        var other = Instance();
        Reload(fresh, "revoked");
        Reload(other, "active");
        SameList([PeerA], other.Stored.Peers);
    }

    // ---- DR2A (R4): arrival -----------------------------------------------------

    [Fact] // :24716-24738 (R4): neither key, an unchecked device is never checked again, a later one is
    public void ANewDeviceIsCheckedOnArrivalOnceAndAnUncheckedOneStaysUnchecked()
    {
        var store = NewStore(seeded: false);
        var source = NewSource(Peer(PeerA));
        var first = NewOverlay(source, store);
        ReadPeers(first);
        // With neither key a newly linked device is checked and recorded as seen.
        SameList([PeerA], first.Stored.Peers);
        Assert.True(first.IncludesRemote);
        SameSet([PeerA], Seen(store));
        SameList([PeerA], Persisted(store)!.Peers);
        first.TogglePeer(PeerA); // this PC stays chosen, so the device can go
        Assert.True(first.Stored.Local);
        Assert.Empty(first.Stored.Peers);
        first.RefreshPeers();
        Quiet();
        Assert.Empty(first.Stored.Peers); // a later list does not check it again
        var second = NewOverlay(source, store);
        ReadPeers(second);
        Quiet();
        Assert.Empty(second.Stored.Peers); // nor does a new instance (a reopened flyout)
        Assert.False(second.IncludesRemote);
        source.Set(s => s.Peers = [Peer(PeerA, state: "revoked")]);
        second.RefreshPeers();
        Assert.True(Pump(() => second.SelectablePeers.Count == 0));
        source.Set(s => s.Peers = [Peer(PeerA)]);
        second.RefreshPeers();
        Assert.True(Pump(() => second.SelectablePeers.Count == 1));
        Quiet();
        Assert.Empty(second.Stored.Peers); // inactive and then active again is not a new device
        SameSet([PeerA], Seen(store));
        source.Set(s => s.Peers = [Peer(PeerA), Peer(PeerC)]);
        second.RefreshPeers();
        Assert.True(Pump(() => second.SelectablePeers.Count == 2));
        SameList([PeerC], second.Stored.Peers); // a device linked later is checked, the unchecked one stays unchecked
        SameSet([PeerA, PeerC], Seen(store));
    }

    [Fact] // :24738-24757 (R4): a selection saved before the seen set decodes unchanged; the first list only records
    public void AfterAnUpgradeTheFirstListKeepsTheUsersChoiceAndRecordsTheExistingDevicesAsSeen()
    {
        var store = NewStore(seeded: false);
        store.SetString(DeviceOverlayState.SelectionKey, """{"local":false,"peers":["%s"]}""".Replace("%s", PeerA));
        Assert.Equal(new StoredDeviceSelection { Local = false, Peers = [PeerA] }, Persisted(store));
        var source = NewSource(Peer(PeerA), Peer(PeerB));
        var old = NewOverlay(source, store);
        Assert.Equal(new StoredDeviceSelection { Local = false, Peers = [PeerA] }, old.Stored); // decodes unchanged
        Assert.Null(Seen(store)); // unknown until the first successful list
        ReadPeers(old, 2);
        Assert.Equal(new StoredDeviceSelection { Local = false, Peers = [PeerA] }, old.Stored);
        SameSet([PeerA, PeerB], Seen(store));
        source.Set(s => s.Peers = [Peer(PeerA), Peer(PeerB), Peer(PeerC)]);
        old.RefreshPeers();
        Assert.True(Pump(() => old.SelectablePeers.Count == 3));
        SameList([PeerA, PeerC], old.Stored.Peers); // after that a new device is checked
    }

    [Fact] // :24757-24768 (R4): an unreadable list changes nothing and records nothing
    public void AnUnreadablePeerListChangesNothingAndRecordsNothing()
    {
        var store = NewStore(seeded: false);
        var source = NewSource();
        source.Set(s => s.PeersError = new FakeDeviceSource.FakeFailure());
        var overlay = NewOverlay(source, store);
        overlay.RefreshPeers();
        Assert.True(Pump(() => source.Get(s => s.PeerCalls) == 1));
        Quiet();
        Assert.Equal(new StoredDeviceSelection(), overlay.Stored);
        Assert.Null(Seen(store));
    }

    // ---- DR2A (2, 3), (R2), (5), (R2, R3): lenses, budget, busy ------------------------

    [Fact] // :24632-24667 (2): Overview takes the overlay's own pages, never this PC's
    public void OverviewTakesTheOverlaysOwnPagesAndNothingBeforeTheyArrive()
    {
        var (overlay, source, _, _) = Chosen();
        ModelColorMap Local() => new((ModelReport?)null);
        OverviewMergeState? Merge(string? single = null, string hidden = "") =>
            overlay.OverviewMergeFor(null, single, hidden, "", Local());
        source.Set(s => { s.Calls = []; s.HoldCached = true; });
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Calls.Count > 0));
        var early = Merge();
        Assert.NotNull(early); // before the overlay arrives there is nothing to draw (loading)
        Assert.Null(early!.Graph);
        Assert.Null(early.Models);
        Assert.Equal(2, early.DeviceCount);
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached(2);
        Assert.True(Pump(() => overlay.Graph?.Data is not null && overlay.Models?.Data is not null) && Settled());
        SameList(["graph-cached", "models-cached", "graph-refresh", "models-refresh"], source.Calls.Select(c => c.Tag));
        var full = Merge();
        Assert.Equal(FakeDeviceSource.OverlayGeneratedAt, full!.Graph!.Value.Payload.Meta.GeneratedAt);
        Assert.Contains(full.Models!.Entries, e => e.Model.StartsWith(FakeDeviceSource.RemoteModel));
        Assert.Contains(FakeDeviceSource.RemoteClient, full.ClientIds);
        Assert.Contains(FakeDeviceSource.RemoteClient, full.Graph.Value.Stats.PresentClients);
        SameList(ClientRegistry.TabSlice("claude"), Merge("claude")!.ClientIds);
        Assert.DoesNotContain("claude", Merge(hidden: "claude")!.ClientIds);
        overlay.TogglePeer(PeerA);
        Assert.Null(Merge()); // back to this PC alone, Overview has no merge
    }

    [Fact] // :24692-24703 (R2): the request budget, 2 refresh requests per producer per minute
    public void OverviewStatsDailyAndMonthlyInsideTheWindowAskTheProducerTwiceNotOncePerLens()
    {
        var (overlay, source, clock, _) = Chosen();
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Idle(overlay));
        foreach (var view in new[] { AppView.Stats, AppView.Daily, AppView.Overview, AppView.Monthly })
        {
            overlay.Ensure(view, null, null);
            Quiet();
            Assert.True(Idle(overlay));
        }
        Assert.Equal(2, source.Refreshes.Count);
        Assert.Equal(4, source.Calls.Count);
        overlay.Ensure(AppView.Overview, null, null);
        Quiet();
        Assert.True(Idle(overlay));
        overlay.ManualRefresh();
        Assert.True(Pump(() => source.Refreshes.Count == 4) && Settled()); // a manual refresh still asks inside the window
        clock.Advance(DeviceOverlayState.FreshWindow);
        overlay.Ensure(AppView.Stats, null, null);
        Assert.True(Pump(() => source.Refreshes.Count == 6) && Settled()); // once the window has passed a lens asks again
    }

    [Fact] // :24716-24738 (5): the overlay is refreshing while its refresh is out, and the status line says so
    public void WhileTheRefreshIsOutTheOverlayIsRefreshingAndTheStatusLineSaysGettingUsage()
    {
        var (overlay, source, _, _) = Chosen(s => s.RemoteState = "lastGood");
        Assert.False(overlay.Refreshing);
        source.Set(s => s.HoldRefresh = true);
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.RefreshStarted == 1));
        Assert.True(overlay.Refreshing);
        SameList([C.GettingUsage.Localized(Name)], overlay.StatusLines(AppView.Overview, null, null));
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Pump(() => !overlay.Refreshing) && Settled());
        Assert.All(overlay.StatusLines(AppView.Overview, null, null), l => Assert.DoesNotContain("◌", l));
    }

    [Fact] // :24759-24805 (R2, R3): a busy refresh arms one retry and the sentence says so only while it is armed
    public void ABusyRefreshArmsOneRetryAndTheSentenceSaysSoOnlyWhileItIsArmed()
    {
        var (overlay, source, clock, _) = Chosen(s => { s.RemoteState = "lastGood"; s.RemoteReason = "busy"; });
        IReadOnlyList<string> Lines() => overlay.StatusLines(AppView.Overview, null, null);
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => overlay.RetryPendingForTesting) && Settled());
        SameList([C.BusyDataRetry.Localized(Name, Age)], Lines());
        overlay.PopoverClosed();
        Assert.False(overlay.RetryPendingForTesting); // with no retry armed the sentence does not promise one
        SameList([C.BusyData.Localized(Name, Age)], Lines());
        source.Set(s => { s.RemoteState = "fresh"; s.RemoteReason = null; });
        clock.Advance(DeviceOverlayState.FreshWindow);
        overlay.Ensure(AppView.Overview, null, null);
        Quiet();
        Assert.True(Idle(overlay));
        Assert.False(overlay.RetryPendingForTesting); // control: nothing is armed before the poll
        source.Set(s => { s.RemoteState = "lastGood"; s.RemoteReason = "busy"; });
        overlay.PollOnce();
        Assert.True(Pump(() => overlay.RetryPendingForTesting) && Settled()); // a poll's refresh that gets busy arms the retry too
        overlay.PopoverClosed();
    }

    [Fact] // :24805-24829 (R2): a poll's refresh that is unreachable arms no retry (and ends the lens's own, which it completes)
    public void APollRefreshThatIsUnreachableArmsNoRetry()
    {
        var (overlay, source, _, _) = Chosen(s => { s.RemoteState = "lastGood"; s.RemoteReason = "unreachable"; });
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        Assert.True(Pump(() => overlay.RetryPendingForTesting)); // the lens's own refresh left no new data: armed
        overlay.PollOnce();
        Assert.True(Pump(() => source.RefreshStarted == 4) && Settled());
        // The poll completed the retry's keys (so that retry is moot) and arms none of its own.
        Assert.True(Pump(() => !overlay.RetryPendingForTesting));
    }

    [Fact] // :24778 FOLLOWUP: a voided retry is no longer armed once it wakes, and the retry clause goes
    public void AVoidedRetryIsNoLongerArmedOnceItWakesAndTheRetryClauseGoes()
    {
        var (overlay, _, clock, _) = Chosen(s => { s.RemoteState = "lastGood"; s.RemoteReason = "busy"; });
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => overlay.RetryPendingForTesting && clock.PendingCount == 1) && Settled());
        DeviceOverlayState.ResetSingleFlightForTesting();
        clock.Advance(DeviceOverlayState.RetryDelay);
        Assert.True(Pump(() => !overlay.RetryPendingForTesting));
        SameList([C.BusyData.Localized(Name, Age)], overlay.StatusLines(AppView.Overview, null, null));
    }

    // ---- DR2A review rounds ----------------------------------------------------

    [Fact] // :24829-24846 (review 1): a scan-roots change inside the window refreshes the visible keys
    public void AScanRootsChangeInsideTheWindowRefreshesTheVisibleKeys()
    {
        var (overlay, source, _, _) = Chosen();
        overlay.Ensure(AppView.Overview, null, null, rootsGeneration: 1);
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Idle(overlay));
        overlay.Ensure(AppView.Overview, null, null, rootsGeneration: 1);
        Quiet();
        Assert.Equal(2, source.Refreshes.Count); // control: the same roots inside the window ask nothing
        overlay.Ensure(AppView.Overview, null, null, rootsGeneration: 2);
        Assert.True(Pump(() => source.Refreshes.Count == 4) && Settled());
    }

    [Fact] // :24847-24862 (review b): coming back to a year refreshed inside the window asks no producer
    public void ComingBackToAYearRefreshedInsideTheWindowLoadsTheCachedPageAndAsksNoProducer()
    {
        var (overlay, source, _, _) = Chosen();
        overlay.Ensure(AppView.Daily, null, null);
        Assert.True(Pump(() => source.Refreshes.Count == 1) && Idle(overlay));
        overlay.Ensure(AppView.Daily, "2024", null);
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Idle(overlay));
        var before = source.Calls.Count;
        overlay.Ensure(AppView.Daily, null, null);
        Assert.True(Pump(() => source.Calls.Count > before));
        Quiet();
        Assert.True(Idle(overlay));
        Assert.Equal(2, source.Refreshes.Count);
        SameList(["cached-all"], source.Calls.Skip(before).Select(c => $"{(c.Mode == DeviceFetchMode.Cached ? "cached" : "refresh")}-{c.Year ?? "all"}"));
    }

    [Fact] // :24863-24871 (review a): a refresh that did not bring the device back fresh does not use the budget
    public void ABusyRefreshDoesNotCountAsFreshSoTheLensAsksAgain()
    {
        var (overlay, source, _, _) = Chosen(s => { s.RemoteState = "lastGood"; s.RemoteReason = "busy"; });
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Settled());
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Refreshes.Count == 4) && Settled());
    }

    [Fact] // :24872-24884 (review 2, 3): the kept-last and saturated notes reach Overview
    public void TheKeptLastAndSaturatedLinesReachOverview()
    {
        var (overlay, source, _, _) = Chosen();
        OverviewMergeState? Merge() => overlay.OverviewMergeFor(null, null, "", "", new ModelColorMap((ModelReport?)null));
        overlay.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => overlay.Graph?.Data is not null && overlay.Models?.Data is not null) && Idle(overlay));
        var clean = Merge()!;
        Assert.False(clean.KeptLast);
        Assert.False(clean.Saturated);
        Assert.Empty(clean.NoteLines); // a clean page has no Overview note
        source.Set(s => s.FailRefresh = true);
        overlay.ManualRefresh();
        Assert.True(Pump(() => overlay.Graph?.Failed == true) && Idle(overlay));
        Assert.NotNull(Merge()!.Graph);
        Assert.True(Merge()!.KeptLast);
        SameList([C.KeptLast.Localized()], Merge()!.NoteLines); // the retained page carries the kept-last line
        source.Set(s => { s.FailRefresh = false; s.Saturated = true; });
        overlay.ManualRefresh();
        Assert.True(Pump(() => Merge()?.Saturated == true) && Settled());
        Assert.False(Merge()!.KeptLast);
        SameList([C.Saturated.Localized()], Merge()!.NoteLines); // once, with both pages saturated
    }

    // ---- DR2A on-screen round 1 -------------------------------------------------

    [Fact] // :25012-25030 (screen 4): while the cached load runs, a device with no data reads as getting usage
    public void WhileTheCachedLoadRunsADeviceWithNoDataReadsAsGettingUsageNotNoDataYet()
    {
        var (slow, source, _, _) = Chosen(s => s.RemoteState = "unavailable");
        source.Set(s => s.HoldCached = true);
        slow.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => source.Calls.Count > 0));
        Assert.False(slow.Refreshing);
        Assert.True(slow.Fetching);
        SameList([C.GettingUsage.Localized(Name)], slow.StatusLines(AppView.Overview, null, null));
        // A load for a lens no longer on screen is not in flight for the one that is (:25023).
        slow.PopoverClosed();
        Assert.False(slow.Fetching);
        Assert.Equal(0, slow.CachedLoads);
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached(2);
        slow.Ensure(AppView.Overview, null, null);
        Assert.True(Pump(() => slow.Graph?.Data is not null && slow.Models?.Data is not null) && Settled());
        Assert.True(Pump(() => !slow.Fetching));
        SameList([C.NoDataYet.Localized(Name)], slow.StatusLines(AppView.Overview, null, null)); // the no-data sentence returns
    }

    [Fact] // :25031-25053 (screen 4b): a refresh queued behind another model's reads as getting usage
    public void ARefreshQueuedBehindAnotherModelsReadsAsGettingUsage()
    {
        var (first, firstSource, _, _) = Chosen(s => s.RemoteState = "unavailable");
        var (second, secondSource, _, _) = Chosen(s => s.RemoteState = "unavailable");
        DeviceOverlayState.ResetSingleFlightForTesting();
        firstSource.Set(s => s.HoldRefresh = true);
        first.Ensure(AppView.Daily, null, null);
        Assert.True(Pump(() => firstSource.RefreshStarted == 1));
        second.Ensure(AppView.Daily, null, null);
        Assert.True(Pump(() => second.Graph?.Data is not null && second.QueuedRefresh));
        Assert.False(second.Refreshing);
        Assert.True(second.Fetching);
        SameList([C.GettingUsage.Localized(Name)], second.StatusLines(AppView.Daily, null, null));
        firstSource.Set(s => s.HoldRefresh = false);
        firstSource.Release();
        Assert.True(Pump(() => secondSource.Refreshes.Count == 1) && Settled());
        Assert.True(Pump(() => !second.Fetching)); // the queued refresh runs and the pending state ends
    }

    [Fact] // :25054-25072 (screen 6): a refresh begun under the old roots does not suppress the refresh the new roots need
    public void ARefreshThatBeganUnderTheOldRootsDoesNotSuppressTheRefreshTheNewRootsNeed()
    {
        var (roots, source, _, _) = Chosen(s => s.RemoteState = "fresh");
        source.Set(s => s.HoldRefresh = true);
        roots.Ensure(AppView.Daily, null, null, rootsGeneration: 1);
        Assert.True(Pump(() => source.Refreshes.Count == 1));
        roots.Ensure(AppView.Daily, "2024", null, rootsGeneration: 1);
        Assert.True(Pump(() => roots.Graph?.Key.Year == "2024"));
        source.Set(s => s.HoldCached = true);
        roots.Ensure(AppView.Daily, null, null, rootsGeneration: 2);
        Assert.True(Pump(() => source.Calls.Count(c => c.Mode == DeviceFetchMode.Cached && c.Year is null) == 2));
        source.Set(s => s.HoldRefresh = false);
        source.Release();
        Assert.True(Pump(() => !roots.Refreshing));
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached();
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Settled());
    }

    // ---- 4e2-specific: timezone, poll loop ---------------------------------------

    [Fact] // brief item 3: timezoneUnavailable is a failed fetch, never thrown; the page keeps its last good data
    public void ATimezoneUnavailableIsStoredLikeAFailedFetchAndKeepsTheLastGoodPage()
    {
        var (overlay, source, clock, _) = Chosen();
        overlay.Ensure(AppView.Hourly, "2025", null);
        Assert.True(Pump(() => overlay.Hourly?.Data is not null) && Idle(overlay));
        var good = overlay.Hourly!.Data;
        clock.Advance(DeviceOverlayState.FreshWindow);
        source.Set(s => { s.FailAll = true; s.Failure = new TbCoreException("timezoneUnavailable"); });
        overlay.Ensure(AppView.Hourly, "2025", null);
        Assert.True(Pump(() => overlay.Hourly?.Failed == true) && Idle(overlay));
        Assert.Same(good, overlay.Hourly!.Data);
        // and with nothing to keep: a failed slot with no data
        overlay.Ensure(AppView.Hourly, "2026", null);
        Assert.True(Pump(() => overlay.Hourly?.Key.Year == "2026" && overlay.Hourly.Failed) && Idle(overlay));
        Assert.Null(overlay.Hourly!.Data);
    }

    [Fact] // :3450-3456 pollLoop: a tick every 300 s refreshes the lens on screen; cancelling ends the loop
    public async Task ThePollLoopRefreshesTheLensOnScreenEveryThreeHundredSecondsAndStopsWhenCancelled()
    {
        var (overlay, source, clock, _) = Chosen();
        overlay.Ensure(AppView.Hourly, "2012", null);
        Assert.True(Pump(() => source.RefreshStarted == 1) && Idle(overlay));
        using var cts = new CancellationTokenSource();
        var loop = overlay.PollLoopAsync(cts.Token);
        Assert.Equal<TimeSpan>([DeviceOverlayState.RemotePoll], clock.Requested);
        clock.Advance(DeviceOverlayState.RemotePoll);
        Assert.True(Pump(() => source.RefreshStarted == 2) && Settled());
        Assert.True(Pump(() => clock.Requested.Count(d => d == DeviceOverlayState.RemotePoll) == 2)); // the next tick is armed
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.IsCompletedSuccessfully);
        Assert.Equal(0, clock.PendingCount);
    }
}
