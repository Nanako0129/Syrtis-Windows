using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>A clock a test moves by hand: <see cref="Now"/> only changes in
/// <see cref="Advance"/>, which also completes every delay that has come due.
/// Replaces the real sleeps of the macOS self-tests (their 0.6 s retry delay
/// and 0.2 s freshness window become 65 s and 60 s of fake time).</summary>
internal sealed class FakeDeviceClock : IDeviceClock
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Done)> _pending = [];
    private readonly List<TimeSpan> _requested = [];
    private DateTimeOffset _now = DeviceOverlayTestSupport.Now;

    public DateTimeOffset Now { get { lock (_gate) { return _now; } } }

    /// <summary>Every delay ever asked for, in order.</summary>
    public IReadOnlyList<TimeSpan> Requested { get { lock (_gate) { return [.. _requested]; } } }

    /// <summary>Delays asked for and neither due nor cancelled.</summary>
    public int PendingCount { get { lock (_gate) { return _pending.Count; } } }

    public Task Delay(TimeSpan delay, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _requested.Add(delay);
            _pending.Add((_now + delay, done));
        }
        cancellation.Register(() =>
        {
            lock (_gate) { _pending.RemoveAll(p => p.Done == done); }
            done.TrySetCanceled(cancellation);
        });
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _pending.Where(p => p.Due <= _now).Select(p => p.Done)];
            _pending.RemoveAll(p => p.Due <= _now);
        }
        foreach (var done in due) { done.TrySetResult(); }
    }
}

/// <summary>The dashboard's source with device calls (macOS SA3DFakeSource,
/// SelfTest.swift :25538-25720): the calls are recorded, can fail, and can be
/// held until released. Pages carry a tag (mode and year) in a model name so a
/// test can tell which response a slot ended up with. Never a native call.</summary>
internal sealed class FakeDeviceSource : IDeviceSource
{
    public const string RemoteClient = "sa3d-remote-client";
    public const string RemoteYear = "1999";
    public const string RemoteModel = "sa3d-remote-model";
    public const string OverlayGeneratedAt = "2099-01-01T00:00:00Z";
    public const string DeviceId = "aaaaaaaaaaaaaaaa";

    /// <summary>One device call; lists are joined so records compare by value.</summary>
    public sealed record Call(string Kind, DeviceFetchMode Mode, string? Year, string? Clients, bool Local, string Peers)
    {
        public string Tag => $"{Kind}-{(Mode == DeviceFetchMode.Cached ? "cached" : "refresh")}";
    }

    public sealed class FakeFailure : Exception;

    public sealed class State
    {
        public IReadOnlyList<RemotePeer> Peers = [];
        public Exception? PeersError;
        public List<Call> Calls = [];
        public int PeerCalls;
        public int RefreshStarted;
        public bool HoldRefresh;
        public bool HoldCached;
        public bool FailRefresh;
        public bool FailAll;
        public bool FailModels;
        /// <summary>What a failed call throws (timezoneUnavailable, for one).</summary>
        public Exception Failure = new FakeFailure();
        public string RemoteState = "fresh";
        public string? RemoteReason;
        /// <summary>The device row's label; null is the address.</summary>
        public string? RemoteLabel;
        public bool RemoteCostIncomplete;
        public bool RemoteEverAnswered = true;
        public bool Saturated;
        public PricingMode Pricing = PricingMode.LocalOnly;
        public Dictionary<string, string> RemoteStateByKind = [];
    }

    private readonly object _lock = new();
    private readonly State _state = new();
    private readonly SemaphoreSlim _gate = new(0);
    private readonly SemaphoreSlim _cachedGate = new(0);

    public void Set(Action<State> change) { lock (_lock) { change(_state); } }
    public T Get<T>(Func<State, T> read) { lock (_lock) { return read(_state); } }
    public void Release(int count = 1) => _gate.Release(count);
    public void ReleaseCached(int count = 1) => _cachedGate.Release(count);

    public IReadOnlyList<Call> Calls => Get(s => (IReadOnlyList<Call>)[.. s.Calls]);
    public IReadOnlyList<Call> Refreshes => Get(s => (IReadOnlyList<Call>)[.. s.Calls.Where(c => c.Mode == DeviceFetchMode.Refresh)]);
    public int RefreshStarted => Get(s => s.RefreshStarted);

    public IReadOnlyList<RemotePeer> RemotePeers()
    {
        lock (_lock)
        {
            _state.PeerCalls++;
            return _state.PeersError is { } error ? throw error : _state.Peers;
        }
    }

