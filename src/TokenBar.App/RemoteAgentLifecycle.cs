using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// When the embedded remote-usage agent starts and stops (macOS
/// <c>RemoteAgentLaunch</c>, RemoteAgentLaunch.swift :11-15, :68-108). The
/// agent itself (pairing, grants, sharing) lives in Rust; this only calls
/// <c>tb_remote_start</c> / <c>tb_remote_stop</c>, off the UI thread.
///
/// <para>The app calls <see cref="Launch"/> once at launch, so sharing resumes
/// after every restart (the user's decision, "重裝後分享自動恢復"). Each
/// owner-input push that succeeds (the Claude config directories and scan
/// roots, the Cursor sync configuration) calls <see cref="InputsChanged"/>:
/// <c>tb_remote_start</c> re-reads the scan inputs and restarts the agent only
/// when they changed. Before <see cref="Launch"/> that does nothing (the launch
/// start reads the inputs itself); after <see cref="Stop"/> nothing starts.
/// Starts run one at a time, in order, on the thread pool.</para>
/// </summary>
public sealed class RemoteAgentLifecycle(
    Func<RemoteAgentStatus> start, Func<RemoteAgentStatus> stop, Action<string> log)
{
    /// <summary>How long app exit waits for <c>tb_remote_stop</c>, which can
    /// take up to 15 s. ponytail: a fixed bound, not measured; past it the
    /// process exits anyway and Windows closes the agent's handles (its owner
    /// lock and listener go with the process), so a longer wait buys nothing
    /// but a slow quit.</summary>
    public static readonly TimeSpan StopBound = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private bool _launched;
    private bool _stopped;
    private Task _chain = Task.CompletedTask;

    /// <summary>The live instance; set by <c>App.OnLaunched</c>.</summary>
    public static RemoteAgentLifecycle? Shared { get; set; }

    /// <summary>The launch-time start. <paramref name="before"/> runs first on
    /// the same background thread (the app waits there for the launch push of
    /// the saved Claude roots, as macOS starts after <c>ExtraScanRoots.apply</c>).</summary>
    public void Launch(Action? before = null)
    {
        lock (_gate)
        {
            if (_launched || _stopped) { return; }
            _launched = true;
            Enqueue(before);
        }
    }

    /// <summary>After an owner-input setter succeeded.</summary>
    public void InputsChanged()
    {
        lock (_gate)
        {
            if (_launched && !_stopped) { Enqueue(null); }
        }
    }

    /// <summary>App exit: no further start, then <c>tb_remote_stop</c> on the
    /// thread pool, waited for at most <paramref name="bound"/>. Returns
    /// whether it finished in time.</summary>
    public bool Stop(TimeSpan bound)
    {
        Task stopping;
        lock (_gate)
        {
            if (_stopped) { return true; }
            _stopped = true;
            // After any start already running, so it cannot finish after the
            // stop and leave the agent up; a start not yet begun sees _stopped.
            stopping = _chain.ContinueWith(_ =>
            {
                try
                {
                    stop();
                }
                catch (Exception ex)
                {
                    log($"remote-agent: stop failed {Describe(ex)}");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        var finished = stopping.Wait(bound);
        if (!finished)
        {
            log("remote-agent: stop still running at exit");
        }

        return finished;
    }

    // Under _gate.
    private void Enqueue(Action? before) =>
        _chain = _chain.ContinueWith(_ => RunStart(before), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);

    private void RunStart(Action? before)
    {
        try
        {
            before?.Invoke();
            lock (_gate)
            {
                if (_stopped) { return; }
            }

            start();
        }
        catch (Exception ex)
        {
            // A failure (another process holds the agent root, an unsafe
            // root) leaves the app as it was.
            log($"remote-agent: start failed {Describe(ex)}");
        }
    }

    /// <summary>The core's fixed class string when it is one, else a constant:
    /// the Rust side answers every failure with a class, never a path, token,
    /// Pair URL or address, and anything else is not logged at all.</summary>
    public static string Describe(Exception ex) =>
        ex is TbCoreException { Message: var reason } && IsClassString(reason) ? reason : "(unclassified)";

    /// <summary>ASCII letters only, 1 to 32 of them (macOS :105-108).</summary>
    public static bool IsClassString(string text) =>
        text.Length is > 0 and <= 32 && text.All(char.IsAsciiLetter);
}
