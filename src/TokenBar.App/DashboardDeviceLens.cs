using System.Runtime.CompilerServices;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

// The UI-free half of the dashboard's device overlay (SA-4g): what each lens
// shows (this PC's snapshot or an overlay page), the device menu's rows, the
// notes, the refresh control's state and the once-per-key ensure. DashboardView
// compiles under no test project, so every decision lives here. Nothing in this
// file takes or returns DashboardModel: overlay pages never enter the model, its
// snapshot, SetClientSelection or LocalRecordClients.

/// <summary>Where a lens gets its content (macOS DeviceOverlayLens, PopoverView
/// lensContent).</summary>
public enum LensSource
{
    /// <summary>This PC's own snapshot, unchanged.</summary>
    Local,

    /// <summary>The selected devices' page.</summary>
    Overlay,

    /// <summary>The page has not arrived: a fixed line, never this PC's data.</summary>
    Loading,

    /// <summary>The refresh failed with nothing to show.</summary>
    Failed,
}

/// <summary>What one lens shows. <see cref="Local"/> is set only when
/// <see cref="Source"/> is <see cref="LensSource.Local"/> and is the very
/// instance that was passed in; an overlay choice carries the pages beside it
/// and never inside it.</summary>
public sealed record LensChoice<TLocal>(
    LensSource Source,
    TLocal? Local,
    UsagePayload? Graph = null,
    ModelReport? Models = null,
    HourlyReport? Hourly = null,
    AgentsReport? Agents = null,
    IReadOnlyList<string>? ClientIds = null,
    UsageStats? Stats = null,
    ModelColorMap? Colors = null,
    string? ModelsNote = null,
    IReadOnlyList<RemoteDevice>? Devices = null,
    IReadOnlyList<string>? Notes = null,
    int DeviceCount = 0,
    bool PerDeviceCard = false,
    OverviewMergeState? Merge = null) where TLocal : class
{
    public IReadOnlyList<string> ClientIdList => ClientIds ?? [];
    public IReadOnlyList<string> NoteLines => Notes ?? [];
}

/// <summary>One row of the device menu: this PC (<see cref="Peer"/> null) or a
/// linked device.</summary>
public sealed record DeviceMenuRow(
    string? Peer, string Name, bool Checked, bool CanToggle, DeviceOverlayCopy.ShortStatusResult? Status);

/// <summary>The device menu as the view draws it. <see cref="Structure"/>
/// changes only when the rows themselves change (a device appeared, left or was
/// renamed): the view rebuilds its controls then, and only while the menu is
/// closed; everything else is updated on the controls in place.</summary>
public sealed record DeviceMenuState(string Title, IReadOnlyList<DeviceMenuRow> Rows, string Structure);

public static class DashboardDeviceLens
{
    public static string HiddenRaw(SettingsStore store) => store.GetString(ClientRegistry.TabHiddenKey) ?? "";

    public static string OrderRaw(SettingsStore store) => store.GetString(ClientRegistry.TabOrderKey) ?? "";

    /// <summary>The <c>clients</c> argument of every overlay call and query:
    /// the single client tab's slice, else the registry minus the hidden tabs.
    /// Never this PC's present clients (that would drop a remote-only client
    /// from Hourly and Agents).</summary>
    public static IReadOnlyList<string>? Clients(string? singleClient, string hiddenRaw) =>
        DeviceOverlayRequests.ClientsArgument(singleClient, ClientRegistry.ParseIdSet(hiddenRaw));

    // ---- the lens choice --------------------------------------------------

