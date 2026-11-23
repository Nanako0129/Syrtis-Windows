using System.Reflection;
using System.Text.Json;
using TokenBar.App;
using TokenBar.Interop;
using CostStyle = TokenBar.Core.DiscordPresence.CostStyle;
using M = TokenBar.App.DiscordDeviceMerge;
using Selection = TokenBar.Core.DiscordPresence.Selection;
using VisibilityChange = TokenBar.Core.DiscordIpc.VisibilityChange;

namespace TokenBar.Core.Tests;

// SA-4i: Discord counts the other devices selected in the dashboard. Port of
// macOS D1 (SelfTest.swift :12090-12358, Syrtis 161f7c23), each test citing
// the expectation it ports, plus the Windows-only rules (cost authority, the
// due margin, the controller's single critical section). The merged numbers
// come from the fake's GraphDevices (the core's job in the app); nothing here
// adds a local and a remote value. No native call, no Discord pipe.
public sealed class DiscordDevicesTests : IDisposable
{
    private const string Today = "2026-09-25";
    private static readonly string PeerA = new('a', 64);
    private static readonly string PeerB = new('b', 64);
    // The monotonic clock the cadence runs on (any origin).
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(1_000);

    private static TimeSpan At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    // Local: claude 1,000 today. Merged: the same plus 7,000,000 on another
    // device under a registered client id (SelfTest.swift :12095-12103).
    private static readonly UsagePayload Local = Graph(Stripe("claude", 1_000, 1.0));
    private static readonly UsagePayload Merged = Graph(Stripe("claude", 1_000, 1.0), Stripe("codex", 7_000_000, 7.0));

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-discord-devices", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SettingsStore NewStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

    private static UsagePayload Graph(params ContributionClient[] today) => DiscordPresenceTests.Graph(today);

    private static ContributionClient Stripe(string id, long tokens, double cost) =>
        DiscordPresenceTests.Stripe(id, tokens, cost);

    private static UsagePayload WithMeta(UsagePayload graph, PricingMode mode, CostCoverage coverage) =>
        graph with { Meta = graph.Meta with { PricingMode = mode, CostCoverage = coverage } };

    private static UsagePayload Dated(UsagePayload graph, string date) =>
        graph with { Contributions = [.. graph.Contributions.Select(c => c with { Date = date })] };

    private static DiscordPresence.Payload? Pay(UsagePayload? graph) =>
        graph is null
            ? null
            : DiscordPresence.Build(
                graph, new HashSet<string>(), Today, CostStyle.Banded, DiscordPresence.DefaultComponents,
                new Selection.MostUsed());

    /// <summary>The device source with a graph call only: records calls, can
    /// fail or throw, and can hold a call until released.</summary>
    private sealed class FakeSource : IDeviceSource
    {
        private readonly object _lock = new();
        private readonly List<(DeviceSelection Selection, DeviceFetchMode Mode)> _calls = [];
        private readonly List<string?> _years = [];
        private bool _holdTimedOut;

        public FakeSource(UsagePayload body) => Body = body;

        public UsagePayload Body { get; set; }
        public bool Fail { get; set; }
        public SemaphoreSlim? Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(DeviceSelection Selection, DeviceFetchMode Mode)> Calls
        {
            get { lock (_lock) { return [.. _calls]; } }
        }

        /// <summary>Every year argument received. Recorded, not asserted here:
        /// this runs inside the controller's worker, whose catch would swallow
        /// a failed assertion.</summary>
        public IReadOnlyList<string?> Years
        {
            get { lock (_lock) { return [.. _years]; } }
        }

        public bool HoldTimedOut
        {
            get { lock (_lock) { return _holdTimedOut; } }
        }

        public DeviceAwarePayload<UsagePayload> GraphDevices(string? year, DeviceSelection selection, DeviceFetchMode mode)
        {
            lock (_lock)
            {
                _calls.Add((selection, mode));
                _years.Add(year);
            }

            Entered.TrySetResult();
            if (Hold is { } hold && !hold.Wait(TimeSpan.FromSeconds(10)))
            {
                lock (_lock) { _holdTimedOut = true; }
            }

            if (Fail)
            {
                throw new InvalidOperationException("device call failed");
            }

            // The device row carries the other owner's free text.
            return new DeviceAwarePayload<UsagePayload>(
                Body, [new RemoteDevice("dev", "fresh", Label: "ZZLABEL", Address: "10.0.0.9:1")],
                false, false, false);
        }

        public IReadOnlyList<RemotePeer> RemotePeers() => throw new NotSupportedException();

        public DeviceAwarePayload<ModelReport> ModelReportDevices(string? year, DeviceSelection selection, DeviceFetchMode mode) =>
            throw new NotSupportedException();

        public DeviceAwarePayload<HourlyReport> HourlyReportDevices(
            string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode) =>
            throw new NotSupportedException();

        public DeviceAwarePayload<AgentsReport> AgentsReportDevices(
            string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode) =>
            throw new NotSupportedException();
    }

