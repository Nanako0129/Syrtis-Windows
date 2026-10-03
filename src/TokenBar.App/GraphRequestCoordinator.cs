using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

public enum GraphPublicationStage
{
    LocalFirst,
    Richer,
}

public readonly record struct GraphQuery(string? Year)
{
    public static GraphQuery Normalize(string? year) =>
        new(string.IsNullOrWhiteSpace(year) ? null : year.Trim());
}

public readonly record struct GraphRequestId(GraphQuery Query, long Generation);

/// <param name="RestoredFrom">When the payload is a persisted snapshot, the
/// time it was captured; null for a live computation. Lets the UI say the
/// numbers on screen are from an earlier run until the live pass replaces
/// them.</param>
public sealed record GraphPublication(
    GraphRequestId RequestId,
    GraphPublicationStage Stage,
    UsagePayload Payload,
    DateTimeOffset? RestoredFrom = null);

public sealed record GraphRequestCompletion(
    GraphRequestId RequestId,
    bool LocalSucceeded,
    bool RicherSucceeded)
{
    public bool Succeeded => LocalSucceeded && RicherSucceeded;

    public bool IsSuccessfulRicherFor(GraphRequestId requestId) =>
        RequestId == requestId && RicherSucceeded;
}

public readonly record struct GraphAttachment(
    GraphRequestId RequestId,
    GraphPublication? Latest,
    bool InFlight);

internal sealed class SnapshotAccess
{
    public SnapshotAccess(
        Func<string?, GraphSnapshotReadResult> read,
        Func<string?, DateTimeOffset, UsagePayload, Action<Action>?, GraphSnapshotWriteStatus> write)
    {
        Read = read ?? throw new ArgumentNullException(nameof(read));
        Write = write ?? throw new ArgumentNullException(nameof(write));
    }

    public Func<string?, GraphSnapshotReadResult> Read { get; }

    public Func<string?, DateTimeOffset, UsagePayload, Action<Action>?, GraphSnapshotWriteStatus> Write { get; }
}

/// <summary>Process-shared graph pipeline. Every production graph call enters
/// here, so local-first publication and request supersession have one owner.</summary>
public sealed class GraphRequestCoordinator
{
    internal const Environment.SpecialFolder SnapshotProfileRoot =
        Environment.SpecialFolder.LocalApplicationData;

    /// <summary>Oldest persisted snapshot still shown at startup: 90 days, as
    /// macOS (<c>SnapshotStore.maxAge</c>). A hit already requires the same
    /// schema, source context id and year (<see cref="GraphSnapshotStore"/>),
    /// the payload is validated, and the live pass replaces it as soon as it
    /// publishes, so age only decides whether a cold start shows yesterday's
    /// numbers (marked as such) or nothing.</summary>
    internal static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromDays(90);

    private sealed class QueryState(GraphQuery query)
    {
        public GraphQuery Query { get; } = query;
        public GraphRequestId Current;
        public GraphPublication? Latest;
        public bool InFlight;
        public bool CurrentSucceeded;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private readonly object _gate = new();
    private readonly object _publicationGate = new();
    private readonly Dictionary<GraphQuery, QueryState> _states = [];
    private readonly Func<string?, UsagePayload> _localFirst;
    private readonly Func<string?, UsagePayload> _graph;
    private readonly Func<string?, UsagePayload> _refreshGraph;
    private readonly Func<IDisposable> _boost;
    private readonly SnapshotAccess? _snapshot;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _nextGeneration;

    public GraphRequestCoordinator(
        Func<string?, UsagePayload>? localFirst = null,
        Func<string?, UsagePayload>? graph = null,
        Func<string?, UsagePayload>? refreshGraph = null,
        Func<IDisposable>? boost = null)
        : this(localFirst, graph, refreshGraph, boost, null, null)
    {
    }

