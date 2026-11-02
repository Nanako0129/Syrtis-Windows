using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Cursor usage synced from the signed-in Cursor desktop app: the preferences
/// the app owns, the rules the Settings section and the Overview notice draw
/// from, and the copy. Port of macOS <c>CursorSync.swift</c> (Syrtis #488).
/// <para>
/// The core switch (<c>tb_set_cursor_sync</c>) is in-memory and starts off
/// every launch, so the settings file is the source of truth and
/// <see cref="CursorSyncController.Reconfigure"/> re-applies it. The sync dir
/// is chosen by the native side (Plan W1); nothing here names a path.
/// </para>
/// </summary>
public static class CursorSync
{
    /// <summary>Default ON (plan D3): read with
    /// <see cref="SettingsStore.GetNullableBool"/> so an absent key and an
    /// explicit off differ.</summary>
    public const string EnabledKey = "tokenbar.cursorSync.enabled";

    /// <summary>The one-time notice was answered. Written only where the
    /// privacy paragraph was shown: <see cref="CursorSyncController.AnswerNotice"/>
    /// (Continue or Turn Off on the Overview card) and Continue in the
    /// confirmation the Settings switch opens
    /// (<see cref="CursorSyncController.TurnOn"/>). Turning the switch on
    /// through <see cref="CursorSyncController.SetEnabled"/> never writes it,
    /// so nothing syncs (<see cref="ShouldSync"/>) until one of those was
    /// answered.</summary>
    public const string NoticeKey = "tokenbar.cursorSync.noticeAcknowledged";

    /// <summary>D6: the user chose Syrtis's own sync over tokscale CLI Cursor
    /// files already on this PC. "Keep tokscale CLI Data" clears it.</summary>
    public const string TakeoverKey = "tokenbar.cursorSync.cliTakeoverConfirmed";

    /// <summary>D4: launch, then every 30 minutes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>The error code <c>tb_set_cursor_sync</c> returns when turning
    /// sync off could not delete every synced file (Plan W7). Sync is off all
    /// the same.</summary>
    public const string CleanupFailedCode = "cleanupFailed";

    public static bool Enabled(SettingsStore store) => store.GetNullableBool(EnabledKey) ?? true;

    public static bool NoticeAcknowledged(SettingsStore store) => store.GetBool(NoticeKey, false);

    public static bool TakeoverConfirmed(SettingsStore store) => store.GetBool(TakeoverKey, false);

    /// <summary>What the Settings switch shows: on only when sync can actually
    /// run. Before the notice is answered nothing syncs, so the default-on
    /// preference alone must not read as "on" with every control inert.
    /// Turning the switch on while unanswered opens a confirmation first, and
    /// only its Continue answers the notice
    /// (<see cref="CursorSyncController.TurnOn"/>).</summary>
    public static bool ToggleShowsOn(bool enabled, bool acknowledged) => enabled && acknowledged;

    /// <summary>The preference and the notice (D3): what the core is told and
    /// what every sync re-checks. Whether this process may call the core at
    /// all is decided once, in <see cref="CursorSyncController"/>'s
    /// constructor (<see cref="IsUserRuntime"/>).</summary>
    public static bool ShouldSync(SettingsStore store) =>
        Enabled(store) && NoticeAcknowledged(store);

    /// <summary>Not one of the non-user run modes (the startup probe, the icon
    /// dump, the synthetic update dialog, the 3D harnesses):
    /// <see cref="DiscordPresence.TestArguments"/>, the list every other
    /// "never in a test run" feature already shares.</summary>
    public static bool IsUserRuntime(IEnumerable<string> arguments) =>
        !arguments.Any(DiscordPresence.TestArguments.Contains);