    // ---- the pure decision (macOS D1, carried out as AppDelegate does) -----

    private static M.Decision Decide(
        M.State state, bool on = true, bool discord = true, bool connect = true,
        IReadOnlyList<string>? peers = null, bool flyout = false, double at = 0) =>
        M.Decide(Local, state, on, discord, connect, peers ?? [PeerA], flyout, At(at));

    /// <summary>Carries a call out against the fake, as the controller does:
    /// a throw is a failed call, and only the UsagePayload is kept.</summary>
    private static M.State Run(M.Decision decision, FakeSource fake)
    {
        if (decision.Call is not { } call)
        {
            return decision.State;
        }

        UsagePayload? payload;
        try
        {
            payload = fake.GraphDevices(null, call.Selection, call.Mode).Payload;
        }
        catch (InvalidOperationException)
        {
            payload = null;
        }

        return M.Arrived(decision.State, call, payload);
    }

    [Fact]
    public void FixturePublishesDifferentFiguresAndANoTodayGraphPublishesNothing()
    {
        // SelfTest.swift :12123-12128 (the controls of every "must not happen").
        Assert.NotNull(Pay(Merged));
        Assert.NotEqual(Pay(Local)!.Details, Pay(Merged)!.Details);
        Assert.Null(Pay(Dated(Merged, "2026-08-03")));
        Assert.True(CostSurfaceProjection.IsAuthoritative(Merged));
    }

    [Fact]
    public void OffMakesNoCallAndUsesThisPcsGraph()
    {
        // D1 (1), SelfTest.swift :12130-12138.
        var fake = new FakeSource(Merged);
        var off = Decide(new M.State(), on: false);
        var on = Decide(new M.State(), on: true);
        Assert.Null(off.Call);
        Assert.Same(Local, off.Graph);
        Assert.Equal(Pay(Local), Pay(off.Graph));
        Assert.Empty(fake.Calls);
        Assert.NotNull(on.Call);
    }

    [Fact]
    public void OnWithAPeerUsesTheMergedGraphOnceItArrives()
    {
        // D1 (2), SelfTest.swift :12140-12151.
        var fake = new FakeSource(Merged);
        var first = Decide(new M.State());
        var state = Run(first, fake);
        var used = Decide(state, at: 60);
        Assert.Equal(Pay(Local), Pay(first.Graph));
        Assert.NotNull(Pay(used.Graph));
        Assert.NotEqual(Pay(Local), Pay(used.Graph));
        Assert.Equal(Pay(Merged), Pay(used.Graph));
    }

    [Fact]
    public void NoConnectionOrNoPeerMakesNoCall()
    {
        // D1 (2) control, SelfTest.swift :12152-12157.
        var noConnect = Decide(new M.State(), connect: false);
        var noPeers = Decide(new M.State(), peers: []);
        Assert.Null(noConnect.Call);
        Assert.Null(noPeers.Call);
        Assert.Equal(Pay(Local), Pay(noConnect.Graph));
        Assert.Equal(Pay(Local), Pay(noPeers.Graph));
    }

    [Fact]
    public void RefreshOnlyWithTheFlyoutClosedAndOncePerInterval()
    {
        // D1 (3), SelfTest.swift :12159-12172. Adapted: macOS's 299 s tick is
        // 269 s here, the last offset still inside the window once the 30 s
        // DueMargin applies (299 s is due on Windows by design, see below).
        var fake = new FakeSource(Merged);
        var state = new M.State();
        List<DeviceFetchMode> modes = [];
        foreach (var (offset, flyout) in new (double, bool)[]
                 {
                     (0, false), (60, false), (300 - M.DueMargin.TotalSeconds - 1, false), (300, false),
                     (359, false), (600, true), (900, true), (901, false),
                 })
        {
            var decision = Decide(state, flyout: flyout, at: offset);
            modes.Add(decision.Call?.Mode ?? DeviceFetchMode.Cached);
            state = Run(decision, fake);
        }

        Assert.Equal(
            [
                DeviceFetchMode.Refresh, DeviceFetchMode.Cached, DeviceFetchMode.Cached, DeviceFetchMode.Refresh,
                DeviceFetchMode.Cached, DeviceFetchMode.Cached, DeviceFetchMode.Cached, DeviceFetchMode.Refresh,
            ],
            modes);
        Assert.Equal(8, fake.Calls.Count);
    }

    [Fact]
    public void ClosedFlyoutTicksSlightlyUnder300SecondsApartEachRefresh()
    {
        // Windows (c): TrayFeed's 300 s timer plus fetch time (a forced full
        // reread takes about 25 s) can land a tick up to ~25 s short; each
        // still refreshes. Control: 60 s later is cached.
        Assert.InRange(M.DueMargin.TotalSeconds, 5, 30);
        var fake = new FakeSource(Merged);
        var state = new M.State();
        List<DeviceFetchMode> modes = [];
        foreach (var offset in new double[] { 0, 297, 572, 847 })
        {
            var decision = Decide(state, at: offset);
            modes.Add(decision.Call!.Mode);
            state = Run(decision, fake);
        }

        Assert.All(modes, mode => Assert.Equal(DeviceFetchMode.Refresh, mode));
        Assert.Equal(DeviceFetchMode.Cached, Decide(state, at: 847 + 60).Call!.Mode);
    }

