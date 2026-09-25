using System.Diagnostics;
using System.Threading.Channels;

namespace TokenBar.Core;

/// <summary>Owns one Discord IPC connection: handshake, throttled publishing,
/// bounded reconnection, and a single kill switch. Port of macOS
/// <c>DiscordIPCClient</c> (DiscordIPC.swift :388-1020).
///
/// <para>All mutable connection state lives on ONE worker: a single-reader
/// channel whose items run strictly one at a time, awaits included — the
/// serial <c>DispatchQueue</c> of the macOS client. Pipe reads run on a
/// per-connection read loop that only ever posts what it read back onto the
/// worker, bound to the connection it came from. Nothing in this class logs,
/// and nothing read from a frame is stored except the fixed token
/// <see cref="DiscordIpc.Inbound"/> returns.</para>
///
/// <para>The one piece of state that lives off the worker is consent
/// (granted + epoch), because <see cref="Stop"/> and a reducing
/// <see cref="Publish"/> must invalidate work that is already queued, which a
/// queued block cannot reach back to (DiscordIPC.swift :430-474).</para></summary>
public sealed class DiscordIpcClient
{
    /// <summary>Every duration the client waits on. Injectable so tests run in
    /// milliseconds; <see cref="Production"/> is pinned by a test. The publish
    /// floor is a privacy floor, not a performance knob: sampling frequency is
    /// what turns a presence into a working-hours trace
    /// (DiscordIPC.swift :395-428).</summary>
    public sealed record Timing(
        TimeSpan PublishInterval,
        TimeSpan ReconnectDelay,
        TimeSpan ReadyTimeout,
        TimeSpan ConnectTimeout,
        TimeSpan WriteTimeout);

