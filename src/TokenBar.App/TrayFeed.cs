using Microsoft.UI.Dispatching;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Tray-lifetime data feed (macOS AppDelegate title loop + TrayAnimator
/// pollers): the tray icon keeps its numbers fresh whether or not the
/// flyout is open — rate every 30s, graph and quota every 300s, all served
/// mostly from the engine's caches. The dashboard's own lanes stay separate,
/// same as macOS. The tokenbar.refresh.intervalMin forced re-read joins
/// with the settings panel.
/// </summary>
public sealed class TrayFeed : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly GraphRequestCoordinator _graphCoordinator;
    private readonly GraphConsumerState _graphState = new();
    private readonly DispatcherQueueTimer _fast;
    private readonly DispatcherQueueTimer _slow;
    private readonly Action<string> _onStoreChanged;
    private int _fastInFlight; // Interlocked: reset in a background finally
    private int _slowInFlight;
    private int _quotaInFlight;
    private bool _disposed;

    public UsagePayload? Graph { get; private set; }

    public IReadOnlyList<TraceBucket> Trace { get; private set; } = [];

    public TrayTotals? VisibleTotals { get; private set; }

    public double? TokensPerMin { get; private set; }

    public AgentUsagePayload? Quota { get; private set; }

    public bool CostAuthoritative => _graphState.CostAuthoritative;

    private bool _hasTrace;
    private string? _cachedQuotaSelection;
    private double? _cachedQuotaRemaining;

    /// <summary>Resolved remaining % for the selected quota window. Boots
    /// from the persisted last reading only when its selection identity matches
    /// the current effective selection.</summary>
    public double? QuotaRemaining { get; private set; }

    public event Action? Changed;

    public TrayFeed(
        DispatcherQueue dispatcher,
        GraphRequestCoordinator graphCoordinator)
    {
        _dispatcher = dispatcher;
        _graphCoordinator = graphCoordinator;
        _graphCoordinator.Started += OnGraphStarted;
        _graphCoordinator.Published += OnGraphPublished;
        _graphCoordinator.Completed += OnGraphCompleted;
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.lastSelection");
        var currentSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var persisted = AppSettings.Store.GetDouble("tokenbar.quota.lastRemaining", double.NaN);
        _cachedQuotaRemaining = QuotaSelectionPolicy.MatchingLastGoodRemaining(
            QuotaSelectionPolicy.EffectiveSelection(null, currentSelection),
            persistedSelection,
            double.IsNaN(persisted) ? null : persisted);
        _cachedQuotaSelection = _cachedQuotaRemaining is null ? null : persistedSelection;
        QuotaRemaining = _cachedQuotaRemaining;

        _fast = dispatcher.CreateTimer();
        _fast.Interval = TimeSpan.FromSeconds(30);
        _fast.Tick += (_, _) => RefreshFast();
        _fast.Start();
        _slow = dispatcher.CreateTimer();
        _slow.Interval = TimeSpan.FromSeconds(300);
        _slow.Tick += (_, _) => RefreshSlow();
        _slow.Start();
        AttachGraph();
        RefreshFast();
        RefreshQuota();

        _onStoreChanged = key =>
        {
            if (key == "tokenbar.quota.source")
            {
                _ = _dispatcher.TryEnqueue(() =>
                {
                    ResolveRemaining(); // re-pick from the cached payload
                    Changed?.Invoke();
                });
            }
            else if (key is ClientRegistry.TabHiddenKey or ClientRegistry.LimitsHiddenKey)
            {
                _ = _dispatcher.TryEnqueue(() =>
                {
                    RecomputeVisibleUsage();
                    ResolveRemaining();
                    Changed?.Invoke();
                });
            }
        };
        AppSettings.Store.Changed += _onStoreChanged;
    }

    /// <summary>Stop polling and unsubscribe so the feed can't raise Changed
    /// into a disposed tray icon after shutdown.</summary>
    public void Dispose()
    {
        _disposed = true; // fences any in-flight lane's enqueued callback
        _graphState.Dispose();
        _graphCoordinator.Started -= OnGraphStarted;
        _graphCoordinator.Published -= OnGraphPublished;
        _graphCoordinator.Completed -= OnGraphCompleted;
        _fast.Stop();
        _slow.Stop();
        AppSettings.Store.Changed -= _onStoreChanged;
    }

    private void RefreshFast()
    {
        if (Interlocked.Exchange(ref _fastInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var boost = ProcessPower.Boost(); // live-tail parse
                var trace = TbCore.UsageTrace(600);
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed)
                    {
                        return; // don't touch the tray icon after shutdown
                    }

                    Trace = trace;
                    _hasTrace = true;
                    RecomputeVisibleUsage();
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                DevLog.Write($"tray rate failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _fastInFlight, 0);
            }
        });
    }

    // Forced full re-read cadence (tokenbar.refresh.intervalMin, default 30):
    // the cached path keeps data fresh continuously; this is the macOS title
    // loop's belt against anything the incremental scan misses. An instance
    // field, so restarting timers never triggers an immediate re-read.
    private long _lastFullRefreshTicks = DateTimeOffset.UtcNow.Ticks;
    private long _forcedRefreshGeneration;

    private void RefreshSlow()
    {
        if (Interlocked.Exchange(ref _slowInFlight, 1) == 1)
        {
            return;
        }

        var intervalMin = Math.Max(1, AppSettings.Store.GetInt("tokenbar.refresh.intervalMin", 30));
        var lastFullRefresh = new DateTimeOffset(
            Volatile.Read(ref _lastFullRefreshTicks), TimeSpan.Zero);
        var force = DateTimeOffset.UtcNow - lastFullRefresh
            >= TimeSpan.FromMinutes(intervalMin);

        _graphCoordinator.Request(null, force, requestId =>
        {
            OnGraphStarted(requestId);
            if (force)
            {
                Volatile.Write(ref _forcedRefreshGeneration, requestId.Generation);
            }
        });
        RefreshQuota();
    }

    private void AttachGraph()
    {
        Interlocked.Exchange(ref _slowInFlight, 1);
        var attachment = _graphCoordinator.Attach(null, OnGraphRequestStarted);
        if (!attachment.InFlight)
        {
            Volatile.Write(ref _slowInFlight, 0);
        }

        if (attachment.Latest is { } latest)
        {
            OnGraphPublished(latest);
        }
    }

    private void OnGraphStarted(GraphRequestId requestId)
    {
        if (requestId.Query.Year is not null || !_graphState.Begin(requestId))
        {
            return;
        }

        // Revoke cost authority before the graph callback lands; token/rate/
        // quota projections remain usable from the retained topology.
        _ = _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && _graphState.IsCurrent(requestId))
            {
                Changed?.Invoke();
            }
        });
    }

    private void OnGraphRequestStarted(GraphRequestId requestId) =>
        OnGraphStarted(requestId);

    private void OnGraphPublished(GraphPublication publication)
    {
        if (!_graphState.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage))
        {
            return;
        }

        _ = _dispatcher.TryEnqueue(() =>
        {
            // Repeat the exact request/generation/disposal gate after dispatch.
            if (_disposed || !_graphState.TryAcceptGraph(
                    publication.RequestId, publication.Payload, publication.Stage))
            {
                return;
            }

            Graph = publication.Payload;
            RecomputeVisibleUsage();
            ResolveRemaining();
            Changed?.Invoke();
        });
    }

    private void OnGraphCompleted(GraphRequestCompletion completion)
    {
        if (!_graphState.IsCurrent(completion.RequestId))
        {
            return;
        }

        Volatile.Write(ref _slowInFlight, 0);
        var forcedGeneration = Volatile.Read(ref _forcedRefreshGeneration);
        var forcedRequest = new GraphRequestId(
            GraphQuery.Normalize(null), forcedGeneration);
        if (forcedGeneration != 0
            && completion.IsSuccessfulRicherFor(forcedRequest)
            && Interlocked.CompareExchange(
                ref _forcedRefreshGeneration, 0, forcedGeneration)
                == forcedGeneration)
        {
            Interlocked.Exchange(
                ref _lastFullRefreshTicks, DateTimeOffset.UtcNow.Ticks);
        }
    }

    private void RefreshQuota()
    {
        if (Interlocked.Exchange(ref _quotaInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var quota = TryFetch(
                    () => AgentUsageFetchCoordinator.Shared.FetchAsync().GetAwaiter().GetResult(),
                    "tray quota");
                if (quota is null)
                {
                    return;
                }

                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Quota = quota;
                    var persistedSelection = AppSettings.Store.GetString(
                        "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
                    if (QuotaSelectionPolicy.MigrationToPersist(quota, persistedSelection)
                        is { } migrated)
                    {
                        AppSettings.Store.SetString("tokenbar.quota.source", migrated);
                    }

                    RecomputeVisibleUsage();
                    ResolveRemaining();
                    Changed?.Invoke();
                });
            }
            finally
            {
                Volatile.Write(ref _quotaInFlight, 0);
            }
        });
    }

    private void RecomputeVisibleUsage()
    {
        // Client ids, not tab ids: a stored hide of the "antigravity" tab
        // must still exclude "antigravity-cli"'s tokens from these usage
        // totals, and a legacy "antigravity-cli" hide must still exclude
        // "antigravity". HiddenTabClients folds both directions.
        var hidden = ClientRegistry.HiddenTabClients(AppSettings.Store);
        VisibleTotals = Graph?.TrayTotals(hidden, Format.TodayKey());
        TokensPerMin = _hasTrace ? TraceCollapse.TotalRate(Trace, hidden) : null;
    }

    private void ResolveRemaining()
    {
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var selection = QuotaSelectionPolicy.EffectiveSelection(Quota, persistedSelection);
        var hidden = ClientRegistry.QuotaExcludedClients(AppSettings.Store);
        if (QuotaResolver.Resolve(Quota, selection, hidden) is { } pick)
        {
            var resolved = Math.Clamp(pick.Window.RemainingPercent, 0, 100);
            if (resolved != QuotaRemaining)
            {
                DevLog.Write($"tray quota pick: {pick.ClientId}|{pick.Window.CardId} {resolved:F1}%");
            }

            QuotaRemaining = resolved;
            _cachedQuotaSelection = selection;
            _cachedQuotaRemaining = resolved;
            // Write-through pair so the next launch boots only for this
            // selection (the store no-ops when values have not changed).
            AppSettings.Store.SetDouble("tokenbar.quota.lastRemaining", resolved);
            AppSettings.Store.SetString("tokenbar.quota.lastSelection", selection);
        }
        else if (QuotaResolver.ExcludedAllCandidates(Quota, selection, hidden))
        {
            // All healthy AUTO candidates are hidden. Suppress only the
            // displayed reading; keep the selected-source last-good pair.
            QuotaRemaining = null;
        }
        else
        {
            // Fetch/provider/explicit-selection failures keep only this
            // selection's last-good reading visible.
            QuotaRemaining = QuotaSelectionPolicy.MatchingLastGoodRemaining(
                selection, _cachedQuotaSelection, _cachedQuotaRemaining);
        }
    }

    private static T? TryFetch<T>(Func<T> fetch, string label) where T : class
    {
        try
        {
            return fetch();
        }
        catch (Exception ex)
        {
            DevLog.Write($"{label} failed: {ex.Message}");
            return null;
        }
    }
}