    [Fact]
    public void TheDueBoundaryIsIntervalMinusMarginOnTheMonotonicClock()
    {
        // Review round 1 (3): 269 s cached, 270 s refresh (300 - 30 margin).
        var fake = new FakeSource(Merged);
        var started = Run(Decide(new M.State()), fake);
        var edge = 300 - M.DueMargin.TotalSeconds;
        Assert.Equal(DeviceFetchMode.Cached, Decide(started, at: edge - 1).Call!.Mode);
        Assert.Equal(DeviceFetchMode.Refresh, Decide(started, at: edge).Call!.Mode);
    }

    [Fact]
    public void AFailureKeepsTheLastMergedResultAndNeverSucceededIsThisPc()
    {
        // D1 (4), SelfTest.swift :12174-12185.
        var fake = new FakeSource(Merged);
        var state = Run(Decide(new M.State()), fake);
        fake.Fail = true;
        state = Run(Decide(state, at: 400), fake);
        var kept = Decide(state, at: 401);
        var never = Run(Decide(new M.State()), fake);
        var neverUsed = Decide(never, at: 1);
        Assert.Equal(Pay(Merged), Pay(kept.Graph));
        Assert.Equal(Pay(Local), Pay(neverUsed.Graph));
        Assert.False(state.InFlight);
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public void APeerChangeDropsTheOldResult()
    {
        // D1 (5), SelfTest.swift :12187-12195.
        var fake = new FakeSource(Merged);
        var before = Run(Decide(new M.State()), fake);
        var after = Decide(before, peers: [PeerA, PeerB], at: 1);
        Assert.NotEqual(before.Generation, after.State.Generation);
        Assert.Null(after.State.Merged);
        Assert.Equal(Pay(Local), Pay(after.Graph));
        Assert.Equal(Pay(Merged), Pay(Decide(before, at: 1).Graph));
    }

    [Fact]
    public void UntickingOrUnselectingRetiresAndAddingDoesNot()
    {
        // D1 (6), SelfTest.swift :12197-12208.
        var merged = Run(Decide(new M.State()), new FakeSource(Merged));
        var unticked = Decide(merged, on: false, at: 1);
        Assert.True(M.Visibility(true, [PeerA], false, [PeerA]).Retires);
        Assert.True(M.Visibility(true, [PeerA, PeerB], true, [PeerA]).Retires);
        Assert.False(M.Visibility(false, [PeerA], true, [PeerA]).Retires);
        Assert.False(M.Visibility(true, [PeerA], true, [PeerA, PeerB]).Retires);
        Assert.Equal(Pay(Local), Pay(unticked.Graph));
        Assert.NotEqual(Pay(Merged), Pay(unticked.Graph));
        Assert.Null(unticked.Call);
    }

    [Fact]
    public void IncludeDevicesIsAStrictBool()
    {
        // D1 (7), SelfTest.swift :12210-12225.
        var store = NewStore();
        var absent = DiscordPresence.IncludeDevices(store);
        store.SetString(DiscordPresence.IncludeDevicesKey, "true");
        var text = DiscordPresence.IncludeDevices(store);
        store.SetInt(DiscordPresence.IncludeDevicesKey, 1);
        var number = DiscordPresence.IncludeDevices(store);
        store.SetBool(DiscordPresence.IncludeDevicesKey, true);
        Assert.False(absent);
        Assert.False(text);
        Assert.False(number);
        Assert.True(DiscordPresence.IncludeDevices(store));
    }

    [Fact]
    public void TheCallIsThisPcPlusThePersistedPeersSortedDeduplicatedAndCapped()
    {
        // D1 (8), SelfTest.swift :12227-12242. The fixture's key-and-cap
        // equality is not ported: SelectedPeers reads DeviceOverlayState's own
        // constants, there is no copy to drift.
        var store = NewStore();
        store.SetString(DeviceOverlayState.SelectionKey, $$"""{"local":false,"peers":["{{PeerB}}","{{PeerA}}","{{PeerB}}"]}""");
        var persisted = M.SelectedPeers(store);
        var many = Enumerable.Range(0, 20).Select(i => i.ToString("x64")).ToList();
        store.SetString(DeviceOverlayState.SelectionKey, JsonSerializer.Serialize(new { local = true, peers = many }));
        var capped = M.SelectedPeers(store);
        var call = Decide(new M.State(), peers: persisted).Call!;
        Assert.Equal([PeerA, PeerB], persisted);
        Assert.True(call.Selection.Local);
        Assert.Equal(persisted, call.Selection.Peers);
        Assert.Equal(many.Take(16), capped);
        Assert.Equal(DeviceOverlayState.MaxSelectedPeers, capped.Count);
        // Missing or undecodable is none.
        store.SetString(DeviceOverlayState.SelectionKey, "{not json");
        Assert.Empty(M.SelectedPeers(store));
        store.SetString(DeviceOverlayState.SelectionKey, """{"local":true,"peers":null}""");
        Assert.Empty(M.SelectedPeers(store));
        Assert.Empty(M.SelectedPeers(NewStore()));
    }

    [Fact]
    public void AResultArrivingAfterTheBoxWasUntickedIsDropped()
    {
        // D1 (9), SelfTest.swift :12244-12256.
        var fake = new FakeSource(Merged);
        var started = Decide(new M.State());
        var unticked = Decide(started.State, on: false, at: 1);
        var landed = Run(new M.Decision(null, started.Call, unticked.State), fake);
        var reticked = Decide(landed, on: true, at: 2);
        Assert.NotNull(started.Call);
        Assert.Null(landed.Merged);
        Assert.False(landed.InFlight);
        Assert.Equal(Pay(Local), Pay(reticked.Graph));
        Assert.Single(fake.Calls);
        Assert.NotNull(Run(started, fake).Merged);
    }

    [Fact]
    public void ATickWhileACallIsInFlightIsSkipped()
    {
        // D1 (10), SelfTest.swift :12258-12281, without the thread: the
        // controller test below holds a real call in flight.
        var fake = new FakeSource(Merged);
        var started = Decide(new M.State());
        var during = Decide(started.State, at: 400);
        Assert.Equal(DeviceFetchMode.Refresh, started.Call!.Mode);
        Assert.Null(during.Call);
        Assert.Equal(Pay(Local), Pay(during.Graph));
        var landed = Run(started, fake);
        Assert.Single(fake.Calls);
        Assert.NotNull(Decide(landed, at: 401).Call);
    }

    [Fact]
    public void WhatTheOtherDeviceSendsCannotReachThePublicText()
    {
        // D1 (11), SelfTest.swift :12283-12314. Built by hand: an Int64.MaxValue
        // stripe would overflow the Graph helper's checked LINQ sums.
        ContributionClient[] stripes =
        [
            Stripe("cc-mirror/ZZSECRET", long.MaxValue, 1e308), Stripe("claude", 2_000, 2.0),
        ];
        var hostile = Merged with
        {
            Summary = Merged.Summary with { TotalTokens = long.MaxValue, TotalCost = 1e308, Clients = ["cc-mirror/ZZSECRET", "claude"] },
            Contributions =
            [
                new Contribution(Today, new ContributionTotals(long.MaxValue, 1e308, 2), 1,
                    new TokenBreakdown(0, 0, 0, 0, 0), stripes),
            ],
        };
        var state = Run(Decide(new M.State()), new FakeSource(hostile));
        var used = Decide(state, at: 1);
        DiscordPresence.Payload? Whole(UsagePayload graph) => DiscordPresence.Build(
            graph, new HashSet<string>(), Today, CostStyle.WholeDollars, DiscordPresence.DefaultComponents,
            new Selection.MostUsed());
        var published = Whole(used.Graph!);
        // 2,000 and $2: not this PC's 1,000 and $1, so a fallback to the local
        // graph cannot pass for the merged one.
        var allowed = Whole(Graph(Stripe("claude", 2_000, 2.0)));
        Assert.NotNull(state.Merged);
        Assert.Contains(state.Merged!.Contributions[0].Clients, c => c.Client.Contains("ZZSECRET"));
        Assert.NotNull(published);
        Assert.Equal(allowed, published);
        Assert.NotEqual(Whole(Local), published);
        Assert.DoesNotContain("ZZLABEL", state.ToString());
        Assert.DoesNotContain("ZZ", string.Join("|", published!.Fields.Values));
    }

    [Fact]
    public void ADeviceRemovedWhileTheFirstCallIsInFlightDropsTheLateResult()
    {
        // D1 (13), SelfTest.swift :12316-12329.
        var fake = new FakeSource(Merged);
        var started = Decide(new M.State());
        var removed = M.Invalidate(started.State);
        var landed = Run(new M.Decision(null, started.Call, removed), fake);
        Assert.NotNull(started.Call);
        Assert.Null(started.State.Merged);
        Assert.True(started.State.InFlight);
        Assert.Null(landed.Merged);
        Assert.False(landed.InFlight);
        Assert.NotNull(Run(started, fake).Merged);
    }

    [Fact]
    public void AtPublishTimeAHeldResultIsUsedOnlyWithEveryConditionStillTrue()
    {
        // D1 (14), SelfTest.swift :12331-12347.
        var held = Run(Decide(new M.State()), new FakeSource(Merged));
        DiscordPresence.Payload? Pick(bool include, bool connect, IReadOnlyList<string> peers) =>
            Pay(M.Graph(Local, held, include, connect, peers));
        Assert.NotNull(held.Merged);
        Assert.Equal(Pay(Merged), Pick(true, true, held.MergedPeers));
        Assert.Equal(Pay(Local), Pick(false, true, held.MergedPeers));
        Assert.Equal(Pay(Local), Pick(true, false, held.MergedPeers));
        Assert.Equal(Pay(Local), Pick(true, true, [.. held.MergedPeers, PeerB]));
        Assert.NotEqual(Pay(Local), Pay(Merged));
    }

    [Fact]
    public void TestArgumentsNeverPull()
    {
        // D1 (12), SelfTest.swift :12349-12356, with this port's test flag.
        var gated = DiscordPresence.MayConnect(["app", "--startup-smoke"], true);
        var ordinary = DiscordPresence.MayConnect(["app"], true);
        Assert.Null(Decide(new M.State(), connect: gated).Call);
        Assert.NotNull(Decide(new M.State(), connect: ordinary).Call);
    }

    [Fact]
    public void AnEqualButSeparatelyBuiltPeerListStillMatches()
    {
        // F9: compared by value; == on two lists would compare references and
        // the feature would silently do nothing.
        var call = new M.Call(DeviceFetchMode.Cached, new DeviceSelection(true, new List<string> { PeerA }), 0);
        var state = M.Arrived(new M.State() with { InFlight = true }, call, Merged);
        Assert.Same(Merged, M.Graph(Local, state, true, true, new List<string> { PeerA }));
        var observed = M.Observe(state with { IncludeDevices = true, DiscordOn = true, Peers = new List<string> { PeerA } },
            true, true, new List<string> { PeerA });
        Assert.Same(Merged, observed.Merged);
    }

    [Fact]
    public void ANonAuthoritativeMergedAnswerIsNeverStoredOrPublished()
    {
        // Windows (d), pure half: the cost-authority gate at Arrived and Graph.
        foreach (var (mode, coverage, authoritative) in new (PricingMode, CostCoverage, bool)[]
                 {
                     (PricingMode.BestEffort, CostCoverage.None, false),
                     (PricingMode.LocalOnly, CostCoverage.Complete, false),
                     (PricingMode.BestEffort, CostCoverage.Complete, true),
                     (PricingMode.BestEffort, CostCoverage.Partial, true),
                 })
        {
            var body = WithMeta(Merged, mode, coverage);
            var decision = Decide(new M.State());
            var arrived = M.Arrived(decision.State, decision.Call!, body);
            Assert.Equal(authoritative, arrived.Merged is not null);
            Assert.False(arrived.InFlight);
            var forced = decision.State with { Merged = body, MergedPeers = [PeerA] };
            Assert.Same(authoritative ? body : Local, M.Graph(Local, forced, true, true, [PeerA]));
        }
    }

    // ---- the controller --------------------------------------------------

    private sealed class Rig
    {
        public required DiscordPresenceController Controller { get; init; }
        public required FakeSource Source { get; init; }
        public required SettingsStore Store { get; init; }
        public readonly List<(DiscordPresence.Payload? Payload, VisibilityChange Change)> Published = [];
        public TimeSpan Now = T0;
        public bool FlyoutOpen;
        public int FlyoutReads;

        public (DiscordPresence.Payload? Payload, VisibilityChange Change) Last
        {
            get { lock (Published) { return Published[^1]; } }
        }

        public int Count
        {
            get { lock (Published) { return Published.Count; } }
        }

        public async Task CallDone()
        {
            Assert.NotNull(Controller.MergeCallForTesting);
            await Controller.MergeCallForTesting!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(Source.Years, Assert.Null);
            Assert.False(Source.HoldTimedOut, "a held call was never released");
        }

        /// <summary>The payloads published after the first
        /// <paramref name="count"/>, in order.</summary>
        public IReadOnlyList<DiscordPresence.Payload?> Since(int count)
        {
            lock (Published) { return [.. Published.Skip(count).Select(p => p.Payload)]; }
        }

        public string Today = DiscordDevicesTests.Today;

        public void Write(string key, Action<SettingsStore> write)
        {
            write(Store);
            Controller.OnSettingChanged(key);
        }

        public void SelectPeers(params string[] peers) => Write(
            DeviceOverlayState.SelectionKey,
            s => s.SetString(DeviceOverlayState.SelectionKey, JsonSerializer.Serialize(new { local = true, peers })));
    }

    private Rig NewRig(UsagePayload body, bool includeDevices = true, string[]? peers = null)
    {
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        if (includeDevices)
        {
            store.SetBool(DiscordPresence.IncludeDevicesKey, true);
        }

        store.SetString(
            DeviceOverlayState.SelectionKey,
            JsonSerializer.Serialize(new { local = true, peers = peers ?? [PeerA] }));
        var source = new FakeSource(body);
        Rig? rig = null;
        var controller = new DiscordPresenceController(
            store, ["app"],
            // No pipe: every connection attempt fails at once.
            () => new DiscordIpcClient(
                (_, _) => throw new IOException("Discord is not running"),
                new DiscordIpcClient.Timing(
                    TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))),
            () => rig!.Today, source,
            () =>
            {
                rig!.FlyoutReads++;
                return rig.FlyoutOpen;
            },
            () => rig!.Now);
        rig = new Rig { Controller = controller, Source = source, Store = store };
        controller.PublishedForTesting = (payload, change) =>
        {
            lock (rig.Published) { rig.Published.Add((payload, change)); }
        };
        controller.Launch();
        return rig;
    }

