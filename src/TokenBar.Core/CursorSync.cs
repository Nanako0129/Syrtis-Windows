using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Cursor usage synced from the signed-in Cursor desktop app: the preferences
/// the app owns, the rules the Settings section and the Overview notice draw
/// from, and the copy. Port of macOS <c>CursorSync.swift</c> (Syrtis #488).
/// <para>
/// The core switch (<c>tb_set_cursor_sync</c>) is in-memory and starts off
/// every launch, so the settings file is the source of truth and
/// <see cref="CursorSyncController.Reconfigure"/> re-applies it. Every sync
/// dir is chosen by the native side (Plan W1); nothing here names a Syrtis
/// path. Added Cursor profiles (CMP-W) have their own local-only store,
/// <see cref="CursorProfileStore"/>, which W1 defines but does not push: the
/// push carries <c>profiles: []</c> until W2 wires the store in behind its
/// disclosure (WB11).
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
    /// (<see cref="CursorSyncController.TurnOn"/>). No other controller call
    /// writes it, and none of them stores the preference on, so nothing syncs
    /// (<see cref="ShouldSync"/>) until one of those was answered.</summary>
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

    /// <summary>App-side state: the core refused the newest on push, so
    /// nothing syncs (neither the schedule nor Sync Now) until a push is
    /// accepted. A profile the user removed is therefore never walked by the
    /// configuration the core kept.</summary>
    public const string ConfigRejectedState = "configRejected";

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
        /// <summary><see cref="Error"/> plus the core's fixed reason code, so a
        /// screenshot can be diagnosed (#547; macOS <c>errorWithReason</c>, whose
        /// <c>%@</c> is {0} here).</summary>
        public const string ErrorWithReason = "Cursor usage couldn't be synced ({0}). Syrtis will try again later.";
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
        /// <summary>The core refused the settings (<see cref="ConfigRejectedState"/>):
        /// nothing syncs. macOS #532 wording.</summary>
        public const string ConfigRejected = "Cursor sync couldn't be set up, so nothing is being synced.";
        // --- Cursor 3.27+ encrypted sign-in consent (S3b, U3) ----------------
        // DRAFT copy: the disclosure (C4) is final only after the maintainer
        // approves it. Windows has no OS dialog (DPAPI is silent), so this
        // in-app confirmation is the only gate. No "Keychain" wording.

        /// <summary>The status line when the core reports
        /// <c>keychain_consent</c>: a sealed sign-in and no "cursor" grant yet
        /// (or it was withdrawn). The Allow control sits beside it.</summary>
        public const string KeychainConsentCode = "keychain_consent";
        public const string KeychainConsentLine =
            "Cursor stores its sign-in encrypted. Syrtis needs your permission to read it.";

        /// <summary>The button that opens the disclosure confirmation.</summary>
        public const string KeychainAllowAccess = "Allow reading Cursor's encrypted sign-in…";

        /// <summary>The disclosure shown before Allow (C4 required content:
        /// the Local State key decrypts everything Cursor stores encrypted;
        /// Windows does not ask, so this is the only check; only the sign-in
        /// token is used; the key stays in memory for that read only; the
        /// sign-in is sent only to cursor.com; Withdraw stops reading the
        /// encrypted sign-in and does not stop reading an unencrypted
        /// one).</summary>
        public const string KeychainDisclosure =
            "Cursor 3.27 and later can store its sign-in encrypted. To read it, Syrtis unlocks the key Cursor keeps in its \"Local State\" file on this PC. Windows does not ask, so this confirmation is the only check. That key can decrypt everything Cursor stores encrypted, but Syrtis uses it only to read your Cursor sign-in, keeps it in memory for that read alone, and never saves or logs it. The sign-in is sent only to Cursor's usage service (cursor.com). Turning on Cursor sync does not grant this. Withdraw stops Syrtis reading Cursor's encrypted sign-in; if Cursor stores its sign-in unencrypted, Syrtis still reads that and Withdraw does not change it.";

        /// <summary>The Withdraw control shown once the grant is stored.</summary>
        public const string KeychainWithdraw = "Withdraw";

        /// <summary>The confirmation's Allow button: an English source string
        /// (localized once, by the flyout). Not the semantic
        /// <c>consent.action.allow</c> key, which renders as the key itself
        /// in English through <c>Localized()</c>, and not bare "Allow", the
        /// status badge ("待授權").</summary>
        public const string KeychainAllow = "Allow access";

        /// <summary>Second consent paragraph once added profiles exist
        /// (<see cref="ConsentParagraphs"/>), macOS #535 wording. W2 shows it;
        /// each sentence is held to the code there.</summary>
        public const string ProfilesPrivacy =
            "Add a folder you start Cursor with using --user-data-dir to sync it too (Syrtis keeps the folder path in its settings); each profile's sign-in is handled as described above. All profiles add up to one Cursor total, and an account signed in to several profiles counts once. Removing a profile deletes its downloaded usage the next time Syrtis can.";

        /// <summary>The info flyout's rows. The privacy paragraph is also what
        /// the enable confirmation and the first-run notice card show.</summary>
        public static IEnumerable<(string Text, bool Heading)> InfoRows() => [(Privacy, false)];

        // --- Cursor Billing V2 (plan CURSOR-BV2, slice BV2-W) -----------------
        // macOS #554 copy verbatim (%@ -> {0}). Each keeps the core's code on
        // the #547 line, so a screenshot can be diagnosed.

        /// <summary><c>billing_v2_team_required</c>: V2 answered
        /// <c>missing_team_id</c>.</summary>
        public const string BillingV2TeamRequired =
            "Cursor's new billing asks for a team for this account, which Syrtis can't read yet ({0}).";

        /// <summary><c>billing_v2_unavailable</c>: V2 answered another
        /// <c>availability</c>.</summary>
        public const string BillingV2Unavailable =
            "Cursor's new billing didn't provide this account's usage ({0}). Syrtis will try again later.";

        /// <summary><c>billing_v2_no_items</c>: no earlier usage and V2
        /// returned none.</summary>
        public const string BillingV2NoItems =
            "Cursor's new billing has no usage for this account yet ({0}). Syrtis will try again later.";

        /// <summary><c>billing_v2_principal_mismatch</c>: a V2 item was neither
        /// on the cookie's user id nor on the walk's one other <c>type: user</c>
        /// principal (a second other user, another id that is not
        /// <c>type: user</c>, an empty id, a missing principal); nothing
        /// saved.</summary>
        public const string BillingV2PrincipalMismatch =
            "Cursor returned usage of another account, so Syrtis saved nothing ({0}).";

        public static readonly string[] All =
        [
            Title, Toggle, Privacy, Continue, Cancel, TurnOff, LastSynced, Partial, Expired, NotSignedIn,
            Offline, Error, ErrorWithReason, CliPresent, SyncNow, Syncing, CliQuestion, UseSyrtis, KeepCli, CleanupFailed,
            ConfigRejected, ProfilesPrivacy,
            KeychainConsentLine, KeychainAllowAccess, KeychainDisclosure, KeychainWithdraw, KeychainAllow,
            BillingV2TeamRequired, BillingV2Unavailable, BillingV2NoItems, BillingV2PrincipalMismatch,
        ];
    }

    /// <summary>The Billing V2 line for one of the core's four fixed
    /// <c>billing_v2_*</c> codes, or null for any other reason.</summary>
    private static string? BillingV2Copy(string? reason) => reason switch
    {
        "billing_v2_team_required" => Copy.BillingV2TeamRequired,
        "billing_v2_unavailable" => Copy.BillingV2Unavailable,
        "billing_v2_no_items" => Copy.BillingV2NoItems,
        "billing_v2_principal_mismatch" => Copy.BillingV2PrincipalMismatch,
        _ => null,
    };

    /// <summary>What a consent surface shows: the privacy paragraph, plus the
    /// added profiles' paragraph once any folder is stored (they are read
    /// too). macOS <c>consentParagraphs</c>; W2 wires it to the store.</summary>
    public static IReadOnlyList<string> ConsentParagraphs(int addedProfiles) =>
        addedProfiles > 0 ? [Copy.Privacy, Copy.ProfilesPrivacy] : [Copy.Privacy];

    /// <summary>The status line for a core state; null where there is nothing
    /// to say (<c>disabled</c>, not yet synced, or <c>ok</c> with no recorded
    /// time). The relative time is this app's own (<see cref="Format.RelativeTime"/>).
    /// For <c>error</c>, <paramref name="reason"/> is shown only if it looks
    /// like one of the core's fixed codes (<c>^[a-z_]{1,40}$</c>), so the line
    /// never carries free text (macOS <c>statusLine</c>, #547).</summary>
    public static string? StatusLine(
        string? state, long? lastSuccessMs, DateTimeOffset? now = null, string? reason = null) =>
        state switch
        {
            "ok" when lastSuccessMs is { } ms =>
                Copy.LastSynced.Localized(Format.RelativeTime((ulong)Math.Max(0, ms / 1000), now)),
            "partial" => Copy.Partial.Localized(),
            "expired" => Copy.Expired.Localized(),
            "notSignedIn" => Copy.NotSignedIn.Localized(),
            "offline" => Copy.Offline.Localized(),
            // A sealed sign-in with no "cursor" grant: the consent prompt, not
            // the generic error-with-reason line (the Allow control sits beside
            // it). Checked before the reason-code branch, which would otherwise
            // match this lowercase code.
            "error" when reason == Copy.KeychainConsentCode => Copy.KeychainConsentLine.Localized(),
            // Billing V2: its own line, still carrying the code (macOS #554).
            "error" when BillingV2Copy(reason) is { } billing => billing.Localized(reason!),
            // A char test, not a Regex: .NET's `$` also matches before a final "\n".
            "error" when reason is { Length: >= 1 and <= 40 } && reason.All(c => c is '_' or >= 'a' and <= 'z') =>
                Copy.ErrorWithReason.Localized(reason),
            "error" => Copy.Error.Localized(),
            "cliPresent" => Copy.CliPresent.Localized(),
            ConfigRejectedState => Copy.ConfigRejected.Localized(),
            _ => null,
        };

    /// <summary>ok is drawn in the secondary colour, every other line in
    /// orange (shared spec §2).</summary>
    public static bool StatusIsWarning(string? state) => state != "ok";

    /// <summary>The Cursor section needs the Allow control: a sealed sign-in
    /// with no "cursor" grant (the core reports <c>keychain_consent</c>, U3).
    /// Unlike macOS there is no <c>keychain_denied</c>/<c>keychain_unlock</c>:
    /// Windows has no OS dialog and no first-round rule.</summary>
    public static bool NeedsKeychainAllow(string? state, string? reason) =>
        state == "error" && reason == Copy.KeychainConsentCode;
}