    /// <summary>Which content <paramref name="view"/> shows. Only a lens with
    /// overlay pages (Models, Monthly, Daily, Hourly, Stats, Agents) while a
    /// device other than this PC is selected leaves <see cref="LensSource.Local"/>;
    /// Overview stays local and carries the merge for its combined cards; Quota
    /// is this PC's alone.</summary>
    public static LensChoice<TLocal> Choose<TLocal>(
        TLocal local, AppView view, DeviceOverlayState? overlay, string? year, string? singleClient,
        string hiddenRaw, string orderRaw, Func<ModelColorMap> localColors) where TLocal : class
    {
        if (overlay is null || !overlay.IncludesRemote || DeviceOverlayRequests.KindsFor(view).Count == 0)
        {
            return new(LensSource.Local, local);
        }

        if (view == AppView.Overview)
        {
            return new(
                LensSource.Local, local,
                Merge: overlay.OverviewMergeFor(year, singleClient, hiddenRaw, orderRaw, localColors()));
        }

        var clients = Clients(singleClient, hiddenRaw);
        var count = overlay.SelectedCount;
        IReadOnlyList<string> Ids(DeviceOverlayKind kind) =>
            overlay.DisplayClients(kind, singleClient, hiddenRaw, orderRaw);

        switch (view)
        {
            case AppView.Models:
            {
                var (page, failed) = overlay.Visible(overlay.Models, DeviceOverlayKind.Models, year, clients);
                return page is null
                    ? Missing<TLocal>(failed)
                    : new(LensSource.Overlay, null, Models: page.Payload, ClientIds: Ids(DeviceOverlayKind.Models),
                        Colors: overlay.ModelsColors, Notes: Lines(failed, Note(page)), DeviceCount: count);
            }

            case AppView.Hourly:
            {
                var (page, failed) = overlay.Visible(overlay.Hourly, DeviceOverlayKind.Hourly, year, clients);
                return page is null
                    ? Missing<TLocal>(failed)
                    : new(LensSource.Overlay, null, Hourly: page.Payload, ClientIds: Ids(DeviceOverlayKind.Hourly),
                        Notes: Lines(failed, Note(page)), DeviceCount: count);
            }

            case AppView.Agents:
            {
                var (page, failed) = overlay.Visible(overlay.Agents, DeviceOverlayKind.Agents, year, clients);
                return page is null
                    ? Missing<TLocal>(failed)
                    : new(LensSource.Overlay, null, Agents: page.Payload, ClientIds: Ids(DeviceOverlayKind.Agents),
                        Notes: Lines(failed, Note(page)), DeviceCount: count);
            }

            default: // Daily, Monthly, Stats
            {
                var (page, failed) = overlay.Visible(overlay.Graph, DeviceOverlayKind.Graph, year, clients);
                var stats = view == AppView.Stats;
                var (modelsPage, modelsFailed) = stats
                    ? overlay.Visible(overlay.Models, DeviceOverlayKind.Models, year, clients)
                    : (null, false);
                if (page is null)
                {
                    // The graph page decides: a failed models fetch beside a graph
                    // that is still loading is Loading, not "couldn't load".
                    return Missing<TLocal>(failed);
                }

                var ids = Ids(DeviceOverlayKind.Graph);
                var ownStats = stats ? overlay.StatsFor(ids.ToHashSet(StringComparer.Ordinal)) : null;
                if (stats && ownStats is null)
                {
                    return Missing<TLocal>(false);
                }

                var part = stats ? overlay.StatsModels(year) : (Report: null, Note: null);
                return new(
                    LensSource.Overlay, null, Graph: page.Payload, ClientIds: ids, Stats: ownStats,
                    Models: part.Report, ModelsNote: part.Note,
                    Colors: part.Report is null ? null : overlay.ModelsColors,
                    Devices: page.Devices, PerDeviceCard: !stats,
                    // "Showing the last data received" only for a page that is held:
                    // a failed models fetch with no models page keeps nothing.
                    Notes: Lines(failed || (modelsFailed && modelsPage is not null), Note(page), Note(modelsPage)),
                    DeviceCount: count);
            }
        }
    }

    private static LensChoice<TLocal> Missing<TLocal>(bool failed) where TLocal : class =>
        new(failed ? LensSource.Failed : LensSource.Loading, null);

    /// <summary>The notes above a combined page: the capped-totals line once,
    /// then "couldn't update" when the page shown is the last good one.</summary>
    private static IReadOnlyList<string> Lines(bool keptLast, params PageNote?[] pages) =>
    [
        .. DeviceOverlayCopy.Lines(pages.OfType<PageNote>().Select(n => (n.Devices, n.Duplicate, n.Saturated, n.RangeLimited))),
        .. keptLast ? new[] { DeviceOverlayCopy.KeptLast.Localized() } : Array.Empty<string>(),
    ];

    private sealed record PageNote(IReadOnlyList<RemoteDevice> Devices, bool Duplicate, bool Saturated, bool RangeLimited);

    private static PageNote? Note<T>(DeviceAwarePayload<T>? page) =>
        page is null ? null : new(page.Devices, page.DuplicateWarning, page.Saturated, page.RemoteRangeLimited);