    /// <summary>A fresh instance of this PC's graph, so OnGraph accepts it.</summary>
    private static UsagePayload LocalTick() => Graph(Stripe("claude", 1_000, 1.0));

    private async Task<Rig> MergedRig(UsagePayload? body = null)
    {
        var rig = NewRig(body ?? Merged);
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal(Pay(Merged), rig.Last.Payload);
        return rig;
    }

    [Fact]
    public void OffPublishesThisPcAndNeverCallsTheDevices()
    {
        // D1 (1) through the controller; and on with no peer selected, the same.
        foreach (var rig in new[] { NewRig(Merged, includeDevices: false), NewRig(Merged, peers: []) })
        {
            rig.Controller.OnGraph(LocalTick());
            Assert.Equal(Pay(Local), rig.Last.Payload);
            Assert.Null(rig.Controller.MergeCallForTesting);
            Assert.Empty(rig.Source.Calls);
        }
    }

    [Fact]
    public async Task OnPublishesThisPcThenTheMergedGraphFromOneCall()
    {
        // D1 (2) through the controller: this PC's graph at once, the merged
        // one when the call lands, as an addition (no retire).
        // The worker can publish before OnGraph's own publish is read, so the
        // order is asserted after the call has landed.
        var rig = NewRig(Merged);
        var before = rig.Count;
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal([Pay(Local), Pay(Merged)], rig.Since(before));
        Assert.False(rig.Last.Change.Retires);
        var (selection, mode) = Assert.Single(rig.Source.Calls);
        Assert.True(selection.Local);
        Assert.Equal([PeerA], selection.Peers);
        Assert.Equal(DeviceFetchMode.Refresh, mode);
    }

