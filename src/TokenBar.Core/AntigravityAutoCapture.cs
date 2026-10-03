using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Captures every Google account agy signs into, without a button press, while
/// the Settings toggle <see cref="EnabledKey"/> is on (off by default), plus
/// the manual Capture and Remove. Port of macOS <c>AntigravityAutoCapture</c>
/// (945dbcc2); Windows has no <c>"present"</c> marker, so there is no
/// "unavailable" state.
/// <para>
/// Trigger: before each quota fetch, <see cref="AntigravityFetch"/> awaits
/// <see cref="PrepareForFetch"/> when the toggle is on. <see cref="Poll"/>
/// reads agy's login marker (attributes only, no secret) and, when it differs
/// from the last marker attempted, runs ONE automatic capture in the core. The
/// marker is recorded before the attempt, so a failure is not retried until
/// the marker changes, the toggle is turned on again, or the user presses
/// Capture.
/// </para>
/// <para>
/// <see cref="Current"/> is the key of the account agy is signed into, bound
/// to the marker it was confirmed under; it drives <see cref="AntigravityDedup"/>.
/// While automatic capture is on it is cleared the moment a new marker is seen
/// (before the attempt) and on a pause, and set by a <c>captured</c> /
/// <c>unchanged</c> attempt. A successful manual Capture sets it whether or not
/// the toggle is on, when agy's marker did not change during the capture.
/// Turning the toggle off keeps it; Remove of that account clears it. It is
/// persisted (<see cref="CurrentKey"/>) across relaunch.
/// </para>
/// <para>
/// One operation at a time: <see cref="Busy"/> covers the automatic attempt,
/// manual Capture and Remove, so a capture can never land between a remove's
/// Credential Manager delete and its list change. State lives under one lock;
/// native calls run on the thread pool outside it.
/// </para>
/// </summary>
public sealed class AntigravityAutoCapture
{
    /// <summary>The native calls, replaced by fakes in tests. Each throws on
    /// an error envelope (<see cref="TbCoreException"/> carrying a fixed
    /// code).</summary>
    public sealed record Io(
        Func<string> Marker,
        Func<IReadOnlyList<string>, AntigravityAutoCaptureResult> AutoCapture,
        Func<AntigravityAccount> Capture,
        Action<string> Remove,
        Action Install);

    public const string EnabledKey = "tokenbar.antigravity.autoCapture";

    /// <summary><c>{"key","marker"}</c>: a hash and a FILETIME, no secret.
    /// Safe to restore without re-reading agy's login, because dedup also
    /// requires the primary card to have been fetched under that marker.</summary>
    public const string CurrentKey = "tokenbar.antigravity.currentAgy";

    /// <summary>The error code recorded for a failure that carried no core
    /// code (it maps to the generic sentence).</summary>
    public const string UnexpectedError = "unexpected";

    /// <summary>The live instance for this process; set once at launch.</summary>
    public static AntigravityAutoCapture? Shared { get; set; }

    private readonly object _gate = new();
    private readonly Io _io;
    private readonly SettingsStore _store;
    private readonly Action<string> _log;
    private string? _lastAttemptedMarker;
    private string? _currentKey;
    private string? _currentMarker;
    private string? _errorCode;
    private bool _busy;
    private bool _checking;
    private bool _pollAgain;
    private bool _paused;

    /// <summary>Raised after Busy, Paused or the error changes (Settings
    /// redraws). On any thread; handlers must not block.</summary>
    public event Action? StateChanged;

    public AntigravityAutoCapture(Io io, SettingsStore store, Action<string>? log = null)
    {
        _io = io;
        _store = store;
        _log = log ?? (_ => { });
        try
        {
            if (store.GetString(CurrentKey) is { Length: > 0 } raw
                && JsonSerializer.Deserialize<Dictionary<string, string>>(raw) is { } stored
                && stored.TryGetValue("key", out var key)
                && stored.TryGetValue("marker", out var marker)
                && !string.IsNullOrEmpty(key)
                && !string.IsNullOrEmpty(marker))
            {
                _currentKey = key;
                _currentMarker = marker;
            }
        }
        catch (JsonException)
        {
            // Unreadable: no binding, exactly as if none was stored.
        }
    }

    public bool IsEnabled => _store.GetBool(EnabledKey, false);

    public bool Busy { get { lock (_gate) { return _busy; } } }

    public bool Paused { get { lock (_gate) { return _paused; } } }

    /// <summary>The fixed code of the last manual Capture or Remove that
    /// failed; shown only through the sentence map, never raw.</summary>
    public string? LastErrorCode { get { lock (_gate) { return _errorCode; } } }

    public string? LastAttemptedMarker { get { lock (_gate) { return _lastAttemptedMarker; } } }