    /// <summary>Whether costs the overlay shows may be stated. Read from the pricing
    /// meta of the graph page: the lens's own, else the one the overlay holds for
    /// the same selection and year (the Models, Hourly and Agents lenses fetch no
    /// graph), else this PC's. Never assumed true: with pricing unavailable the
    /// combined card says "Checking", and so must every surface beside it.</summary>
    public static bool Authoritative(
        UsagePayload? pageGraph, DeviceOverlayState? overlay, string? year, bool localAuthoritative)
    {
        if (pageGraph is not null) { return CostSurfaceProjection.IsAuthoritative(pageGraph); }
        var held = overlay?.Visible(overlay.Graph, DeviceOverlayKind.Graph, year, null).Page;
        return held is not null ? CostSurfaceProjection.IsAuthoritative(held.Payload) : localAuthoritative;
    }

    /// <summary>A per-device card row's cost, behind the same gate as every other
    /// cost: a dash for a device with no part in the page, "Checking" while
    /// costs may not be stated, else the money text of its tokens and cost.</summary>
    public static string DeviceCostText(DeviceTotalsRow row, bool authoritative) =>
        row.Cost is { } cost ? CostSurfaceProjection.CostText(row.Tokens ?? 0, cost, authoritative) : "—";

    /// <summary>Overview's per-device card reads the graph page's devices.</summary>
    public static DeviceAwarePayload<UsagePayload>? OverviewDevicesPage(DeviceOverlayState? overlay, string? year) =>
        overlay is { IncludesRemote: true }
            ? overlay.Visible(overlay.Graph, DeviceOverlayKind.Graph, year, null).Page
            : null;

    // ---- notes ------------------------------------------------------------

    /// <summary>The live rate's suffix (macOS PopoverView :735).</summary>
    public static string LiveRateSuffix(DeviceOverlayState? overlay) =>
        overlay is { IncludesRemote: true } ? " " + DeviceOverlayCopy.ThisPcLive.Localized() : "";

    /// <summary>The three "this PC only" notes: above the quota summary, in its
    /// corner, and on the Quota lens. All null while only this PC is selected.</summary>
    public static (string? Summary, string? Corner, string? QuotaLens) QuotaNotes(DeviceOverlayState? overlay) =>
        overlay is { IncludesRemote: true }
            ? (DeviceOverlayCopy.QuotaThisPcOnly.Localized(), DeviceOverlayCopy.ThisPcOnly.Localized(),
                DeviceOverlayCopy.WindowUsageThisPcOnly.Localized())
            : (null, null, null);

    // ---- the device menu --------------------------------------------------

    public static string MenuTitle(DeviceOverlayState overlay)
    {
        var selected = overlay.SelectablePeers.FirstOrDefault(p => overlay.IsSelected(p.ProducerStatic));
        return DeviceOverlayCopy.MenuTitle(
            overlay.SelectedCount, overlay.Stored.Local, selected is null ? null : overlay.DisplayName(selected));
    }

    /// <summary>Null while there is nothing to choose (the menu is hidden).</summary>
    public static DeviceMenuState? Menu(DeviceOverlayState? overlay, DateTimeOffset now)
    {
        if (overlay is null || overlay.SelectablePeers.Count == 0) { return null; }
        var snapshot = overlay.Snapshot;
        List<DeviceMenuRow> rows =
        [
            new(null, DeviceOverlayCopy.ThisPc.Localized(), overlay.Stored.Local, overlay.CanToggleLocal(), null),
        ];
        foreach (var peer in overlay.SelectablePeers)
        {
            rows.Add(new(
                peer.ProducerStatic, overlay.DisplayName(peer), overlay.IsSelected(peer.ProducerStatic),
                overlay.CanTogglePeer(peer.ProducerStatic), snapshot.ShortStatus(peer.Fingerprint, now)));
        }

        // Names are part of the structure: a renamed device gets a rebuilt row.
        var structure = string.Join('\n', rows.Select(r => $"{r.Peer}|{r.Name}"));
        return new(MenuTitle(overlay), rows, structure);
    }

    // ---- refresh control --------------------------------------------------

    /// <summary>The header refresh control: it spins for a local refresh or for
    /// the overlay's fetch, but only a local refresh disables it (a manual
    /// refresh does not wait for the other devices, macOS PopoverView :685-709).</summary>
    public static (bool Spinning, bool Enabled) RefreshControl(bool localBusy, bool overlayFetching) =>
        (localBusy || overlayFetching, !localBusy);