    [Fact]
    public async Task ASelectionWriteThatDropsAPeerRepublishesThisPcAndRetires()
    {
        // Windows (a).
        var rig = await MergedRig();
        var before = rig.Count;
        rig.SelectPeers(PeerB);
        Assert.Equal(before + 1, rig.Count);
        Assert.Equal(Pay(Local), rig.Last.Payload);
        Assert.True(rig.Last.Change.Retires);
        Assert.Single(rig.Source.Calls); // a write never starts a call
    }

    [Fact]
    public async Task HidingAClientOnlyInTheMergedGraphIsReducing()
    {
        // Windows (b): codex has usage only on the other device.
        var rig = await MergedRig();
        rig.Write(ClientRegistry.TabHiddenKey, s => s.SetString(ClientRegistry.TabHiddenKey, "codex"));
        Assert.True(rig.Last.Change.Retires);

        // Control: the same hide with the merge off reads as no change.
        var control = NewRig(Merged, includeDevices: false);
        control.Controller.OnGraph(LocalTick());
        control.Write(ClientRegistry.TabHiddenKey, s => s.SetString(ClientRegistry.TabHiddenKey, "codex"));
        Assert.Equal(2, control.Count);
        Assert.False(control.Last.Change.Retires);
    }

    [Fact]
    public async Task ANonAuthoritativeMergedAnswerNeverReachesThePublishedPayload()
    {
        // Windows (d) through the controller; control: BestEffort with
        // Complete coverage does.
        foreach (var (mode, coverage, authoritative) in new (PricingMode, CostCoverage, bool)[]
                 {
                     (PricingMode.BestEffort, CostCoverage.None, false),
                     (PricingMode.LocalOnly, CostCoverage.Complete, false),
                     (PricingMode.BestEffort, CostCoverage.Complete, true),
                 })
        {
            var rig = NewRig(WithMeta(Merged, mode, coverage));
            rig.Controller.OnGraph(LocalTick());
            await rig.CallDone();
            Assert.Equal(authoritative ? Pay(Merged) : Pay(Local), rig.Last.Payload);
            lock (rig.Published)
            {
                Assert.Equal(authoritative, rig.Published.Any(p => p.Payload == Pay(Merged)));
            }
        }
    }