    /// <summary>15 s floor (the reference implementation's
    /// UPDATE_MIN_INTERVAL_MS), 30 s reconnect delay, 10 s READY deadline,
    /// 1 s connect, 2 s write (the macOS socket's SO_SNDTIMEO).</summary>
    public static Timing Production { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2));

    /// <summary>Consecutive failures before giving up; reset only when a
    /// connection reaches READY, so a peer that accepts and immediately drops
    /// cannot keep this looping forever (DiscordIPC.swift :402-410).</summary>
    public const int MaxReconnectAttempts = 5;

    private readonly Func<TimeSpan, CancellationToken, Task<Stream>> _connect;
    private readonly Timing _timing;
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });

    // Consent — the only state touched off the worker.
    private readonly object _consentGate = new();
    private bool _granted = true;
    private ulong _epoch;

    // Worker-only state below.
    private Connection? _connection;
    private bool _running;
    private bool _abandoned;
    private bool _ready;
    private byte[] _buffer = [];
    private CancellationTokenSource? _reconnectWork;
    private CancellationTokenSource? _throttleWork;
    private CancellationTokenSource? _readyWork;
    private int _attempts;
    private long? _lastSent;
    private DiscordPresence.Payload? _pending;
    private ulong _pendingEpoch;
    private bool _hasPending;
    private DiscordPresence.Payload? _lastSampledPayload;

    // What the CURRENT connection has been given. Two fields rather than one
    // nullable, because null is a payload here — the clear — and "nothing
    // delivered yet" must stay distinct from "a clear was delivered"
    // (DiscordIPC.swift :509-523).
    private bool _deliveredAny;
    private DiscordPresence.Payload? _delivered;

    private string _inboundToken = string.Empty;

    private int _stopRequests;

    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;

        public CancellationTokenSource Reads { get; } = new();
    }

    public DiscordIpcClient(
        Func<TimeSpan, CancellationToken, Task<Stream>> connect, Timing? timing = null)
    {
        _connect = connect;
        _timing = timing ?? Production;
        _ = Task.Run(RunWorkerAsync);
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var item in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await item().ConfigureAwait(false);
            }
            catch
            {
                // A worker item must never end the worker. Nothing is logged:
                // an exception here can carry pipe or frame details.
            }
        }
    }

    private void Post(Func<Task> item) => _work.Writer.TryWrite(item);

    private void Post(Action item) => Post(() =>
    {
        item();
        return Task.CompletedTask;
    });

    /// <summary>Runs <paramref name="work"/> on the worker after
    /// <paramref name="delay"/>, unless the returned source is cancelled first
    /// — checked again on the worker, so a cancel that races the timer still
    /// wins (DispatchWorkItem.cancel semantics).</summary>
    private CancellationTokenSource Schedule(TimeSpan delay, Func<Task> work)
    {
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _ = Task.Delay(delay, token).ContinueWith(
            delayed =>
            {
                if (!delayed.IsCanceled)
                {
                    Post(() => token.IsCancellationRequested ? Task.CompletedTask : work());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return cancel;
    }

    private bool ConsentAllows(ulong epoch)
    {
        lock (_consentGate)
        {
            return _granted && _epoch == epoch;
        }
    }

    private bool ConsentGranted()
    {
        lock (_consentGate)
        {
            return _granted;
        }
    }

    // ── Public surface ───────────────────────────────────────────────────

    /// <summary>Idempotent while live; an abandoned client tries again.
    /// Consent is restored off the worker so a later <see cref="Stop"/> wins
    /// by call order (DiscordIPC.swift :540-562).</summary>
    public void Start()
    {
        lock (_consentGate)
        {
            _granted = true;
        }

        Post(async () =>
        {
            if (_running && !_abandoned)
            {
                return;
            }

            _running = true;
            _abandoned = false;
            _attempts = 0;
            await OpenConnectionAsync().ConfigureAwait(false);
        });
    }

    /// <summary>The kill switch: withdraw consent and retire every queued
    /// payload NOW (off the worker), then on the worker cancel every wake-up,
    /// send the clear and close. Order matters — the clear cannot go through a
    /// closed pipe (DiscordIPC.swift :564-604).</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _stopRequests);
        lock (_consentGate)
        {
            _granted = false;
            _epoch++;
        }

        Post(async () =>
        {
            var wasRunning = _running;
            _running = false;
            _abandoned = false;
            _reconnectWork?.Cancel();
            _throttleWork?.Cancel();
            _readyWork?.Cancel();
            _readyWork = null;
            _hasPending = false;
            _pending = null;
            _lastSampledPayload = null;
            _deliveredAny = false;
            _delivered = null;
            if (wasRunning && _connection is not null)
            {
                await WriteFrameAsync(
                    DiscordIpc.Opcode.Frame, DiscordIpc.ActivityJson(null, Pid, Nonce()))
                    .ConfigureAwait(false);
            }

            Teardown();
        });
    }

    /// <summary>Coalescing, not queueing: only the newest payload is ever
    /// published; null clears. The ticket is captured HERE, off the worker, and
    /// a retiring change bumps the epoch first so every payload queued before
    /// it is refused while this one keeps the bumped value as its own ticket
    /// (DiscordIPC.swift :606-663).</summary>
    public void Publish(DiscordPresence.Payload? payload, DiscordIpc.VisibilityChange visibility)
    {
        ulong ticket;
        lock (_consentGate)
        {
            if (visibility.Retires)
            {
                _epoch++;
            }

            ticket = _epoch;
        }

        Post(async () =>
        {
            if (!_running || !ConsentAllows(ticket))
            {
                return;
            }

            _pending = payload;
            _pendingEpoch = ticket;
            _hasPending = true;
            await FlushAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Completes once everything already queued has run. Quit waits
    /// on it with its own ceiling (≤300 ms), so the clear queued by
    /// <see cref="Stop"/> goes out before the process does
    /// (AppDelegate.swift :468-495).</summary>
    public Task DrainAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(drained.SetResult);
        return drained.Task;
    }

    // ── Test seams (public: Core has no InternalsVisibleTo) ──────────────

    /// <summary>Holds the worker until <paramref name="gate"/> completes, so a
    /// test can pin work as queued-but-not-run.</summary>
    public void HoldForTesting(Task gate) => Post(() => gate);

    /// <summary>How many times <see cref="Stop"/> has been called.</summary>
    public int StopRequestsForTesting => Volatile.Read(ref _stopRequests);

    public Task<bool> IsConnectedForTestingAsync() => Query(() => _connection is not null);

    public Task<bool> IsReadyForTestingAsync() => Query(() => _ready);

    public Task<bool> IsAbandonedForTestingAsync() => Query(() => _abandoned);

    public Task<bool> ReconnectPendingForTestingAsync() =>
        Query(() => _reconnectWork is { IsCancellationRequested: false });

    public Task<string> InboundTokenForTestingAsync() => Query(() => _inboundToken);

    private Task<T> Query<T>(Func<T> read)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => result.SetResult(read()));
        return result.Task;
    }

    // ── Connection ───────────────────────────────────────────────────────

    /// <summary>The one place a connection can come into existence, so the one
    /// place that honours "after Stop(), nothing reconnects" — against CURRENT
    /// consent, read off the worker (DiscordIPC.swift :715-796).</summary>
    private async Task OpenConnectionAsync()
    {
        if (!_running || !ConsentGranted())
        {
            return;
        }

        if (_connection is not null)
        {
            Teardown();
        }

        Stream stream;
        try
        {
            stream = await _connect(_timing.ConnectTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            ScheduleReconnect();
            return;
        }

        var connection = new Connection(stream);
        _connection = connection;
        _buffer = [];
        _ready = false;
        _ = Task.Run(() => ReadLoopAsync(connection));

        if (await WriteFrameAsync(DiscordIpc.Opcode.Handshake, DiscordIpc.HandshakeJson())
                .ConfigureAwait(false))
        {
            ArmReadyDeadline();
        }
    }

    /// <summary>A peer that accepts and then stays silent must not hold the
    /// client connected-but-never-ready (DiscordIPC.swift :798-811).</summary>
    private void ArmReadyDeadline()
    {
        _readyWork?.Cancel();
        _readyWork = Schedule(_timing.ReadyTimeout, () =>
        {
            if (_running && !_ready && _connection is not null)
            {
                HandleDisconnect();
            }

            return Task.CompletedTask;
        });
    }

    private void ScheduleReconnect()
    {
        if (_attempts >= MaxReconnectAttempts)
        {
            GiveUp();
            return;
        }

        _attempts++;
        _reconnectWork = Schedule(_timing.ReconnectDelay, OpenConnectionAsync);
    }

    /// <summary>Not <see cref="Stop"/>: no pipe to clear on and not the user
    /// asking. <c>_pending</c> is kept so a later <see cref="Start"/> restores
    /// it (DiscordIPC.swift :830-841).</summary>
    private void GiveUp()
    {
        _abandoned = true;
        _attempts = 0;
        _throttleWork?.Cancel();
        _throttleWork = null;
        _reconnectWork?.Cancel();
        _reconnectWork = null;
        Teardown();
    }

    private void Teardown()
    {
        _ready = false;
        _deliveredAny = false;
        _delivered = null;
        _readyWork?.Cancel();
        _readyWork = null;
        _buffer = [];
        if (_connection is { } connection)
        {
            _connection = null;
            connection.Reads.Cancel();
            try
            {
                connection.Stream.Dispose();
            }
            catch
            {
                // Closing a pipe whose peer is gone may throw; it is gone
                // either way.
            }
        }
    }

    private void HandleDisconnect()
    {
        Teardown();
        if (_running)
        {
            ScheduleReconnect();
        }
    }

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>Reads off the worker and posts each chunk back to it, bound to
    /// <paramref name="connection"/>: a chunk from a torn-down connection is
    /// dropped rather than fed to its replacement (DiscordIPC.swift
    /// :778-787).</summary>
    private async Task ReadLoopAsync(Connection connection)
    {
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                var count = await connection.Stream.ReadAsync(chunk, connection.Reads.Token)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var bytes = chunk.AsSpan(0, count).ToArray();
                Post(() => ReceiveAsync(connection, bytes));
            }
        }
        catch
        {
            // Falls through to the disconnect below; nothing is logged.
        }

        Post(() =>
        {
            if (_connection == connection)
            {
                HandleDisconnect();
            }
        });
    }

    private async Task ReceiveAsync(Connection connection, byte[] bytes)
    {
        if (_connection != connection)
        {
            return;
        }

        _buffer = [.. _buffer, .. bytes];
        while (true)
        {
            var result = DiscordIpc.Decode(_buffer, out var consumed, out var opcode, out var body);
            _buffer = _buffer[consumed..];
            switch (result)
            {
                case DiscordIpc.DecodeResult.NeedMore:
                    return;
                case DiscordIpc.DecodeResult.Discard:
                    continue;
                case DiscordIpc.DecodeResult.Fatal:
                    HandleDisconnect();
                    return;
            }

            switch (opcode)
            {
                case DiscordIpc.Opcode.Ping:
                    // The peer's own bytes echoed to the peer, length already
                    // bounded; consent-gated like every other write, so after a
                    // withdrawal the clear is the only thing this process sends
                    // (DiscordIPC.swift :886-901).
                    if (ConsentGranted())
                    {
                        await WriteFrameAsync(DiscordIpc.Opcode.Pong, body).ConfigureAwait(false);
                    }

                    break;
                case DiscordIpc.Opcode.Close:
                    HandleDisconnect();
                    return;
                default:
                    _inboundToken = DiscordIpc.Inbound(body);
                    if (_inboundToken == DiscordIpc.ReadyEvent)
                    {
                        _ready = true;
                        // Reset HERE, not on connect (DiscordIPC.swift :909-916).
                        _attempts = 0;
                        _readyWork?.Cancel();
                        _readyWork = null;
                        // A replacement connection starts with no activity on
                        // Discord's side: restore what was last intended.
                        if (_pending is not null)
                        {
                            _hasPending = true;
                        }

                        await FlushAsync().ConfigureAwait(false);
                    }

                    break;
            }

            if (_connection != connection)
            {
                return; // a write above failed and tore this connection down
            }
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────

    /// <summary>DiscordIPC.swift :934-992. Checked against the epoch the
    /// pending payload was recorded under. A clear and a restore skip the
    /// floor — neither carries new information — and only a new sample
    /// advances its clock.</summary>
    private async Task FlushAsync()
    {
        if (!ConsentAllows(_pendingEpoch))
        {
            return;
        }

        if (!_running || !_ready || !_hasPending || _connection is null)
        {
            return;
        }

        if (_deliveredAny && Equals(_delivered, _pending))
        {
            _hasPending = false;
            return;
        }

        var carriesNewInformation = _pending is not null && !Equals(_pending, _lastSampledPayload);
        if (carriesNewInformation && _lastSent is { } lastSent)
        {
            var elapsed = Stopwatch.GetElapsedTime(lastSent);
            if (elapsed < _timing.PublishInterval)
            {
                // Deferred, not dropped.
                _throttleWork?.Cancel();
                _throttleWork = Schedule(_timing.PublishInterval - elapsed, FlushAsync);
                return;
            }
        }

        var payload = _pending;
        if (await WriteFrameAsync(
                DiscordIpc.Opcode.Frame, DiscordIpc.ActivityJson(payload, Pid, Nonce()))
            .ConfigureAwait(false))
        {
            _hasPending = false;
            if (carriesNewInformation)
            {
                _lastSent = Stopwatch.GetTimestamp();
            }

            _lastSampledPayload = payload;
            _deliveredAny = true;
            _delivered = payload;
        }
    }

    /// <summary>Writes one whole frame within the write timeout, or tears the
    /// connection down. Returns whether the bytes went out.</summary>
    private async Task<bool> WriteFrameAsync(DiscordIpc.Opcode opcode, byte[] body)
    {
        if (_connection is not { } connection)
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(_timing.WriteTimeout);
            await connection.Stream.WriteAsync(DiscordIpc.Encode(opcode, body), timeout.Token)
                .ConfigureAwait(false);
            await connection.Stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (_connection == connection)
            {
                HandleDisconnect();
            }

            return false;
        }
    }

    private static int Pid => Environment.ProcessId;

    private static string Nonce() => Guid.NewGuid().ToString();
}