/// <summary>
/// The schedule and the ordering rules around the core calls (macOS
/// <c>CursorSyncController</c> with the #488 fixes): config pushes reach the
/// core in the order they were made; a sync waits for the newest push; a
/// result that returns after a newer configuration is discarded and the sync
/// reruns; a refused on push stops every sync until a push is accepted
/// (<see cref="CursorSync.ConfigRejectedState"/>).
/// Every native call runs on the thread pool; state lives under one lock.
/// </summary>
public sealed class CursorSyncController
{
    /// <summary>The native calls, replaced by fakes in tests. Each throws
    /// <see cref="TbCoreException"/> (a fixed code) on an error envelope.</summary>
    public sealed record Io(
        Func<string, CursorSyncConfig> SetConfig,
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
    // Bumped by ConsentChanged, with the answer it reported: an Allow during a
    // round the core refused for want of consent, or that threw, reruns it as
    // a user round.
    private int _consentEpoch;
    private bool _consentGranted;
    private bool _rerunPending;
    private bool _syncing;
    // The running pass is a user press, not the schedule.
    private bool _runningUser;
    // A press arrived during a scheduled pass; one user sync follows it.
    private bool _userPending;
    private string? _state;
    // The core's fixed reason code for the last round, written with every
    // "error" state (null when the round threw); StatusLine checks its shape.
    private string? _reason;
    private long? _lastSuccessMs;
    private bool _cleanupFailed;
    // While on: the last accepted on push could not clean a profile it no
    // longer lists, or the last round could not delete one of a profile's
    // files (cleanupFailedProfiles). Cleared by an off push.
    private bool _pushProfileCleanupFailed;
    private bool _roundProfileCleanupFailed;
    private int _offPushesPending;
    private bool _cursorPresent;

    /// <summary>Raised after the state, Syncing, CleanupFailed or the Cursor
    /// presence changes (Settings and the Overview redraw). On any thread;
    /// handlers must not block.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when what the dashboard shows may have changed (a sync
    /// round in which some profile's file content changed, <c>changed</c>, or a
    /// toggle / D6 answer): the core already dropped its caches, so the app
    /// only re-asks.</summary>
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

    /// <summary>The last round's <see cref="CursorSyncStatus.Reason"/> (#547).</summary>
    public string? Reason { get { lock (_gate) { return _reason; } } }

    public long? LastSuccessMs { get { lock (_gate) { return _lastSuccessMs; } } }

    public bool Syncing { get { lock (_gate) { return _syncing; } } }

    /// <summary>The last finished off push failed its cleanup
    /// (<c>cleanupFailed</c>) and no newer off push is in flight. A retry
    /// therefore never shows the previous failure: the line is hidden while
    /// the new push runs and then follows that push's outcome (a success
    /// clears it). In memory only: the next launch pushes off again while sync
    /// is off, which is the retry the copy ("Restart Syrtis to retry") names.</summary>
    public bool CleanupFailed { get { lock (_gate) { return _cleanupFailed && _offPushesPending == 0; } } }

    /// <summary>While sync is on, some Syrtis usage file of a profile could
    /// not be deleted: by the last accepted on push (a profile it no
    /// longer lists, <c>cleanupFailed</c> in its result) or by the last round
    /// (<c>cleanupFailedProfiles</c> in its status). Not shown by W1: the
    /// <see cref="CleanupFailed"/> line's copy is about sync being off, and an
    /// on-state line needs its own approved copy (W2).</summary>
    public bool ProfileCleanupFailed
    {
        get { lock (_gate) { return _pushProfileCleanupFailed || _roundProfileCleanupFailed; } }
    }

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
        // WB11: no added profile is pushed until W2 reads CursorProfileStore
        // behind its disclosure.
        var json = CursorSyncRequest.Json(run, CursorSync.TakeoverConfirmed(_store), []);
        Task push;
        CancellationToken token;
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = new CancellationTokenSource();
            token = _loop.Token;
            _generation++;
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
            }