    [Fact]
    public async Task APeerChangeDuringAnInFlightCallDropsTheLateResult()
    {
        // Windows (e), and D1 (10) with a real call held in flight.
        var rig = NewRig(Merged);
        using var hold = new SemaphoreSlim(0);
        rig.Source.Hold = hold;
        rig.Controller.OnGraph(LocalTick());
        await rig.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A tick while it is held is skipped, and this PC's graph publishes.
        rig.Now = At(400);
        var inFlight = rig.Controller.MergeCallForTesting;
        rig.Controller.OnGraph(LocalTick());
        Assert.Same(inFlight, rig.Controller.MergeCallForTesting);
        Assert.Equal(Pay(Local), rig.Last.Payload);

        rig.Controller.OnPeersChanged();
        Assert.Equal(Pay(Local), rig.Last.Payload);
        Assert.True(rig.Last.Change.Retires);
        var afterRemoval = rig.Count;

        hold.Release();
        await rig.CallDone();
        lock (rig.Published)
        {
            Assert.All(rig.Published.Skip(afterRemoval), p => Assert.Equal(Pay(Local), p.Payload));
        }

        Assert.Single(rig.Source.Calls);
        // Control: no longer in flight, the next tick calls again.
        rig.Source.Hold = null;
        rig.Now = At(401);
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal(2, rig.Source.Calls.Count);
        Assert.Equal(Pay(Merged), rig.Last.Payload);
    }