    internal GraphRequestCoordinator(
        Func<string?, UsagePayload>? localFirst,
        Func<string?, UsagePayload>? graph,
        Func<string?, UsagePayload>? refreshGraph,
        Func<IDisposable>? boost,
        SnapshotAccess? snapshot,
        Func<DateTimeOffset>? utcNow)
    {
        _localFirst = localFirst ?? TbCore.GraphLocalFirst;
        _graph = graph ?? refreshGraph ?? TbCore.RefreshGraph;
        _refreshGraph = refreshGraph ?? graph ?? TbCore.RefreshGraph;
        _boost = boost ?? (() => NoopScope.Instance);
        _snapshot = snapshot;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal GraphRequestCoordinator(
        Func<string?, UsagePayload>? localFirst,
        Func<string?, UsagePayload>? graph,
        SnapshotAccess snapshot,
        Func<DateTimeOffset>? utcNow = null)
        : this(localFirst, graph, null, null, snapshot, utcNow)
    {
    }

    internal GraphRequestCoordinator(
        Func<string?, UsagePayload>? localFirst,
        Func<string?, UsagePayload>? graph,
        Func<string?, UsagePayload>? refreshGraph,
        SnapshotAccess snapshot,
        Func<DateTimeOffset>? utcNow = null)
        : this(localFirst, graph, refreshGraph, null, snapshot, utcNow)
    {
    }

    internal SnapshotAccess? Snapshot => _snapshot;

    public event Action<GraphRequestId>? Started;
    public event Action<GraphPublication>? Published;
    public event Action<GraphRequestCompletion>? Completed;

    /// <summary>Build the production graph pipeline and its profile-local
    /// snapshot. Any profile setup failure leaves the live pipeline usable.</summary>
    internal static GraphRequestCoordinator CreateForApp(
        Func<IDisposable>? boost = null,
        Func<Environment.SpecialFolder, string?>? getFolderPath = null,
        Func<string>? sourceContextId = null,
        Action<string>? createDirectory = null,
        Func<string?, UsagePayload>? localFirst = null,
        Func<string?, UsagePayload>? graph = null,
        Func<string?, UsagePayload>? refreshGraph = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        try
        {
            var root = getFolderPath is null
                ? Environment.GetFolderPath(SnapshotProfileRoot)
                : getFolderPath(SnapshotProfileRoot);
            if (string.IsNullOrWhiteSpace(root)
                || !Path.IsPathFullyQualified(root))
            {
                throw new InvalidOperationException();
            }

            var profile = Path.Combine(root, "TokenBar");
            if (createDirectory is null)
            {
                Directory.CreateDirectory(profile);
            }
            else
            {
                createDirectory(profile);
            }

            var sourceId = sourceContextId is null
                ? TbCore.SourceContextId()
                : sourceContextId();
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                throw new InvalidOperationException();
            }

            var store = new GraphSnapshotStore(
                Path.Combine(profile, "graph-snapshot.json"));
            var snapshot = new SnapshotAccess(
                year => store.Read(sourceId, year),
                (year, capturedAt, payload, commitFence) =>
                    store.Write(sourceId, year, capturedAt, payload, commitFence));
            return new GraphRequestCoordinator(
                localFirst,
                graph,
                refreshGraph,
                boost,
                snapshot,
                utcNow);
        }
        catch
        {
            return new GraphRequestCoordinator(
                localFirst,
                graph,
                refreshGraph,
                boost,
                snapshot: null,
                utcNow: utcNow);
        }
    }

    /// <summary>Attach an observer's exact query. A retained exact publication
    /// is returned synchronously; otherwise one pipeline is started.</summary>
    public GraphAttachment Attach(
        string? year,
        Action<GraphRequestId>? onBegin = null)
    {
        var query = GraphQuery.Normalize(year);
        QueryState state;
        GraphRequestId requestId;
        GraphPublication? latest;
        bool start;
        bool inFlight;
        lock (_gate)
        {
            state = GetState(query);
            if (state.InFlight)
            {
                requestId = state.Current;
                start = false;
            }
            else if (state.CurrentSucceeded && state.Latest?.RequestId == state.Current)
            {
                requestId = state.Current;
                start = false;
            }
            else
            {
                requestId = BeginLocked(state);
                start = true;
            }
        }

        if (start)
        {
            InvokeStarted(requestId);
        }

        onBegin?.Invoke(requestId);
        lock (_gate)
        {
            // The handoff callback may let a publication race before the
            // consumer begins. Replay the newest exact publication instead of
            // returning the snapshot captured before that handoff.
            latest = state.Latest?.RequestId == requestId ? state.Latest : null;
            inFlight = state.InFlight;
        }

        if (start)
        {
            StartPipeline(state, requestId, force: false);
            StartRestore(state, requestId);
        }

        return new GraphAttachment(requestId, latest, inFlight);
    }

    /// <summary>Start a newer generation unless the exact query is already
    /// running. Force always supersedes the previous generation.</summary>
    public GraphRequestId Request(
        string? year,
        bool force = false,
        Action<GraphRequestId>? onBegin = null,
        Action<GraphPublication>? onReplay = null)
    {
        var query = GraphQuery.Normalize(year);
        QueryState state;
        GraphRequestId requestId;
        bool start;
        lock (_gate)
        {
            state = GetState(query);
            if (!force && state.InFlight)
            {
                requestId = state.Current;
                start = false;
            }
            else
            {
                requestId = BeginLocked(state);
                start = true;
            }
        }

        if (start)
        {
            InvokeStarted(requestId);
        }

        onBegin?.Invoke(requestId);
        if (!start && onReplay is not null)
        {
            GraphPublication? latest;
            lock (_gate)
            {
                // The consumer begins outside the coordinator lock. Reacquire
                // the newest exact stage so a publication racing that handoff
                // cannot be lost or replayed behind a newer richer stage.
                latest = state.Latest?.RequestId == requestId ? state.Latest : null;
            }

            if (latest is not null)
            {
                onReplay(latest);
            }
        }

        if (start)
        {
            StartPipeline(state, requestId, force);
        }

        return requestId;
    }

    private QueryState GetState(GraphQuery query)
    {
        if (_states.TryGetValue(query, out var state))
        {
            return state;
        }

        state = new QueryState(query);
        _states.Add(query, state);
        return state;
    }