    /// <summary>agy's current account and the marker it was confirmed under.</summary>
    public (string? Key, string? Marker) Current { get { lock (_gate) { return (_currentKey, _currentMarker); } } }

    /// <summary>One check, from a quota fetch or right after the toggle turns
    /// on. The caller gates on the toggle; this only refuses to overlap and to
    /// run while paused. A poll refused because something is running is owed,
    /// and runs when that work ends.</summary>
    public async Task Poll(string? known = null)
    {
        var marker = known;
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            if (_busy || _checking)
            {
                _pollAgain = true;
                return;
            }

            // The marker check alone is not Busy: Settings shows "Capturing…"
            // only for an actual capture.
            if (marker is null)
            {
                _checking = true;
            }
        }

        if (marker is null)
        {
            marker = await TryMarker().ConfigureAwait(false);
            lock (_gate)
            {
                _checking = false;
            }
        }

        bool changed;
        lock (_gate)
        {
            if (marker is null || marker == _lastAttemptedMarker || _busy)
            {
                changed = false;
                marker = null;
            }
            else
            {
                _busy = true;
                // Both before the attempt: the old key may not be agy's account
                // any more, and a failed attempt must not be retried for this
                // marker.
                changed = ClearCurrentLocked();
                _lastAttemptedMarker = marker;
            }
        }

        if (marker is null)
        {
            await PollIfOwed().ConfigureAwait(false);
            return;
        }

        Notify(changed);
        changed = false;
        var removed = AntigravityAccounts.RemovedKeys(_store);
        AntigravityAutoCaptureResult? outcome = null;
        string? code = null;
        try
        {
            outcome = await Task.Run(() => _io.AutoCapture(removed)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            code = CodeOf(ex);
            _log($"antigravity auto-capture failed: {ex.GetType().Name}");
        }

        if (outcome is { Status: ("captured" or "unchanged") and var status, Key: { } key, Label: { } label })
        {
            AntigravityAccounts.Mutate(_store, _io.Install, accounts =>
                // `unchanged` keeps a label already listed; only a fresh
                // capture refreshes it.
                status == "unchanged" && accounts.Any(a => a.Key == key)
                    ? accounts
                    : AntigravityAccounts.Adding(new AntigravityAccount(key, label), accounts));
            if (IsEnabled)
            {
                lock (_gate)
                {
                    changed = SetCurrentLocked(key, marker);
                }
            }
        }
        else if (code == "paused")
        {
            lock (_gate)
            {
                _paused = true;
                changed = ClearCurrentLocked();
            }
        }

        lock (_gate)
        {
            _busy = false;
        }

        Notify(changed);
        await PollIfOwed().ConfigureAwait(false);
    }

    /// <summary>Before a quota fetch: read agy's login marker and, when it
    /// differs from the last attempt, forget the current account NOW, so the
    /// fetch that follows a login change is never drawn as the previous
    /// account. The capture attempt is returned, not awaited, so the fetch
    /// never waits on Google. Never throws.</summary>
    public async Task<Task?> PrepareForFetch()
    {
        lock (_gate)
        {
            if (_paused || _checking)
            {
                return null;
            }

            _checking = true;
        }

        var marker = await TryMarker().ConfigureAwait(false);
        var changed = false;
        lock (_gate)
        {
            _checking = false;
            if (marker is not null && marker != _lastAttemptedMarker)
            {
                changed = ClearCurrentLocked();
            }
        }

        if (marker is null)
        {
            return null;
        }

        Notify(changed);
        return Task.Run(() => Poll(marker));
    }

    private async Task PollIfOwed()
    {
        lock (_gate)
        {
            if (!_pollAgain)
            {
                return;
            }

            _pollAgain = false;
        }

        await Poll().ConfigureAwait(false);
    }

    /// <summary>The toggle. On: forget the last marker and try now. Off: stop
    /// watching agy's login; the current account stays, still bound to its
    /// marker, so a manual capture's dedup survives. Either way a pause ends.
    /// Settings turns it on only through <see cref="TurnOn"/>.</summary>
    public async Task SetEnabled(bool on)
    {
        _store.SetBool(EnabledKey, on);
        lock (_gate)
        {
            _paused = false;
            if (on)
            {
                _lastAttemptedMarker = null;
            }
        }

        Notify(false);
        if (on)
        {
            await Poll().ConfigureAwait(false);
        }
    }

    /// <summary>Turning automatic capture on from Settings: the confirmation
    /// first (security review R7-1: Windows reads agy's login without any OS
    /// prompt). Cancel stores nothing and calls nothing. Returns whether it
    /// was turned on.</summary>
    public async Task<bool> TurnOn(Func<Task<bool>> confirm)
    {
        if (!await confirm())
        {
            return false;
        }

        await SetEnabled(true);
        return true;
    }

