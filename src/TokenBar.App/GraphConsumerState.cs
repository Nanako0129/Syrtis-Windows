using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

public static class GraphCompletionPolicy
{
    public static (bool IsCurrent, bool ShouldRerun, bool ClearRefreshing) Decide(
        bool isCurrent,
        bool polling,
        bool sameYear,
        bool forceRequested,
        bool refreshing)
    {
        if (!isCurrent)
        {
            return default;
        }

        var shouldRerun = polling && (!sameYear || forceRequested);
        return (true, shouldRerun, refreshing && !shouldRerun);
    }
}

/// <summary>The Refresh button's tooltip (macOS <c>PopoverView.refreshHelp</c>):
/// while the graph on screen is a restored snapshot, it says how old the data
/// is.</summary>
public static class RefreshTip
{
    public static string Text(DateTimeOffset? restoredAt, DateTimeOffset now) =>
        restoredAt is { } at
            ? "Refresh usage data — showing data from {0}".Localized(
                Format.RelativeTime((ulong)Math.Max(0, at.ToUnixTimeSeconds()), now))
            : "Refresh usage data".Localized();

    /// <summary>The Refresh control is busy (spinning, disabled) while a
    /// manual refresh or any graph request the dashboard started runs, or
    /// while nothing is loaded yet. Ctrl+R passes <c>loading: false</c>, so a
    /// failed first load can still be retried by hand (macOS
    /// <c>refreshDisabled</c> has no loading term).</summary>
    public static bool Busy(bool loading, bool manualRefresh, bool graphInFlight) =>
        loading || manualRefresh || graphInFlight;

    /// <summary>The glyph is tinted only when restored data is on screen and
    /// its live refresh failed: the one state where data is stale and nothing
    /// is running to fix it (macOS <c>showingStaleRestore</c>). Restored data
    /// with a refresh still running is not tinted.</summary>
    public static bool ShowsStaleRestore(DateTimeOffset? restoredAt, bool restoreFailed) =>
        restoredAt is not null && restoreFailed;
}

public static class GraphResumePolicy
{
    public static bool ShouldRefreshAfterAttach(GraphAttachment attachment) =>
        attachment.RequestId.Query.Year is not null && !attachment.InFlight;
}

public static class GraphYearPolicy
{
    public static bool ShouldClearToAllTime(
        string? selectedYear,
        GraphPublication publication) =>
        selectedYear is not null
        && publication.RequestId.Query.Year == selectedYear
        && !publication.Payload.Years.Any(year => year.Year == selectedYear);
}

public static class GraphLazyRefreshPolicy
{
    public static bool ShouldRequest(
        GraphRequestId? scheduledRequestId,
        GraphRequestId requestId) =>
        scheduledRequestId != requestId;
}

/// <summary>Which lazy lanes the currently-open lens wants, told by the view
/// switch rather than accumulated by "ensure" calls that never un-want.
/// <para>
/// Round-10 finding: <c>DashboardModel</c> exposed one <c>EnsureX</c> method
/// per lane, each only ever setting its own flag true, with no path back to
/// false — a user who visited Quota once left <c>_windowUsageWanted</c> (and
/// its siblings, same shape) true for the model's lifetime, so every later
/// graph publication's <see cref="GraphLazyRefreshPolicy"/> request repeated
/// the window-usage export even with Quota no longer visible, serialized
/// ahead of whichever lens actually was. <c>DashboardView.SwitchTo</c> is the
/// only place that already knows which lens is active — every "ensure" call
/// site derives from it — so the model is now told that fact directly
/// instead of being told to want lanes one at a time with no way to stop
/// wanting them.
/// </para></summary>
public static class LazyLaneActivation
{
    public readonly record struct Wanted(
        bool Hourly, bool Agents, bool QuotaHistory, bool WindowUsage);

    /// <summary>Quota's card and its window-usage export open together, the
    /// same pairing <c>DashboardView.SwitchTo</c> already fetches together.
    /// Overview wants the quota history too: its Agent-limits card draws the
    /// recent-trend arrow (and, in the Chart layout, the sparkline) from those
    /// curves. macOS refreshes the curves with every quota card refresh,
    /// whichever lens is open (DashboardModel.swift, windowCurves); without
    /// this the Overview card showed arrows only after a Quota visit had left
    /// history behind in the snapshot.</summary>
    public static Wanted For(AppView view) => view switch
    {
        AppView.Overview => new Wanted(false, false, true, false),
        AppView.Hourly => new Wanted(true, false, false, false),
        AppView.Agents => new Wanted(false, true, false, false),
        AppView.Quota => new Wanted(false, false, true, true),
        _ => default,
    };
}