    /// <summary>The Overview notice: a real session, the preference on, not yet
    /// answered, and Cursor desktop present (<c>tb_cursor_present</c>, an
    /// existence check only). <paramref name="cursorPresent"/> is asked last,
    /// so nothing is probed for a notice that would not show anyway.</summary>
    public static bool NoticeVisible(
        SettingsStore store, IEnumerable<string> arguments, Func<bool> cursorPresent) =>
        IsUserRuntime(arguments) && Enabled(store) && !NoticeAcknowledged(store) && cursorPresent();

    /// <summary>The cleanupFailed line shows under the switch while the switch
    /// reads off. Its copy says sync is off, so it is not shown while the switch
    /// reads on. <paramref name="cleanupFailed"/> is
    /// <see cref="CursorSyncController.CleanupFailed"/>, which is false while a
    /// newer off push is in flight and is then decided by that push alone.</summary>
    public static bool CleanupFailedVisible(bool toggleShowsOn, bool cleanupFailed) =>
        cleanupFailed && !toggleShowsOn;

    /// <summary>Approved copy (shared spec §3, Windows wording: "this PC").
    /// The English text is the catalog key. The privacy sentence must stay
    /// true to the native write whitelist (timestamp, model, kind, token
    /// counts, cost fields, conversationId; docs/cursor-sync.md).</summary>
    public static class Copy
    {
        public const string Title = "Cursor usage sync";
        public const string Toggle = "Sync Cursor usage from the Cursor app";
        /// <summary>The Accounts page section header: a product name, not translated.</summary>
        public const string Header = "Cursor";
        public const string Privacy =
            "To show your Cursor usage, Syrtis reads the login of the Cursor app on this PC and sends it only to Cursor's usage service (cursor.com) to download your usage. Syrtis stores the date, model, token counts, cost and conversation ID of each request on this PC; the login itself is never saved or logged. Turning this off deletes the downloaded usage.";
        public const string Continue = "Continue";
        /// <summary>macOS <c>CursorSync.Copy.cancel</c>: the Settings
        /// confirmation's buttons are Cancel / Continue.</summary>
        public const string Cancel = "Cancel";
        public const string TurnOff = "Turn Off";
        /// <summary>macOS "Last synced %@"; .NET's placeholder is {0}.</summary>
        public const string LastSynced = "Last synced {0}";
        public const string Partial = "Only part of your usage was downloaded. Syrtis will try again.";
        public const string Expired = "Your Cursor login has expired. Open Cursor to refresh it.";
        public const string NotSignedIn = "Sign in to the Cursor app to sync your usage.";
        public const string Offline = "Can't reach Cursor. Syrtis will try again.";
        public const string Error = "Cursor usage couldn't be synced. Syrtis will try again later.";
        public const string CliPresent = "Showing Cursor usage from tokscale CLI.";
        public const string SyncNow = "Sync Now";
        public const string Syncing = "Syncing…";
        public const string CliQuestion =
            "You also have Cursor usage from tokscale CLI on this PC. Use Syrtis's own sync instead? Choose this only if it's the same Cursor account, or that account's usage will no longer be shown.";
        public const string UseSyrtis = "Use Syrtis Sync";
        public const string KeepCli = "Keep tokscale CLI Data";
        /// <summary>Windows only (W7): a disable whose cleanup failed.
        /// Approved 2026-10-08 (shared spec §2). "Restart", not "turn sync on
        /// and off": turning it on would sync with cursor.com before the
        /// retry. The launch off push is the retry (App.StartCursorSync).</summary>
        public const string CleanupFailed =
            "Cursor sync is off, but some downloaded usage couldn't be deleted. Restart Syrtis to retry.";

        /// <summary>The info flyout's rows. The privacy paragraph is also what
        /// the enable confirmation and the first-run notice card show.</summary>
        public static IEnumerable<(string Text, bool Heading)> InfoRows() => [(Privacy, false)];

        public static readonly string[] All =
        [
            Title, Toggle, Privacy, Continue, Cancel, TurnOff, LastSynced, Partial, Expired, NotSignedIn,
            Offline, Error, CliPresent, SyncNow, Syncing, CliQuestion, UseSyrtis, KeepCli, CleanupFailed,
        ];
    }