    private GraphRequestId BeginLocked(QueryState state)
    {
        var requestId = new GraphRequestId(state.Query, ++_nextGeneration);
        state.Current = requestId;
        state.Latest = null;
        state.InFlight = true;
        state.CurrentSucceeded = false;
        return requestId;
    }

    private void StartPipeline(QueryState state, GraphRequestId requestId, bool force)
    {
        try
        {
            _ = Task.Run(() => RunPipeline(state, requestId, force));
        }
        catch
        {
        }
    }

    private void StartRestore(QueryState state, GraphRequestId requestId)
    {
        if (_snapshot is null)
        {
            return;
        }

        try
        {
            _ = Task.Run(() => RestoreSnapshot(state, requestId));
        }
        catch
        {
        }
    }

    private void RunPipeline(QueryState state, GraphRequestId requestId, bool force)
    {
        var localSucceeded = false;
        var richerSucceeded = false;
        try
        {
            UsagePayload local;
            try
            {
                using (_boost())
                {
                    local = _localFirst(requestId.Query.Year);
                }

                localSucceeded = true;
            }
            catch
            {
                return;
            }

            if (!PublishIfCurrent(
                    state,
                    requestId,
                    new GraphPublication(
                        requestId, GraphPublicationStage.LocalFirst, local)))
            {
                return;
            }

            try
            {
                UsagePayload richer;
                using (_boost())
                {
                    richer = (force ? _refreshGraph : _graph)(requestId.Query.Year);
                }

                richerSucceeded = true;
                if (PublishIfCurrent(
                        state,
                        requestId,
                        new GraphPublication(
                            requestId, GraphPublicationStage.Richer, richer)))
                {
                    StartPersist(state, requestId, richer);
                }
            }
            catch
            {
                // Local-first is still useful; no richer success is published.
            }
        }
        finally
        {
            var current = false;
            lock (_gate)
            {
                if (state.Current == requestId)
                {
                    state.InFlight = false;
                    state.CurrentSucceeded = localSucceeded
                        || state.Latest?.RequestId == requestId;
                    current = true;
                }
            }

            if (current)
            {
                InvokeCompleted(new GraphRequestCompletion(
                    requestId, localSucceeded, richerSucceeded));
            }
        }
    }

    private void RestoreSnapshot(QueryState state, GraphRequestId requestId)
    {
        var access = _snapshot;
        if (access is null)
        {
            return;
        }

        GraphSnapshotReadResult? result;
        try
        {
            result = access.Read(requestId.Query.Year);
        }
        catch
        {
            return;
        }

        if (result is null
            || result.Status != GraphSnapshotReadStatus.Hit
            || result.Payload is null
            || result.CapturedAt is not { } capturedAt)
        {
            return;
        }

        try
        {
            var now = _utcNow().ToUniversalTime();
            var age = now - capturedAt.ToUniversalTime();
            if (age < TimeSpan.Zero || age > SnapshotMaxAge)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        PublishIfCurrent(
            state,
            requestId,
            new GraphPublication(
                requestId, GraphPublicationStage.LocalFirst, result.Payload, capturedAt),
            snapshot: true);
    }

    private void StartPersist(
        QueryState state,
        GraphRequestId requestId,
        UsagePayload payload)
    {
        var access = _snapshot;
        if (access is null)
        {
            return;
        }

        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var capturedAt = _utcNow();
                    access.Write(
                        requestId.Query.Year,
                        capturedAt,
                        payload,
                        commit =>
                        {
                            lock (_gate)
                            {
                                if (state.Current == requestId
                                    && requestId.Generation == _nextGeneration)
                                {
                                    commit();
                                }
                            }
                        });
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

    private bool PublishIfCurrent(
        QueryState state,
        GraphRequestId requestId,
        GraphPublication publication,
        bool snapshot = false)
    {
        lock (_publicationGate)
        {
            lock (_gate)
            {
                if (state.Current != requestId
                    || snapshot && state.Latest is not null)
                {
                    return false;
                }

                state.Latest = publication;
                state.CurrentSucceeded = true;
            }

            InvokePublished(publication);
            lock (_gate)
            {
                return state.Current == requestId;
            }
        }
    }

    private void InvokeStarted(GraphRequestId requestId)
    {
        foreach (var observer in Started?.GetInvocationList()
            .Cast<Action<GraphRequestId>>() ?? [])
        {
            try
            {
                observer(requestId);
            }
            catch
            {
                // One consumer cannot prevent the others from revoking stale state.
            }
        }
    }

    private void InvokePublished(GraphPublication publication)
    {
        foreach (var observer in Published?.GetInvocationList()
            .Cast<Action<GraphPublication>>() ?? [])
        {
            try
            {
                observer(publication);
            }
            catch
            {
                // A consumer must not stop the shared pipeline for its peers.
            }
        }
    }

    private void InvokeCompleted(GraphRequestCompletion completion)
    {
        foreach (var observer in Completed?.GetInvocationList()
            .Cast<Action<GraphRequestCompletion>>() ?? [])
        {
            try
            {
                observer(completion);
            }
            catch
            {
                // Completion is advisory; consumer failures cannot poison peers.
            }
        }
    }
}