    [Fact]
    public async Task AThrowingSourceIsAFailedCallAndALaterTickCallsAgain()
    {
        // F2: caught, in flight cleared, last result kept, nothing unobserved.
        var rig = await MergedRig();
        rig.Source.Fail = true;
        rig.Now = At(300);
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone(); // would rethrow an uncaught exception
        Assert.Equal(Pay(Merged), rig.Last.Payload);
        rig.Source.Fail = false;
        rig.Now = At(360);
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal(3, rig.Source.Calls.Count);
    }

    [Fact]
    public async Task SettingsWritesAndPeerChangesNeverDecideOrReadTheFlyout()
    {
        // F3.
        var rig = await MergedRig();
        Assert.Equal(1, rig.FlyoutReads);
        rig.SelectPeers(PeerA, PeerB);
        rig.Write(DiscordPresence.IncludeDevicesKey, s => s.SetBool(DiscordPresence.IncludeDevicesKey, false));
        rig.Controller.OnPeersChanged();
        Assert.Equal(1, rig.FlyoutReads);
        Assert.Single(rig.Source.Calls);
    }

    [Fact]
    public async Task GraphsAFewSecondsApartRefreshOnce()
    {
        // F6: LastRefresh is stamped when refresh is issued; each call is
        // awaited, so "in flight" cannot be what skips the later ones.
        var rig = NewRig(Merged);
        foreach (var at in new double[] { 0, 5, 10 })
        {
            rig.Now = At(at);
            rig.Controller.OnGraph(LocalTick());
            await rig.CallDone();
        }

        Assert.Equal(
            [DeviceFetchMode.Refresh, DeviceFetchMode.Cached, DeviceFetchMode.Cached],
            rig.Source.Calls.Select(c => c.Mode));

        // The injected flyout reaches the decision: due, but open, is cached;
        // closed again, the same moment refreshes.
        rig.FlyoutOpen = true;
        rig.Now = At(400);
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        rig.FlyoutOpen = false;
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal(
            [DeviceFetchMode.Cached, DeviceFetchMode.Refresh],
            rig.Source.Calls.Skip(3).Select(c => c.Mode));
    }

    [Fact]
    public async Task TheMainSwitchOffAndOnDropsTheMergedResult()
    {
        // F10: until a new merged result arrives, this PC's graph.
        var rig = await MergedRig();
        rig.Write(DiscordPresence.EnabledKey, s => s.SetBool(DiscordPresence.EnabledKey, false));
        rig.Write(DiscordPresence.EnabledKey, s => s.SetBool(DiscordPresence.EnabledKey, true));
        Assert.Equal(Pay(Local), rig.Last.Payload);
        rig.Now = At(60);
        var before = rig.Count;
        rig.Controller.OnGraph(LocalTick());
        await rig.CallDone();
        Assert.Equal([Pay(Local), Pay(Merged)], rig.Since(before));
    }

    [Fact]
    public async Task ACallCompletingAfterQuitNeitherPublishesNorStartsAnother()
    {
        // Review round 1 (1): Quit is terminal.
        var rig = NewRig(Merged);
        using var hold = new SemaphoreSlim(0);
        rig.Source.Hold = hold;
        rig.Controller.OnGraph(LocalTick());
        await rig.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Controller.Quit(TimeSpan.FromMilliseconds(300));
        var atQuit = rig.Count;

        hold.Release();
        await rig.CallDone();
        rig.Source.Hold = null;
        rig.Now = At(600);
        rig.Controller.OnGraph(LocalTick());
        rig.Write(DiscordPresence.WholeDollarsKey, s => s.SetBool(DiscordPresence.WholeDollarsKey, true));
        rig.Controller.OnPeersChanged();

        Assert.Empty(rig.Since(atQuit));
        Assert.Single(rig.Source.Calls);

        // Control: an ordinary off/on (no Quit) still publishes again.
        var control = await MergedRig();
        control.Write(DiscordPresence.EnabledKey, s => s.SetBool(DiscordPresence.EnabledKey, false));
        var off = control.Count;
        control.Write(DiscordPresence.EnabledKey, s => s.SetBool(DiscordPresence.EnabledKey, true));
        Assert.Equal([Pay(Local)], control.Since(off));
    }