    /// <summary>Records the call, holds a refresh while <c>HoldRefresh</c>, then
    /// fails it when asked to. Returns the tag the page carries.</summary>
    private string Enter(
        string kind, string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode)
    {
        bool hold, holdCached;
        lock (_lock)
        {
            _state.Calls.Add(new Call(
                kind, mode, year, clients is null ? null : string.Join(",", clients),
                selection.Local, string.Join(",", selection.Peers)));
            if (mode == DeviceFetchMode.Refresh) { _state.RefreshStarted++; }
            hold = mode == DeviceFetchMode.Refresh && _state.HoldRefresh;
            holdCached = mode == DeviceFetchMode.Cached && _state.HoldCached;
        }
        // A bounded wait: a test that forgets to release fails on its own
        // assertion instead of hanging the run.
        if (hold) { _gate.Wait(TimeSpan.FromSeconds(20)); }
        if (holdCached) { _cachedGate.Wait(TimeSpan.FromSeconds(20)); }
        Exception? failure = null;
        lock (_lock)
        {
            if (_state.FailAll || (mode == DeviceFetchMode.Refresh && _state.FailRefresh)
                || (kind == "models" && _state.FailModels))
            {
                failure = _state.Failure;
            }
        }
        if (failure is not null) { throw failure; }
        return $"{(mode == DeviceFetchMode.Cached ? "cached" : "refresh")}-{year ?? "all"}";
    }

    private IReadOnlyList<RemoteDevice> Devices(string kind)
    {
        string state, address = "10.0.0.2:47000";
        string? reason, label;
        bool incomplete, answered;
        lock (_lock)
        {
            state = _state.RemoteStateByKind.GetValueOrDefault(kind) ?? _state.RemoteState;
            reason = _state.RemoteReason;
            label = _state.RemoteLabel ?? address;
            incomplete = _state.RemoteCostIncomplete;
            answered = _state.RemoteEverAnswered;
        }
        return
        [
            new RemoteDevice("local", "local"),
            new RemoteDevice(DeviceId, state, label, 1_700_000_000, reason, null, address, incomplete, answered),
        ];
    }

    private DeviceAwarePayload<T> Page<T>(string kind, T payload) =>
        new(payload, Devices(kind), DuplicateWarning: true, Saturated: Get(s => s.Saturated), RemoteRangeLimited: false);

    private static readonly TokenBreakdown One = new(1, 0, 0, 0, 0);

    public DeviceAwarePayload<UsagePayload> GraphDevices(string? year, DeviceSelection selection, DeviceFetchMode mode)
    {
        var tag = Enter("graph", year, null, selection, mode);
        string[] clients = ["claude", RemoteClient];
        var days = new[] { year ?? RemoteYear, RemoteYear }.Distinct().Order(StringComparer.Ordinal).Select(y =>
            new Contribution(
                $"{y}-01-02", new ContributionTotals(10, 2.0, 2), 1, One,
                [.. clients.Select(c => new ContributionClient(c, tag, "test", One, 1.0, 1))])).ToList();
        var years = days.Select(d => d.Date[..4]).Distinct().Select(y =>
            new YearMeta(y, 10, 2.0, new DateRange($"{y}-01-01", $"{y}-12-31"))).ToList();
        return Page("graph", new UsagePayload(
            new UsageMeta(OverlayGeneratedAt, "test", new DateRange("1999-01-01", "2099-01-01"),
                Get(s => s.Pricing), CostCoverage.Complete),
            new UsageSummary(20, 4.0, 2, 2, 2.0, 2.0, clients, [tag]),
            years, days));
    }

    public DeviceAwarePayload<ModelReport> ModelReportDevices(string? year, DeviceSelection selection, DeviceFetchMode mode)
    {
        var tag = Enter("models", year, null, selection, mode);
        var rows = new[] { "claude", RemoteClient }.Select(c =>
            new ModelReportEntry(c, $"{RemoteModel}-{tag}", "test", 1, 0, 0, 0, 0, 1, 1, 1.0)).ToList();
        return Page("models", new ModelReport(rows, 2, 0, 0, 0, 2, 2.0));
    }

    public DeviceAwarePayload<HourlyReport> HourlyReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode)
    {
        var tag = Enter("hourly", year, clients, selection, mode);
        return Page("hourly", new HourlyReport(
            [new HourlyReportEntry("2099-01-01 10:00", ["claude", RemoteClient], [tag], 1, 0, 0, 0, 0, 1, 1, 1, 1.0)],
            1.0));
    }

    public DeviceAwarePayload<AgentsReport> AgentsReportDevices(
        string? year, IReadOnlyList<string>? clients, DeviceSelection selection, DeviceFetchMode mode)
    {
        var tag = Enter("agents", year, clients, selection, mode);
        return Page("agents", new AgentsReport(
            [new AgentReportEntry(tag, ["claude", RemoteClient], 1, 0, 0, 0, 0, 1, 1.0, 1)], 1.0, 1));
    }
}