            // Each push waits for the one before it, so rapid toggles reach
            // the core in the order they were made. Push never throws.
            var generation = _generation;
            push = _configPush.ContinueWith(
                _ => Push(io, json, run, generation), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _configPush = push;
        }

        StateChanged?.Invoke();
        _ = Loop(push, refresh, run, token).ContinueWith(
            t => _log($"cursor-sync: schedule failed {t.Exception?.GetBaseException().GetType().Name}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <param name="generation">The <see cref="Reconfigure"/> this push
    /// belongs to: a refusal is applied only while that is still the newest
    /// configuration, so an on push refused after a later Reconfigure (sync
    /// turned off meanwhile) cannot leave configRejected behind.</param>
    private void Push(Io io, string json, bool enabled, int generation)
    {
        // The off push's cleanup outcome; null = no answer (another error),
        // which keeps the previous one. On off the core can only fail with
        // cleanupFailed, invalidJson (not reachable: this side builds the JSON)
        // or a panic; storageUnavailable is enable-only (cursor_sync.rs
        // set_with_bases), so "another error" here means a panic.
        bool? failed = null;
        // An on push's outcome: refused (any error) stops every sync until an
        // accepted push; the core then keeps only profiles this push listed.
        var rejected = false;
        var profileCleanupFailed = false;
        try
        {
            profileCleanupFailed = io.SetConfig(json).CleanupFailed;
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
            rejected = enabled;
        }
        finally
        {
            if (enabled)
            {
                lock (_gate)
                {
                    if (rejected)
                    {
                        if (generation == _generation)
                        {
                            _state = CursorSync.ConfigRejectedState;
                        }
                    }
                    else
                    {
                        _pushProfileCleanupFailed = profileCleanupFailed;
                        if (_state == CursorSync.ConfigRejectedState)
                        {
                            _state = null;
                        }
                    }
                }
            }
            else
            {
                // Outcome and end of pending in one step, so the line never
                // flashes the previous answer between them.
                lock (_gate)
                {
                    if (failed is { } outcome)
                    {
                        _cleanupFailed = outcome;
                    }

                    _pushProfileCleanupFailed = false;
                    _roundProfileCleanupFailed = false;

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

        // A refused on push stops the schedule (configRejected).
        if (!run || State == CursorSync.ConfigRejectedState)
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
    /// and the sync reruns, so the new settings get a fresh result.
    /// A second exception to single-flight is <see cref="ConsentChanged"/>: an
    /// Allow pressed while a round runs, when that round ends refused for want
    /// of the "cursor" consent, applies that round's result (its writes are real,
    /// so <see cref="DataChanged"/> still fires on <c>changed</c>); when it
    /// throws there is no result: the state is "error" with a null reason, no
    /// refresh happens and <see cref="DataChanged"/> does not fire. Either way
    /// it then runs one user round, which also serves a remembered Sync Now.</summary>
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
                int startedConsent;
                Task push;
                lock (_gate)
                {
                    _rerunPending = false;
                    // Read the generation BEFORE waiting: a settings change
                    // during the wait makes this pass stale.
                    started = _generation;
                    startedConsent = _consentEpoch;
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
                    // The core refused the newest settings: nothing is
                    // sent, a press included (configRejected).
                    if (!CursorSync.ShouldSync(_store) || _state == CursorSync.ConfigRejectedState)
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
                bool consentRerun;
                lock (_gate)
                {
                    // Allow pressed while this ran, and this round was refused
                    // for want of that consent (keychain_consent) or threw (no
                    // result says it did not depend on the old answer): it
                    // reruns. A plaintext round that synced never read
                    // consent, and a Withdraw needs no rerun (a round that
                    // had not yet opened the sealed sign-in already reports
                    // keychain_consent; one that had, read it under the yes
                    // then in force and reports true data).
                    consentRerun = startedConsent != _consentEpoch && _consentGranted
                        && (result is null || AskedForConsent(result));
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
                        _reason = null;
                    }
                    else
                    {
                        _state = result.State;
                        _reason = result.Reason;
                        _lastSuccessMs = result.LastSuccessMs;
                        _roundProfileCleanupFailed = result.CleanupFailedProfiles is { Count: > 0 };
                        // A round that changed a file already invalidated
                        // the core's caches; refresh on `changed`, not on the
                        // headline (one profile can write while another
                        // fails) and not on the event count (another profile
                        // can write the same count). An identical rewrite is
                        // not `changed` in the core. A round rerun for consent
                        // is still applied: what it wrote is real.
                        refresh = result.Changed;
                    }

                    if (consentRerun)
                    {
                        _rerunPending = true;
                    }

                    again = _rerunPending && CursorSync.ShouldSync(_store);
                    if (again && consentRerun)
                    {
                        // The Allow press is served as a user round, and that
                        // round also serves a Sync Now remembered meanwhile.
                        user = true;
                        _runningUser = true;
                        _userPending = false;
                    }

                    if (!again)
                    {
                        // The pending press is served only once nothing else
                        // is owed. _syncing stays set, so Syncing is true
                        // across both runs. No ShouldSync check here. Either
                        // order is covered: Reconfigure clears the flag when
                        // sync goes off; and every writer (TurnOff, Enable,
                        // AnswerNotice, SetTakeoverConfirmed) stores before it
                        // calls Reconfigure, so a pass can be
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

    /// <summary>The Cursor encrypted-sign-in answer changed to
    /// <paramref name="granted"/> (Allow or Withdraw in Settings; the caller has
    /// already installed it through <see cref="KeychainConsentComposer"/>).
    /// With no round running, one user round starts now. While a round runs:
    /// an Allow whose round ends refused for want of consent
    /// (<c>keychain_consent</c>) or throws is served by one user round right
    /// after it (an exception to single-flight; a plain Sync Now during a user
    /// round is dropped). Otherwise the press adds no round, and the running
    /// round ends as it would have without it: a Sync Now remembered during a
    /// scheduled round still runs one user round after it, and a reconfigure
    /// during it still reruns it (see <see cref="RunSync"/>).</summary>
    public Task ConsentChanged(bool granted)
    {
        lock (_gate)
        {
            _consentEpoch++;
            _consentGranted = granted;
            if (_syncing)
            {
                return Task.CompletedTask;
            }
        }

        return RunSync(userInitiated: true);
    }

    /// <summary>The round, headline or any profile, was refused because the
    /// "cursor" consent was missing.</summary>
    private static bool AskedForConsent(CursorSyncStatus? result) =>
        result is not null
        && (result.Reason == CursorSync.Copy.KeychainConsentCode
            || result.Profiles?.Any(p => p.Reason == CursorSync.Copy.KeychainConsentCode) == true);

    /// <summary>The Settings switch turned off: stores the preference off.
    /// Only <see cref="TurnOn"/> (consent first) stores it on.</summary>
    public void TurnOff()
    {
        _store.SetBool(CursorSync.EnabledKey, false);
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
        if (!CursorSync.NoticeAcknowledged(_store) && !await confirm())
        {
            return false;
        }

        Enable();
        return true;
    }

    // TurnOn's only write path. Writing the notice again when it is already
    // answered is a no-op (SettingsStore skips equal values). When the notice
    // was unanswered, ShouldSync stays false between the two writes (enabled,
    // not yet answered). If the preference was stored off, NoticeVisible can
    // read true in that moment, since it needs enabled and unanswered.
    private void Enable()
    {
        _store.SetBool(CursorSync.EnabledKey, true);
        _store.SetBool(CursorSync.NoticeKey, true);
        Reconfigure(refresh: true);
    }

    /// <summary>The Overview notice card: Continue
    /// (<paramref name="continuing"/>) or Turn Off. Turn Off stores the
    /// preference off before it answers the notice, so no reader ever sees
    /// <see cref="CursorSync.ShouldSync"/> true between the two writes (the
    /// other order passes through answered plus default on).</summary>
    public void AnswerNotice(bool continuing)
    {
        if (!continuing)
        {
            _store.SetBool(CursorSync.EnabledKey, false);
        }

        _store.SetBool(CursorSync.NoticeKey, true);
        Reconfigure(refresh: false);
    }

    /// <summary>D6: "Use Syrtis Sync" (true) or "Keep tokscale CLI Data" (false).</summary>
    public void SetTakeoverConfirmed(bool confirmed)
    {
        _store.SetBool(CursorSync.TakeoverKey, confirmed);
        Reconfigure(refresh: true);
    }
}