    /// <summary>Capture agy's current login (the Settings button). Takes the
    /// account off the removed list, ends a pause, and then tries automatic
    /// capture once more when it is on.</summary>
    public async Task ManualCapture()
    {
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            _errorCode = null;
        }

        Notify(false);
        // The marker before AND after: the Settings steps say to sign agy back
        // in right after pressing Capture, and a sign-in that lands while the
        // capture runs would otherwise bind this key to the NEXT login's
        // marker. Bound only if equal.
        var before = await TryMarker().ConfigureAwait(false);
        AntigravityAccount? captured = null;
        try
        {
            captured = await Task.Run(_io.Capture).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"antigravity capture failed: {ex.GetType().Name}");
            lock (_gate)
            {
                _errorCode = CodeOf(ex);
            }
        }

        var changed = false;
        var resume = false;
        if (captured is not null)
        {
            AntigravityAccounts.Mutate(_store, _io.Install, accounts => AntigravityAccounts.Adding(captured, accounts));
            AntigravityAccounts.SaveRemovedKeys(
                _store, [.. AntigravityAccounts.RemovedKeys(_store).Where(k => k != captured.Key)]);
            // The button is the consent, whether or not automatic capture is
            // on; when agy signs in elsewhere the marker moves and dedup stops.
            var after = await TryMarker().ConfigureAwait(false);
            lock (_gate)
            {
                changed = before is not null && before == after
                    ? SetCurrentLocked(captured.Key, after)
                    : ClearCurrentLocked();
                if (_paused)
                {
                    _paused = false;
                    _lastAttemptedMarker = null;
                    resume = IsEnabled;
                }
            }
        }

        lock (_gate)
        {
            _busy = false;
        }

        Notify(changed);
        if (resume)
        {
            await Poll().ConfigureAwait(false);
        }

        await PollIfOwed().ConfigureAwait(false);
    }

    /// <summary>Delete one account's Credential Manager copy and drop it from
    /// the list. While automatic capture is on, the key also goes on the
    /// removed list so the next login change does not add it back.</summary>
    public async Task Remove(string key)
    {
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            _errorCode = null;
        }

        Notify(false);
        string? code = null;
        try
        {
            await Task.Run(() => _io.Remove(key)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            code = CodeOf(ex);
            _log($"antigravity remove failed: {ex.GetType().Name}");
        }

        var changed = false;
        // An invalid key has no Credential Manager entry and no card (the core
        // registry rejects it): dropping the row is all that is left.
        if (code is null or "invalid_key")
        {
            AntigravityAccounts.Mutate(_store, _io.Install, accounts => [.. accounts.Where(a => a.Key != key)]);
            lock (_gate)
            {
                if (_currentKey == key)
                {
                    changed = ClearCurrentLocked();
                }
            }

            if (IsEnabled)
            {
                var removed = AntigravityAccounts.RemovedKeys(_store);
                if (!removed.Contains(key))
                {
                    AntigravityAccounts.SaveRemovedKeys(_store, [.. removed, key]);
                }
            }
        }
        else
        {
            lock (_gate)
            {
                _errorCode = code;
            }
        }

        lock (_gate)
        {
            _busy = false;
        }

        Notify(changed);
        await PollIfOwed().ConfigureAwait(false);
    }

    private bool SetCurrentLocked(string key, string? marker)
    {
        var changed = _currentKey != key;
        _currentMarker = marker;
        _currentKey = key;
        Persist();
        return changed;
    }

    private bool ClearCurrentLocked()
    {
        var changed = _currentKey is not null;
        _currentKey = null;
        Persist();
        return changed;
    }

    private void Persist()
    {
        if (_currentKey is { } key)
        {
            _store.SetString(CurrentKey, JsonSerializer.Serialize(
                new Dictionary<string, string> { ["key"] = key, ["marker"] = _currentMarker ?? "" }));
        }
        else
        {
            _store.Remove(CurrentKey);
        }
    }

    /// <summary>Settings redraws; when agy's current account changed, the
    /// pollers refetch so the dedup follows now, not a cycle later.</summary>
    private void Notify(bool currentChanged)
    {
        StateChanged?.Invoke();
        if (currentChanged)
        {
            AntigravityAccounts.RaiseChanged();
        }
    }

    private async Task<string?> TryMarker()
    {
        try
        {
            return await Task.Run(_io.Marker).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"antigravity marker failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>The core's fixed code, or <see cref="UnexpectedError"/> for
    /// anything else (a missing DLL, a malformed envelope).</summary>
    private static string CodeOf(Exception ex) => ex is TbCoreException core ? core.Message : UnexpectedError;
}