    // ---- re-render trigger ------------------------------------------------

    /// <summary>Everything the overlay contributes to what is on screen for
    /// <paramref name="view"/>. The view rebuilds its content only when this
    /// changes, so a burst of <c>Changed</c> does not rebuild per event.</summary>
    public static string RenderSignature(
        DeviceOverlayState overlay, AppView view, string? year, string? singleClient, string hiddenRaw)
    {
        var clients = Clients(singleClient, hiddenRaw);
        var parts = new List<string> { overlay.Signature, overlay.Fetching.ToString(), overlay.RetryArmed.ToString() };
        foreach (var kind in DeviceOverlayRequests.KindsFor(view))
        {
            switch (kind)
            {
                case DeviceOverlayKind.Graph: Add(overlay.Visible(overlay.Graph, kind, year, clients)); break;
                case DeviceOverlayKind.Models: Add(overlay.Visible(overlay.Models, kind, year, clients)); break;
                case DeviceOverlayKind.Hourly: Add(overlay.Visible(overlay.Hourly, kind, year, clients)); break;
                default: Add(overlay.Visible(overlay.Agents, kind, year, clients)); break;
            }
        }

        parts.AddRange(overlay.StatusLines(view, year, clients));
        return string.Join('\u001f', parts);

        void Add<T>((DeviceAwarePayload<T>? Page, bool Failed) visible) =>
            parts.Add($"{RuntimeHelpers.GetHashCode(visible.Page)}:{visible.Failed}");
    }
}

/// <summary>When the dashboard asks the overlay for its pages: once per key
/// while the flyout is open, never from <c>Changed</c> alone (each
/// <c>Ensure</c> with a missing key starts a cached load and touches the
/// overlay, which raises <c>Changed</c>: asking again on every change would
/// loop). One poll loop per show. UI thread only.</summary>
public sealed class DeviceOverlaySync
{
    private readonly DeviceOverlayState _overlay;
    private bool _open;
    private string? _lastKey;
    private CancellationTokenSource? _poll;

    public DeviceOverlaySync(DeviceOverlayState overlay) => _overlay = overlay;

    /// <summary>Test seam: how many times <c>Ensure</c> was called.</summary>
    internal int EnsureCalls { get; private set; }

    public bool IsOpen => _open;

    /// <summary>The flyout opened: read the peer list (it is what makes the
    /// menu appear after a restart, and runs even when only this PC is
    /// chosen), forget the last key so the first <see cref="Sync"/> ensures, and
    /// start the poll.</summary>
    public void Opened()
    {
        _open = true;
        _lastKey = null;
        _overlay.RefreshPeers();
        if (_poll is null)
        {
            _poll = new CancellationTokenSource();
            _ = _overlay.PollLoopAsync(_poll.Token);
        }
    }

    /// <summary>The flyout closed: stop the poll, tell the overlay nothing is on
    /// screen, and forget the key (a reopen on the same lens ensures again).</summary>
    public void Closed()
    {
        _open = false;
        _lastKey = null;
        _poll?.Cancel();
        _poll = null;
        _overlay.PopoverClosed();
    }

    /// <summary>The slow tick (60 s) while open.</summary>
    public void SlowTick()
    {
        if (_open) { _overlay.RefreshPeers(); }
    }

    /// <summary>Ensures when the key (lens, year, clients argument, selection,
    /// scan-roots generation) differs from the last one ensured. Returns whether
    /// it did. <paramref name="rootsGeneration"/> (DashboardModel.RootsGeneration)
    /// changes when this PC's scan roots changed, which makes this PC's share of
    /// every page stale: the overlay then asks again whatever its 60 s window says.</summary>
    public bool Sync(AppView view, string? year, string? singleClient, string hiddenRaw, int rootsGeneration = 0)
    {
        if (!_open) { return false; }
        var clients = DashboardDeviceLens.Clients(singleClient, hiddenRaw);
        var key = $"{view}|{year}|{(clients is null ? "-" : string.Join(',', clients))}|{_overlay.Signature}|{rootsGeneration}";
        if (key == _lastKey) { return false; }
        _lastKey = key;
        EnsureCalls++;
        _overlay.Ensure(view, year, clients, rootsGeneration);
        return true;
    }
}