    [Fact]
    public async Task AnUnpricedLocalGraphStillLetsTheMergeAskForItsOwnGraph()
    {
        // Review round 1 (2): this PC's graph has no priced message
        // (CostCoverage.None), the merged answer is Partial: the merged
        // payload publishes; this PC's never does on its own.
        var unpriced = WithMeta(LocalTick(), PricingMode.BestEffort, CostCoverage.None);
        var partial = WithMeta(Merged, PricingMode.BestEffort, CostCoverage.Partial);
        var rig = NewRig(partial);
        rig.Controller.OnGraph(unpriced);
        await rig.CallDone();
        Assert.NotNull(Pay(partial));
        Assert.Equal([Pay(partial)], rig.Since(0));

        // The same instance again (a trace tick) asks for nothing.
        var call = rig.Controller.MergeCallForTesting;
        rig.Controller.OnGraph(unpriced);
        Assert.Same(call, rig.Controller.MergeCallForTesting);
        Assert.Single(rig.Source.Calls);

        // Control: merge off, nothing is published and nothing is called.
        var control = NewRig(partial, includeDevices: false);
        control.Controller.OnGraph(WithMeta(LocalTick(), PricingMode.BestEffort, CostCoverage.None));
        Assert.Equal(0, control.Count);
        Assert.Empty(control.Source.Calls);
    }

    [Fact]
    public async Task TheCadenceIgnoresTheCalendarClock()
    {
        // Review round 1 (3): the interval runs on the monotonic seam only.
        // A calendar day moved back (clock set back) or forward neither makes
        // a tick due nor stops a due one.
        var rig = await MergedRig();
        Assert.Equal(DeviceFetchMode.Refresh, rig.Source.Calls[0].Mode);
        foreach (var (today, at, expected) in new (string, double, DeviceFetchMode)[]
                 {
                     ("2026-09-24", 60, DeviceFetchMode.Cached), // set back a day
                     ("2026-12-31", 120, DeviceFetchMode.Cached), // set forward
                     ("2025-01-01", 300, DeviceFetchMode.Refresh), // back, yet due
                 })
        {
            rig.Today = today;
            rig.Now = At(at);
            rig.Controller.OnGraph(LocalTick());
            await rig.CallDone();
            Assert.Equal(expected, rig.Source.Calls[^1].Mode);
        }

        Assert.Equal(4, rig.Source.Calls.Count);
    }

    [Fact]
    public async Task NothingButDiscordCanReceiveTheMergedGraph()
    {
        // D1 (8)'s "no tray graph in the output". The reflection half is a
        // shape check, not a guarantee: it sees members typed UsagePayload, not
        // a Task<UsagePayload>, a delegate or an event that could carry one.
        // The graph handed in is left as it was.
        var given = LocalTick();
        var rig = NewRig(Merged);
        rig.Controller.OnGraph(given);
        await rig.CallDone();
        Assert.Equal(Pay(Merged), rig.Last.Payload);
        Assert.Equal(Pay(Local), Pay(given));
        var exposed = typeof(DiscordPresenceController)
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m switch
            {
                PropertyInfo p => !p.GetMethod!.IsPrivate,
                MethodInfo method => !method.IsPrivate && !method.IsSpecialName,
                FieldInfo f => !f.IsPrivate,
                _ => false,
            })
            .Where(m => m switch
            {
                PropertyInfo p => p.PropertyType == typeof(UsagePayload),
                MethodInfo method => method.ReturnType == typeof(UsagePayload),
                FieldInfo f => f.FieldType == typeof(UsagePayload),
                _ => false,
            });
        Assert.Empty(exposed);
    }

    // ---- copy ------------------------------------------------------------

    [Fact]
    public void BothNewStringsAreTheMacOsTextInBothTables()
    {
        // macOS SettingsPanel.swift :1019-1020; Localizable.strings zh-Hant
        // :275-276, zh-Hans :274-275. Read from the shipped tables.
        Assert.Equal("Include other devices selected in the dashboard", DiscordCopy.IncludeDevices);
        Assert.Equal("Other devices' usage for today updates about every 5 minutes.", DiscordCopy.IncludeDevicesHint);
        foreach (var (file, toggle, hint) in new[]
                 {
                     ("strings-zh-Hant.json", "計入儀表板選的其他裝置", "其他裝置的今日用量約每 5 分鐘更新一次。"),
                     ("strings-zh-Hans.json", "计入仪表板所选的其他设备", "其他设备的今日用量约每 5 分钟更新一次。"),
                 })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            Assert.Equal(toggle, table[DiscordCopy.IncludeDevices]);
            Assert.Equal(hint, table[DiscordCopy.IncludeDevicesHint]);
        }
    }
}