    /// <summary>The status line for a core state; null where there is nothing
    /// to say (<c>disabled</c>, not yet synced, or <c>ok</c> with no recorded
    /// time). The relative time is this app's own (<see cref="Format.RelativeTime"/>).</summary>
    public static string? StatusLine(string? state, long? lastSuccessMs, DateTimeOffset? now = null) =>
        state switch
        {
            "ok" when lastSuccessMs is { } ms =>
                Copy.LastSynced.Localized(Format.RelativeTime((ulong)Math.Max(0, ms / 1000), now)),
            "partial" => Copy.Partial.Localized(),
            "expired" => Copy.Expired.Localized(),
            "notSignedIn" => Copy.NotSignedIn.Localized(),
            "offline" => Copy.Offline.Localized(),
            "error" => Copy.Error.Localized(),
            "cliPresent" => Copy.CliPresent.Localized(),
            _ => null,
        };

    /// <summary>ok is drawn in the secondary colour, every other line in
    /// orange (shared spec §2).</summary>
    public static bool StatusIsWarning(string? state) => state != "ok";
}

/// <summary>
/// The schedule and the ordering rules around the core calls (macOS
/// <c>CursorSyncController</c> with the #488 fixes): config pushes reach the
/// core in the order they were made; a sync waits for the newest push; a
/// result that returns after a newer configuration is discarded and the sync
/// reruns; off resets the refresh bookkeeping so off→on refreshes again.
/// Every native call runs on the thread pool; state lives under one lock.
/// </summary>
public sealed class CursorSyncController
{
    /// <summary>The native calls, replaced by fakes in tests. Each throws
    /// <see cref="TbCoreException"/> (a fixed code) on an error envelope.</summary>
    public sealed record Io(
        Func<bool, bool, CursorSyncConfig> SetConfig,
        Func<bool, CursorSyncStatus> Sync,
        Func<bool> CursorPresent);

    /// <summary>The production calls.</summary>
    public static Io Native { get; } = new(TbCore.SetCursorSync, TbCore.CursorSync, TbCore.CursorPresent);

    /// <summary>The live instance for this process; set once at launch.</summary>
    public static CursorSyncController? Shared { get; set; }

    private readonly object _gate = new();
    // Null in a non-user run: the only gate between a test mode and the core.
    private readonly Io? _io;
    private readonly SettingsStore _store;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private Task _configPush = Task.CompletedTask;
    private CancellationTokenSource? _loop;
    private int _generation;
    private bool _rerunPending;
    private bool _syncing;
    // The running pass is a user press, not the schedule.
    private bool _runningUser;
    // A press arrived during a scheduled pass; one user sync follows it.
    private bool _userPending;
    private string? _state;
    private long? _lastSuccessMs;
    private long? _lastRefreshedEvents;
    private bool _cleanupFailed;
    private int _offPushesPending;
    private bool _cursorPresent;

    /// <summary>Raised after the state, Syncing, CleanupFailed or the Cursor
    /// presence changes (Settings and the Overview redraw). On any thread;
    /// handlers must not block.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when what the dashboard shows may have changed (a
    /// completed sync with a new event count, or a toggle / D6 answer): the
    /// core already dropped its caches, so the app only re-asks.</summary>
    public event Action? DataChanged;

    /// <param name="arguments">This process's arguments. In a non-user run
    /// (<see cref="CursorSync.IsUserRuntime"/>) the controller keeps no
    /// <see cref="Io"/> and never calls the core: not even an off push, since
    /// off deletes files.</param>
    public CursorSyncController(
        Io io, SettingsStore store, IEnumerable<string> arguments,
        Action<string>? log = null, TimeSpan? interval = null)
    {
        _io = CursorSync.IsUserRuntime(arguments) ? io : null;
        _store = store;
        _log = log ?? (_ => { });
        _interval = interval ?? CursorSync.Interval;
    }

