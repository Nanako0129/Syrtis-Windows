using System.Collections.Concurrent;
using System.Text.Json;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using C = TokenBar.Core.DeviceOverlayCopy;

namespace TokenBar.Core.Tests;

/// <summary>The dashboard's device wiring (SA-4g) that does not need WinUI:
/// when the peer list is read, when the overlay is asked for pages, which
/// clients it is asked for, what each lens shows, the refresh control's rule
/// and the remote-string rule. Everything runs against
/// <see cref="FakeDeviceSource"/> and a temporary <see cref="SettingsStore"/>;
/// never <c>AppSettings.Store</c>, <c>TbDeviceSource</c> or <c>TbCore</c>.
/// DashboardView itself compiles under no test project: its draws are covered
/// by the 188 screenshot round.</summary>
public class DashboardDeviceLensTests : IDisposable
{
    private const string PeerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Evil = "‮{0}\nname";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));
    private readonly List<DeviceOverlayState> _made = [];
    private readonly List<FakeDeviceSource> _sources = [];
    private int _stores;

    public DashboardDeviceLensTests()
    {
        DeviceOverlayTestSupport.Load("en");
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
        Pump(() => !DeviceOverlayState.RefreshInFlightForTesting && !DeviceOverlayState.WritesPendingForTesting
            && _made.All(o => !o.Fetching));
        DeviceOverlayState.ResetSingleFlightForTesting();
        DeviceOverlayTestSupport.Load("en");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- helpers -----------------------------------------------------------

    private static RemotePeer Peer(string? label = null) =>
        new(ProducerStatic: PeerA, Fingerprint: PeerA[..16], Host: "10.0.0.2", Port: 47000,
            CreatedAt: 1, ExpiresAt: 4102444800000, State: "active", NodeLabel: label);

    private static bool Pump(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) { Thread.Sleep(2); }
        return condition();
    }

    private static void Quiet() => Thread.Sleep(150);

    private static bool Idle(DeviceOverlayState overlay) =>
        Pump(() => !overlay.Fetching && !DeviceOverlayState.RefreshInFlightForTesting);

    /// <summary>seeded: the peer has been offered before, so it is listed but
    /// not checked on arrival; unseeded: a first appearance is auto-checked.</summary>
    private SettingsStore NewStore(bool seeded = true)
    {
        var store = new SettingsStore(Path.Combine(_dir, $"settings-{_stores++}.json"));
        if (seeded)
        {
            store.SetString(DeviceOverlayState.SeenPeersKey, JsonSerializer.Serialize(new[] { PeerA }));
        }
        return store;
    }

    private (DeviceOverlayState Overlay, FakeDeviceSource Source, FakeDeviceClock Clock) New(
        bool seeded = true, params RemotePeer[] peers)
    {
        var source = new FakeDeviceSource();
        source.Set(s => s.Peers = peers);
        _sources.Add(source);
        var clock = new FakeDeviceClock();
        var overlay = new DeviceOverlayState(source, NewStore(seeded), clock);
        _made.Add(overlay);
        return (overlay, source, clock);
    }

    /// <summary>One listed device, chosen beside this PC, the flyout open.</summary>
    private (DeviceOverlayState Overlay, FakeDeviceSource Source, FakeDeviceClock Clock, DeviceOverlaySync Sync) Open(
        params RemotePeer[] peers)
    {
        var (overlay, source, clock) = New(true, peers.Length == 0 ? [Peer()] : peers);
        var sync = new DeviceOverlaySync(overlay);
        sync.Opened();
        Assert.True(Pump(() => overlay.SelectablePeers.Count == 1));
        overlay.TogglePeer(PeerA);
        DeviceOverlayState.ResetSingleFlightForTesting();
        return (overlay, source, clock, sync);
    }

    /// <summary>This PC's own payload, shaped as GraphSnapshotStoreTests builds
    /// one (the store validates what it writes).</summary>
    private static UsagePayload LocalPayload()
    {
        const string date = "2026-01-02";
        var tokens = new TokenBreakdown(1, 2, 3, 4, 5);
        var client = new ContributionClient("claude", "local-model", "p", tokens, 0.25, 1);
        return new UsagePayload(
            new UsageMeta("g", "v", new DateRange(date, date), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(1, 0.25, 1, 1, 1, 0.25, ["claude"], ["local-model"]),
            [new YearMeta("2026", 1, 0.25, new DateRange(date, date))],
            [new Contribution(date, new ContributionTotals(1, 0.25, 1), -1, tokens, [client], null)]);
    }

    // ---- peers --------------------------------------------------------------

    [Fact] // the peer list is read on open and on every slow tick; one active device is checked
    public void PeersAreReadOnOpenAndOnEachTickAndOneActivePeerIsAutoChecked()
    {
        var (overlay, source, _) = New(seeded: false, Peer());
        var sync = new DeviceOverlaySync(overlay);
        Assert.Equal(0, source.Get(s => s.PeerCalls));
        sync.Opened();
        Assert.True(Pump(() => source.Get(s => s.PeerCalls) == 1 && overlay.SelectablePeers.Count == 1));
        Assert.True(overlay.IsSelected(PeerA)); // first appearance: checked
        Assert.Equal(1, overlay.SelectablePeers.Count);
        sync.SlowTick();
        sync.SlowTick();
        Assert.True(Pump(() => source.Get(s => s.PeerCalls) == 3));
        sync.Closed();
        sync.SlowTick(); // closed: nothing asks
        Quiet();
        Assert.Equal(3, source.Get(s => s.PeerCalls));
        sync.Opened(); // reopened: read again
        Assert.True(Pump(() => source.Get(s => s.PeerCalls) == 4));
    }

    // ---- ensure, deduplicated -------------------------------------------------

    [Fact] // a toggle on the open lens reaches the core without a lens change
    public void TogglingAPeerOnAnOpenLensIssuesDeviceCallsWithoutALensChange()
    {
        var (overlay, source, _) = New(true, Peer());
        var sync = new DeviceOverlaySync(overlay);
        sync.Opened();
        Assert.True(Pump(() => overlay.SelectablePeers.Count == 1));
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Quiet();
        Assert.Empty(source.Calls); // only this PC: no device call
        overlay.TogglePeer(PeerA);
        Assert.True(sync.Sync(AppView.Daily, null, null, "")); // same lens, year and clients: the selection is in the key
        Assert.True(Pump(() => source.Calls.Count == 2) && Idle(overlay));
        Assert.Equal(["graph-cached", "graph-refresh"], source.Calls.Select(c => c.Tag));
    }

    [Fact] // 50 changes while a key's answer is held back: one cached call, not 50
    public void FiftyChangesIssueExactlyOneCachedCallPerKey()
    {
        var (overlay, source, _, sync) = Open();
        source.Set(s => s.HoldCached = true);
        // The view's handler: every Changed asks the sync, which must not ensure again.
        overlay.Changed += () => sync.Sync(AppView.Daily, null, null, "");
        for (var i = 0; i < 50; i++) { sync.Sync(AppView.Daily, null, null, ""); }
        Assert.True(Pump(() => source.Calls.Count(c => c.Mode == DeviceFetchMode.Cached) >= 1));
        Quiet();
        Assert.Equal(1, source.Calls.Count(c => c.Mode == DeviceFetchMode.Cached));
        Assert.Equal(1, sync.EnsureCalls);
        source.Set(s => s.HoldCached = false);
        source.ReleaseCached(50);
        Assert.True(Idle(overlay));
        Assert.Equal(1, source.Calls.Count(c => c.Mode == DeviceFetchMode.Cached));
    }

    [Fact] // show, hide, show on the same lens ensures twice
    public void ShowHideShowOnTheSameLensEnsuresTwice()
    {
        var (overlay, source, clock, sync) = Open();
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Assert.True(Idle(overlay));
        sync.Closed();
        Assert.False(sync.Sync(AppView.Daily, null, null, "")); // closed: nothing is asked
        // Closed told the overlay nothing is on screen: a poll or a manual refresh
        // that still arrives makes no device call.
        var before = source.Calls.Count;
        overlay.PollOnce();
        overlay.ManualRefresh();
        Quiet();
        Assert.Equal(before, source.Calls.Count);
        clock.Advance(DeviceOverlayState.FreshWindow);
        sync.Opened();
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Idle(overlay));
        Assert.Equal(2, sync.EnsureCalls);
    }

    [Fact] // a year the model cleared is a new key
    public void AYearClearedByTheModelReachesEnsure()
    {
        var (overlay, source, _, sync) = Open();
        Assert.True(sync.Sync(AppView.Daily, "2024", null, ""));
        Assert.True(Idle(overlay));
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Assert.True(Idle(overlay));
        Assert.Contains(source.Calls, c => c.Year == "2024");
        Assert.Contains(source.Calls, c => c.Year is null);
        Assert.Equal(2, sync.EnsureCalls);
    }

    // ---- the clients argument ---------------------------------------------------

    [Fact] // Hourly asks for the registry minus the hidden tab, a remote-only client included
    public void HourlyAsksForTheRegistryMinusTheHiddenGroupAndShowsARemoteOnlyClient()
    {
        var (overlay, source, _, sync) = Open();
        Assert.True(sync.Sync(AppView.Hourly, null, null, "claude"));
        Assert.True(Pump(() => source.Calls.Count == 2) && Idle(overlay));
        var hidden = ClientRegistry.HiddenTabClients(ClientRegistry.ParseIdSet("claude"));
        var expected = string.Join(",", ClientRegistry.AllIds.Where(id => !hidden.Contains(id)).Order(StringComparer.Ordinal));
        Assert.All(source.Calls, c => Assert.Equal(expected, c.Clients));
        Assert.Contains("codex", expected.Split(',')); // not this PC's present clients: a remote-only one is asked for
        Assert.DoesNotContain("claude", expected.Split(','));
        var shown = overlay.DisplayClients(DeviceOverlayKind.Hourly, null, "claude", "");
        Assert.Contains(FakeDeviceSource.RemoteClient, shown);
        Assert.DoesNotContain("claude", shown);
    }

    // ---- the refresh control ----------------------------------------------------

    [Theory]
    [InlineData(false, true, true, true)] // overlay fetching only: spinning, still clickable
    [InlineData(true, false, true, false)] // a local refresh: spinning, disabled
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    public void TheRefreshControlSpinsForTheOverlayButOnlyALocalRefreshDisablesIt(
        bool localBusy, bool fetching, bool spinning, bool enabled) =>
        Assert.Equal((spinning, enabled), DashboardDeviceLens.RefreshControl(localBusy, fetching));

    // ---- this PC only -----------------------------------------------------------

    [Theory]
    [InlineData(false)] // a device is listed but not chosen
    [InlineData(true)] // no device is listed at all
    public void OnlyThisPcMakesNoDeviceCallAndShowsNothingOfDevices(bool noPeers)
    {
        var (overlay, source, _) = noPeers ? New(true) : New(true, Peer());
        var sync = new DeviceOverlaySync(overlay);
        sync.Opened();
        Assert.True(Pump(() => source.Get(s => s.PeerCalls) >= 1)); // the control: the list is read
        if (!noPeers) { Assert.True(Pump(() => overlay.SelectablePeers.Count == 1)); }
        foreach (var view in Enum.GetValues<AppView>())
        {
            sync.Sync(view, null, null, "");
            overlay.PollOnce();
            overlay.ManualRefresh();
        }
        Quiet();
        Assert.Empty(source.Calls); // no Graph/ModelReport/Hourly/Agents Devices call at all
        Assert.False(overlay.IncludesRemote);
        Assert.Null(overlay.OverviewMergeFor(null, null, "", "", new ModelColorMap((ModelReport?)null)));
        Assert.Empty(overlay.StatusLines(AppView.Daily, null, null));
        Assert.Equal("", DashboardDeviceLens.LiveRateSuffix(overlay));
        var notes = DashboardDeviceLens.QuotaNotes(overlay);
        Assert.Null(notes.Summary);
        Assert.Null(notes.Corner);
        Assert.Null(notes.QuotaLens);
        var local = LocalPayload();
        foreach (var view in Enum.GetValues<AppView>())
        {
            var choice = DashboardDeviceLens.Choose(
                local, view, overlay, null, null, "", "", () => new ModelColorMap((ModelReport?)null));
            Assert.Equal(LensSource.Local, choice.Source);
            Assert.Same(local, choice.Local);
            Assert.Null(choice.Merge);
        }
    }

    [Fact] // the menu appears only with a selectable device
    public void TheMenuNeedsASelectableDevice()
    {
        var (none, _, _) = New(true);
        Assert.Null(DashboardDeviceLens.Menu(none, DeviceOverlayTestSupport.Now));
        Assert.Null(DashboardDeviceLens.Menu(null, DeviceOverlayTestSupport.Now));
        var (overlay, _, _, _) = Open();
        var menu = DashboardDeviceLens.Menu(overlay, DeviceOverlayTestSupport.Now)!;
        Assert.Equal(2, menu.Rows.Count);
        Assert.Null(menu.Rows[0].Peer); // this PC first
        Assert.Equal(PeerA, menu.Rows[1].Peer);
        Assert.All(menu.Rows, r => Assert.True(r.Checked && r.CanToggle));
        Assert.Equal(C.DeviceCount.Localized(2), menu.Title);
    }

    // ---- isolation (SA3D (3), Windows map) ----------------------------------------

    private static bool HoldsNoRemote(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return !json.Contains(FakeDeviceSource.RemoteClient) && !json.Contains(FakeDeviceSource.RemoteModel)
            && !json.Contains($"\"{FakeDeviceSource.RemoteYear}");
    }

    [Fact]
    public async Task AnOverlayWithARemoteOnlyClientYearAndModelLeavesThisPcsDataAlone()
    {
        var (overlay, source, _, sync) = Open();
        var local = LocalPayload();
        // This PC's own pipeline: the coordinator's publication, the snapshot
        // file and the local-record set, filled the way the model fills them.
        var published = new ConcurrentQueue<GraphPublication>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(_ => local, _ => local);
        coordinator.Published += published.Enqueue;
        coordinator.Completed += _ => completed.TrySetResult();
        var store = NewStore();
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "graph-v1.json");
        var snapshots = new GraphSnapshotStore(file);

        Assert.True(sync.Sync(AppView.Stats, null, null, ""));
        coordinator.Request(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Idle(overlay));
        LocalRecordClients.Record(store, local.Summary.Clients);
        Assert.Equal(GraphSnapshotWriteStatus.Written, snapshots.Write("ctx", null, DateTimeOffset.UtcNow, local));

        // The overlay does hold the remote-only client, year and model (the control).
        Assert.Contains(FakeDeviceSource.RemoteClient, overlay.Graph!.Data!.Payload.Summary.Clients);
        Assert.Contains(overlay.Graph.Data.Payload.Years, y => y.Year == FakeDeviceSource.RemoteYear);
        Assert.Contains(overlay.Models!.Data!.Payload.Entries, e => e.Model.StartsWith(FakeDeviceSource.RemoteModel));

        Assert.NotEmpty(published);
        Assert.All(published, p => Assert.True(HoldsNoRemote(p.Payload)));
        Assert.DoesNotContain(FakeDeviceSource.RemoteClient, File.ReadAllText(file));
        Assert.DoesNotContain(FakeDeviceSource.RemoteModel, File.ReadAllText(file));
        Assert.True(snapshots.Read("ctx", null).Payload is { } read && HoldsNoRemote(read));
        Assert.DoesNotContain(FakeDeviceSource.RemoteClient, store.GetString(LocalRecordClients.Key));
        Assert.True(HoldsNoRemote(local));
        Assert.Equal(1, source.Calls.Count(c => c.Kind == "graph" && c.Mode == DeviceFetchMode.Refresh));

        // The lens choice: the overlay's page beside the local object, never in it.
        foreach (var view in new[] { AppView.Daily, AppView.Monthly, AppView.Stats })
        {
            var choice = DashboardDeviceLens.Choose(
                local, view, overlay, null, null, "", "", () => new ModelColorMap((ModelReport?)null));
            Assert.Equal(LensSource.Overlay, choice.Source);
            Assert.Null(choice.Local); // not carried in the choice
            Assert.NotSame(local, choice.Graph);
            Assert.Contains(FakeDeviceSource.RemoteClient, choice.Graph!.Summary.Clients);
            Assert.True(HoldsNoRemote(local)); // and the local object is untouched
        }
    }

    // ---- review round 1 -------------------------------------------------------------

    private static UsagePayload Priced(PricingMode mode, CostCoverage coverage) =>
        LocalPayload() with { Meta = LocalPayload().Meta with { PricingMode = mode, CostCoverage = coverage } };

    [Fact] // the per-device card's cost is behind the cost gate like every other cost
    public void ThePerDeviceCostIsBehindTheCostGate()
    {
        var row = new DeviceTotalsRow("d", "name", false, 3, 1_000, 1.5, null);
        Assert.Equal(CostSurfaceProjection.Checking, DashboardDeviceLens.DeviceCostText(row, authoritative: false));
        Assert.Equal(Format.Money(1_000, 1.5), DashboardDeviceLens.DeviceCostText(row, authoritative: true));
        Assert.Equal("—", DashboardDeviceLens.DeviceCostText(row with { Cost = null }, authoritative: false));
        Assert.NotEqual(Format.Usd(1.5), DashboardDeviceLens.DeviceCostText(row, authoritative: false));
    }

    [Fact] // authority is read from a graph page's pricing meta, never defaulted to true
    public void CostAuthorityComesFromAGraphPageAndNeverDefaultsToTrue()
    {
        var best = Priced(PricingMode.BestEffort, CostCoverage.Complete);
        var none = Priced(PricingMode.BestEffort, CostCoverage.None);
        var local = Priced(PricingMode.LocalOnly, CostCoverage.Complete);
        Assert.True(DashboardDeviceLens.Authoritative(best, null, null, localAuthoritative: false));
        Assert.False(DashboardDeviceLens.Authoritative(none, null, null, localAuthoritative: true));
        Assert.False(DashboardDeviceLens.Authoritative(local, null, null, localAuthoritative: true));

        // A lens without a graph (Models, Hourly, Agents) reads the graph page the
        // overlay holds for the same selection; the fake's pages are LocalOnly.
        var (overlay, _, _, sync) = Open();
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Assert.True(Idle(overlay));
        Assert.False(DashboardDeviceLens.Authoritative(null, overlay, null, localAuthoritative: true));
        // Another year has no held page: this PC's answer, whichever it is.
        Assert.False(DashboardDeviceLens.Authoritative(null, overlay, "1990", localAuthoritative: false));
        Assert.True(DashboardDeviceLens.Authoritative(null, overlay, "1990", localAuthoritative: true));
        Assert.False(DashboardDeviceLens.Authoritative(null, null, null, localAuthoritative: false));
    }

    [Fact] // a scan-roots change is a new key and asks again inside the 60 s window
    public void ARootsGenerationChangeEnsuresAgainAndRefreshesInsideTheFreshWindow()
    {
        var (overlay, source, _, sync) = Open();
        Assert.True(sync.Sync(AppView.Daily, null, null, "", rootsGeneration: 0));
        Assert.True(Pump(() => source.Refreshes.Count == 1) && Idle(overlay));
        Assert.False(sync.Sync(AppView.Daily, null, null, "", rootsGeneration: 0)); // same generation: deduplicated
        Assert.True(sync.Sync(AppView.Daily, null, null, "", rootsGeneration: 1));
        Assert.True(Pump(() => source.Refreshes.Count == 2) && Idle(overlay)); // no clock advance: not the 60 s window
        Assert.Equal(2, sync.EnsureCalls);
    }

    [Fact] // a failed models fetch with no models page keeps nothing: no "last data" line
    public void StatsWithAFailedModelsFetchAndNoModelsPageDoesNotClaimToShowLastData()
    {
        var (overlay, source, clock, sync) = Open();
        source.Set(s => s.FailModels = true);
        Assert.True(sync.Sync(AppView.Stats, null, null, ""));
        Assert.True(Pump(() => overlay.Models?.Failed == true) && Idle(overlay));
        var local = LocalPayload();
        var choice = DashboardDeviceLens.Choose(
            local, AppView.Stats, overlay, null, null, "", "", () => new ModelColorMap((ModelReport?)null));
        Assert.Equal(LensSource.Overlay, choice.Source);
        Assert.NotNull(choice.ModelsNote); // the fixed "model details aren't available" line instead
        Assert.DoesNotContain(C.KeptLast.Localized(), choice.NoteLines);

        // With a models page held and a later models refresh failing, it is kept data.
        source.Set(s => s.FailModels = false);
        clock.Advance(DeviceOverlayState.FreshWindow);
        overlay.ManualRefresh();
        Assert.True(Pump(() => overlay.Models?.Data is not null) && Idle(overlay));
        source.Set(s => s.FailModels = true);
        overlay.ManualRefresh();
        Assert.True(Pump(() => overlay.Models?.Failed == true) && Idle(overlay));
        Assert.NotNull(overlay.Models!.Data);
        choice = DashboardDeviceLens.Choose(
            local, AppView.Stats, overlay, null, null, "", "", () => new ModelColorMap((ModelReport?)null));
        Assert.Contains(C.KeptLast.Localized(), choice.NoteLines);
    }

    [Fact] // a graph still loading beside a failed models fetch is Loading, not "couldn't load"
    public void StatsWithTheGraphStillLoadingAndModelsFailedIsLoading()
    {
        var (overlay, source, _, sync) = Open();
        source.Set(s => s.FailModels = true);
        Assert.True(sync.Sync(AppView.Models, null, null, "")); // the models slot fails for this key
        Assert.True(Pump(() => overlay.Models?.Failed == true) && Idle(overlay));
        source.Set(s => s.HoldCached = true); // the graph's first answer is held back
        Assert.True(sync.Sync(AppView.Stats, null, null, ""));
        Assert.True(Pump(() => source.Calls.Any(c => c.Kind == "graph")));
        var choice = DashboardDeviceLens.Choose(
            LocalPayload(), AppView.Stats, overlay, null, null, "", "", () => new ModelColorMap((ModelReport?)null));
        Assert.Equal(LensSource.Loading, choice.Source);
    }

    [Fact] // one model, one colour: the overlay's own map is what the lens is given
    public void TheOverlaysColoursReachTheLensAndTheMergeFallsBackToThisPcsOnlyWithoutAModelsPage()
    {
        var (overlay, _, _, sync) = Open();
        var localColors = new ModelColorMap((ModelReport?)null);
        Assert.True(sync.Sync(AppView.Models, null, null, ""));
        Assert.True(Pump(() => overlay.Models?.Data is not null) && Idle(overlay));
        var models = DashboardDeviceLens.Choose(
            LocalPayload(), AppView.Models, overlay, null, null, "", "", () => localColors);
        Assert.Same(overlay.ModelsColors, models.Colors);
        Assert.NotSame(localColors, models.Colors);

        var held = overlay.OverviewMergeFor(null, null, "", "", localColors)!;
        Assert.Same(overlay.ModelsColors, held.Colors);
        Assert.True(sync.Sync(AppView.Overview, "1990", null, "")); // a key with no models page yet
        var missing = overlay.OverviewMergeFor("1990", null, "", "", localColors)!;
        Assert.Same(localColors, missing.Colors);
    }

    // ---- remote strings (R16) -------------------------------------------------------

    [Fact] // a label with U+202E, a placeholder and a newline arrives unchanged everywhere, and nothing throws
    public void ARemoteLabelWithControlCharactersAndAPlaceholderIsCarriedUnchanged()
    {
        var (overlay, source, clock, sync) = Open(Peer(Evil));
        source.Set(s => { s.RemoteLabel = Evil; s.RemoteState = "lastGood"; s.RemoteReason = "unreachable"; });
        overlay.ToggleLocal(); // only the device: the menu title is its name
        DeviceOverlayState.ResetSingleFlightForTesting();
        Assert.True(sync.Sync(AppView.Daily, null, null, ""));
        Assert.True(Pump(() => overlay.Graph?.Data is not null) && Idle(overlay));

        var lines = overlay.StatusLines(AppView.Daily, null, null);
        Assert.Contains(lines, l => l.Contains(Evil));
        Assert.Contains(lines, l => l == C.UnreachableData.Localized(Evil, Format.RelativeTime(1_700_000_000UL, clock.Now)));
        Assert.Equal(Evil, DashboardDeviceLens.MenuTitle(overlay));
        var menu = DashboardDeviceLens.Menu(overlay, clock.Now)!;
        Assert.Equal(Evil, menu.Rows[1].Name);
        Assert.Contains(Evil, menu.Structure);

        var page = overlay.Graph!.Data!;
        var rows = DeviceTotals.Rows(page.Devices, ["claude", FakeDeviceSource.RemoteClient], overlay.LearnedNames);
        Assert.Contains(rows, r => r.Name == Evil);
        var device = new RemoteDevice(
            "x", "fresh", Evil, 1_700_000_000, null, [new DeviceClientTotal(Evil, 1, 2, 3.0)], "10.0.0.9:1");
        var custom = DeviceTotals.Rows([device], [Evil]);
        Assert.Equal(Evil, custom[0].Name);
        Assert.Contains("2", DeviceTotals.Detail(custom[0], clock.Now));
        Assert.Contains(Evil, C.StatusLine(device, Evil, false, false, clock.Now));
        _ = C.ShortStatus(device, true, false, clock.Now); // no label in it, and no throw
    }
}