/// <summary>Pure consumer gate for one retained graph pipeline. UI-facing
/// properties may only change after these exact request/generation checks.</summary>
public sealed class GraphConsumerState : IDisposable
{
    private readonly object _gate = new();
    private GraphRequestId? _desired;
    private GraphRequestId? _acceptedId;
    private UsagePayload? _acceptedGraph;
    private GraphPublicationStage? _acceptedStage;
    private bool _localAccepted;
    private bool _costAuthoritative;
    private bool _modelStarted;
    private bool _modelAccepted;
    private bool _disposed;

    public GraphRequestId? DesiredId
    {
        get { lock (_gate) return _desired; }
    }

    public GraphRequestId? AcceptedId
    {
        get { lock (_gate) return _acceptedId; }
    }

    public UsagePayload? AcceptedGraph
    {
        get { lock (_gate) return _acceptedGraph; }
    }

    public GraphPublicationStage? AcceptedStage
    {
        get { lock (_gate) return _acceptedStage; }
    }

    public bool LocalAccepted
    {
        get { lock (_gate) return _localAccepted; }
    }

    public bool CostAuthoritative
    {
        get { lock (_gate) return _costAuthoritative; }
    }

    public bool ModelStarted
    {
        get { lock (_gate) return _modelStarted; }
    }

    public bool ModelAccepted
    {
        get { lock (_gate) return _modelAccepted; }
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>Revoke the previous generation immediately. A same-query
    /// refresh may keep a caller's old topology, but this state exposes no old
    /// graph or cost authority as current.</summary>
    public bool Begin(GraphRequestId requestId)
    {
        lock (_gate)
        {
            if (_disposed || _desired == requestId)
            {
                return false;
            }

            if (_desired is { } desired
                && desired.Query == requestId.Query
                && requestId.Generation < desired.Generation)
            {
                return false;
            }

            _desired = requestId;
            _acceptedId = null;
            _acceptedGraph = null;
            _acceptedStage = null;
            _localAccepted = false;
            _costAuthoritative = false;
            _modelStarted = false;
            _modelAccepted = false;
            return true;
        }
    }

    /// <summary>Accept only the exact desired query+generation. A richer
    /// retained publication also proves the local stage for late observers.</summary>
    public bool TryAcceptGraph(
        GraphRequestId requestId,
        UsagePayload graph,
        GraphPublicationStage stage)
    {
        ArgumentNullException.ThrowIfNull(graph);
        lock (_gate)
        {
            if (_disposed || _desired != requestId)
            {
                return false;
            }

            // Attach returns a retained snapshot after releasing the coordinator
            // lock. A same-generation richer event can therefore arrive before
            // the retained local snapshot is replayed; never let that replay
            // regress the accepted stage or revoke richer cost authority.
            if (_acceptedId == requestId
                && _acceptedStage == GraphPublicationStage.Richer
                && stage == GraphPublicationStage.LocalFirst)
            {
                return false;
            }

            _acceptedId = requestId;
            _acceptedGraph = graph;
            _acceptedStage = stage;
            _localAccepted = true;
            _costAuthoritative = CostSurfaceProjection.IsAuthoritative(graph);
            return true;
        }
    }

    /// <summary>ModelReport may start once, and only after exact-generation
    /// LocalAccepted (including a retained richer attachment).</summary>
    public bool TryBeginModel(GraphRequestId requestId)
    {
        lock (_gate)
        {
            if (_disposed || _desired != requestId || !_localAccepted || _modelStarted)
            {
                return false;
            }

            _modelStarted = true;
            return true;
        }
    }

    public bool TryAcceptModel(GraphRequestId requestId, ModelReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (_disposed || _desired != requestId || !_modelStarted)
            {
                return false;
            }

            _modelAccepted = true;
            return true;
        }
    }

    public bool IsCurrent(GraphRequestId requestId)
    {
        lock (_gate)
        {
            return !_disposed && _desired == requestId;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _desired = null;
            _acceptedId = null;
            _acceptedGraph = null;
            _acceptedStage = null;
            _localAccepted = false;
            _costAuthoritative = false;
            _modelStarted = false;
            _modelAccepted = false;
        }
    }
}