    public string? State { get { lock (_gate) { return _state; } } }

    public long? LastSuccessMs { get { lock (_gate) { return _lastSuccessMs; } } }

    public bool Syncing { get { lock (_gate) { return _syncing; } } }

    /// <summary>The last finished off push failed its cleanup
    /// (<c>cleanupFailed</c>) and no newer off push is in flight. A retry
    /// therefore never shows the previous failure: the line is hidden while
    /// the new push runs and then follows that push's outcome (a success
    /// clears it). In memory only: the next launch pushes off again while sync
    /// is off, which is the retry the copy ("Restart Syrtis to retry") names.</summary>
    public bool CleanupFailed { get { lock (_gate) { return _cleanupFailed && _offPushesPending == 0; } } }

    /// <summary>Cursor desktop's state.vscdb exists; false until
    /// <see cref="ProbeCursorPresent"/> has answered.</summary>
    public bool CursorPresent { get { lock (_gate) { return _cursorPresent; } } }

    /// <summary>Ask the core once, off the calling thread, whether Cursor
    /// desktop is present; the notice reads the answer.
    /// ponytail: probed once per launch, so installing Cursor while Syrtis
    /// runs shows the notice from the next launch; probe again on Overview
    /// open if that matters.</summary>
    public Task ProbeCursorPresent()
    {
        if (_io is not { } io)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            bool present;
            try
            {
                present = io.CursorPresent();
            }
            catch (Exception ex)
            {
                _log($"cursor-sync: presence probe failed {ex.GetType().Name}");
                present = false;
            }

            lock (_gate)
            {
                _cursorPresent = present;
            }

            StateChanged?.Invoke();
        });
    }

    /// <summary>Push the stored preferences into the core, then (re)start the
    /// schedule when sync is allowed. Called at launch (<paramref name="refresh"/>
    /// false: a launch does not force a rescan) and after every preference
    /// change.</summary>
    public void Reconfigure(bool refresh)
    {
        if (_io is not { } io)
        {
            return;
        }

        var run = CursorSync.ShouldSync(_store);
        var takeover = CursorSync.TakeoverConfirmed(_store);
        Task push;
        CancellationToken token;
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = new CancellationTokenSource();
            token = _loop.Token;
            _generation++;
            // Off deletes the synced files, so the next completed sync must
            // refresh even when it writes the same event count again.
            if (!run)
            {
                // A press still remembered (not yet promoted to the running
                // user pass) is dropped when the switch goes off, even if it
                // is turned back on before the pass ends. A press already
                // promoted can rerun under the new config if sync is turned
                // off and back on during that pass.
                _userPending = false;
                _offPushesPending++;
                _state = null;
                _lastSuccessMs = null;
                _lastRefreshedEvents = null;
            }

            // Each push waits for the one before it, so rapid toggles reach
            // the core in the order they were made. Push never throws.
            push = _configPush.ContinueWith(
                _ => Push(io, run, takeover), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _configPush = push;
        }

        StateChanged?.Invoke();
        _ = Loop(push, refresh, run, token).ContinueWith(
            t => _log($"cursor-sync: schedule failed {t.Exception?.GetBaseException().GetType().Name}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void Push(Io io, bool enabled, bool takeover)
    {
        // The off push's cleanup outcome; null = no answer (another error),
        // which keeps the previous one. On off the core can only fail with
        // cleanupFailed, invalidJson (not reachable: this side builds the JSON)
        // or a panic; storageUnavailable is enable-only (cursor_sync.rs
        // set_from_json), so "another error" here means a panic.
        bool? failed = null;
        try
        {
            io.SetConfig(enabled, takeover);
            failed = false;
        }
        catch (TbCoreException ex) when (!enabled && ex.Message == CursorSync.CleanupFailedCode)
        {
            failed = true;
            _log("cursor-sync: cleanupFailed");
        }
        catch (Exception ex)
        {
            // The type name only: a native message can carry a panic payload.
            _log($"cursor-sync: config push failed {ex.GetType().Name}");
        }
        finally
        {
            if (!enabled)
            {
                // Outcome and end of pending in one step, so the line never
                // flashes the previous answer between them.
                lock (_gate)
                {
                    if (failed is { } outcome)
                    {
                        _cleanupFailed = outcome;
                    }

                    _offPushesPending--;
                }
            }
        }

        StateChanged?.Invoke();
    }

    private async Task Loop(Task push, bool refresh, bool run, CancellationToken token)
    {
        await push.ConfigureAwait(false);
        if (refresh)
        {
            DataChanged?.Invoke();
        }

        if (!run)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            await RunSync(userInitiated: false).ConfigureAwait(false);
            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One sync, on the thread pool. Single-flight here as well as in
    /// the core: a request while a sync runs is dropped, since that sync's
    /// result is current. The exception is a user press during a scheduled
    /// sync: the scheduled one has a short budget, so the press is remembered
    /// (one flag, never a queue) and the running call makes one user pass once
    /// it finishes. The press itself returns immediately, before that user pass
    /// runs. Turning sync off drops a press that is still remembered, even if
    /// sync is turned back on before the pass ends; a press already promoted
    /// to the running user pass can rerun under the new config if sync is
    /// turned off and back on during that pass.
    /// Before calling the core it waits for the newest config push; a
    /// reconfigure during that wait makes it wait again, and if sync was
    /// turned off meanwhile it returns without calling the core. A
    /// reconfigure while the core runs makes the result stale; it is discarded
    /// and the sync reruns, so the new settings get a fresh result.</summary>
    public async Task RunSync(bool userInitiated)
    {
        if (_io is not { } io || !CursorSync.ShouldSync(_store))
        {
            return;
        }

        lock (_gate)
        {
            if (_syncing)
            {
                _userPending |= userInitiated && !_runningUser;
                return;
            }

            _syncing = true;
            _runningUser = userInitiated;
        }

        // Whether the current pass is a user press; set again when a remembered
        // press starts its own pass.
        var user = userInitiated;

        // Set once _syncing has been cleared under the lock that decided this
        // pass was the last, so a sync started right after (the new loop of a
        // reconfigure whose push landed meanwhile) is not refused as "already
        // running" with no rerun owed.
        var released = false;
        try
        {
            // Inside the try, so a throwing handler cannot leave _syncing set.
            StateChanged?.Invoke();
            while (true)
            {
                int started;
                Task push;
                lock (_gate)
                {
                    _rerunPending = false;
                    // Read the generation BEFORE waiting: a settings change
                    // during the wait makes this pass stale.
                    started = _generation;
                    push = _configPush;
                }

                // Sync against the newest configuration the core was given.
                await push.ConfigureAwait(false);
                bool waitAgain;
                lock (_gate)
                {
                    // Re-checked after the wait, before anything is sent: the
                    // switch may have been turned off while this waited (a Sync
                    // Now pressed during a pending "on" push), and that answer
                    // wins. Still on but reconfigured: wait for the newer push.
                    if (!CursorSync.ShouldSync(_store))
                    {
                        _syncing = false;
                        _userPending = false;
                        released = true;
                        return;
                    }

                    waitAgain = started != _generation;
                }

                if (waitAgain)
                {
                    continue;
                }

                CursorSyncStatus? result;
                try
                {
                    result = await Task.Run(() => io.Sync(user)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"cursor-sync: sync failed {ex.GetType().Name}");
                    result = null;
                }

                var refresh = false;
                bool again;
                lock (_gate)
                {
                    if (started != _generation || !CursorSync.ShouldSync(_store))
                    {
                        // A reconfigure (turning sync off, say) happened while
                        // this ran: the result describes a configuration that
                        // no longer applies.
                        _rerunPending = true;
                    }
                    else if (result is null)
                    {
                        _state = "error";
                    }
                    else
                    {
                        _state = result.State;
                        _lastSuccessMs = result.LastSuccessMs;
                        if (result.State == "ok" && result.Events != _lastRefreshedEvents)
                        {
                            _lastRefreshedEvents = result.Events;
                            refresh = true;
                        }
                    }

                    again = _rerunPending && CursorSync.ShouldSync(_store);
                    if (!again)
                    {
                        // The pending press is served only once nothing else
                        // is owed. _syncing stays set, so Syncing is true
                        // across both runs. No ShouldSync check here. Either
                        // order is covered: Reconfigure clears the flag when
                        // sync goes off; and SetEnabled, AnswerNotice and
                        // ConfirmEnable write the store before Reconfigure
                        // runs, so a pass can be
                        // promoted after the store says off, in which case
                        // the post-push re-check returns without calling the
                        // core (Syncing stays set through that wait).
                        if (_userPending)
                        {
                            user = true;
                            _runningUser = true;
                            again = true;
                        }

                        _userPending = false;
                    }

                    if (!again)
                    {
                        _syncing = false;
                        released = true;
                    }
                }

                if (refresh)
                {
                    DataChanged?.Invoke();
                }

                if (!again)
                {
                    return;
                }
            }
        }
        finally
        {
            if (!released)
            {
                lock (_gate)
                {
                    _syncing = false;
                    _userPending = false;
                }
            }

            StateChanged?.Invoke();
        }
    }

    // ---- Actions behind the controls ---------------------------------------

    /// <summary>Stores the preference only (the Settings switch turned off).
    /// It never answers the notice, so turning it on this way before the
    /// notice is answered syncs nothing; Settings turns it on only through
    /// <see cref="TurnOn"/>.</summary>
    public void SetEnabled(bool on)
    {
        _store.SetBool(CursorSync.EnabledKey, on);
        Reconfigure(refresh: true);
    }

    /// <summary>Turning the Settings switch on. While the notice is
    /// unanswered, <paramref name="confirm"/> shows the privacy paragraph
    /// first: Cancel or light dismiss (false) stores nothing and calls
    /// nothing; Continue (true) stores the preference on and the notice
    /// answered, then reconfigures once, so the core gets one config push.
    /// Once the notice is answered, it turns on without asking.
    /// This differs from macOS on purpose (maintainer decision 2026-11-02):
    /// macOS <c>switchChanged(to: true)</c> asks on every turn-on, and only its
    /// <c>confirmEnable()</c> enables; Windows asks only while the notice is
    /// unanswered. Returns whether sync was turned on.</summary>
    public async Task<bool> TurnOn(Func<Task<bool>> confirm)
    {
        if (CursorSync.NoticeAcknowledged(_store))
        {
            SetEnabled(true);
            return true;
        }

        if (!await confirm())
        {
            return false;
        }

        ConfirmEnable();
        return true;
    }

    // Continue in the Settings confirmation: both keys, then one Reconfigure.
    private void ConfirmEnable()
    {
        _store.SetBool(CursorSync.EnabledKey, true);
        _store.SetBool(CursorSync.NoticeKey, true);
        Reconfigure(refresh: true);
    }

    /// <summary>The Overview notice card: Continue
    /// (<paramref name="continuing"/>) or Turn Off.</summary>
    public void AnswerNotice(bool continuing)
    {
        _store.SetBool(CursorSync.NoticeKey, true);
        if (!continuing)
        {
            _store.SetBool(CursorSync.EnabledKey, false);
        }

        Reconfigure(refresh: false);
    }

    /// <summary>D6: "Use Syrtis Sync" (true) or "Keep tokscale CLI Data" (false).</summary>
    public void SetTakeoverConfirmed(bool confirmed)
    {
        _store.SetBool(CursorSync.TakeoverKey, confirmed);
        Reconfigure(refresh: true);
    }
}
