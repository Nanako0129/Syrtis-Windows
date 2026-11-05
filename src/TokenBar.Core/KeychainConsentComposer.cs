using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Owns both credential-read consent answers (Grok Bot's desktop
/// sign-in and Cursor's encrypted sign-in) and the one full-replace payload
/// the core holds. The core registry (<c>tb_set_keychain_consent</c>) is
/// full-replace: a payload for one id would silently drop the other, so a
/// single owner composes <c>{"grok-bot":…,"cursor":…}</c> from both answers
/// under one lock, tracking what the core holds in one
/// <see cref="_lastInstalled"/> (plan CURSOR-SS-W S3b item 3, C3; fixes macOS
/// T2 on Windows).
/// <para>
/// On Windows this is the only gate: DPAPI decrypts either sign-in silently
/// for the signed-in user, so the stored answers — re-applied at launch, since
/// the registry is in-memory and empty every start — are what stands between
/// "installed" and "Syrtis read it".
/// </para></summary>
public sealed class KeychainConsentComposer(SettingsStore store, Action<string> setConsent)
{
    public const string GrokBotId = "grok-bot";
    public const string CursorId = "cursor";

    /// <summary>Settings keys per id (macOS vocabulary kept; the UI never says
    /// "Keychain" on Windows).</summary>
    public const string GrokBotStorageKey = "tokenbar.grokBot.keychainConsent";
    public const string CursorStorageKey = "tokenbar.cursorSync.keychainConsent";

    /// <summary>The core's empty registry, and what a full withdrawal composes
    /// to. A no that composes to this while nothing is installed makes no core
    /// call (the core is already empty at launch).</summary>
    private const string EmptyPayload = "{}";

    /// <summary>The production setter over the FFI.</summary>
    public static Action<string> NativeSetter { get; } = json => TbCore.SetKeychainConsent(json);

    private readonly object _gate = new();

    // The composed payload the core holds from this process; null = nothing
    // installed (the core's empty-registry start). The one source of truth for
    // "what is in the core", never inferred from the stored answers.
    private string? _lastInstalled;

    private static string StorageKeyFor(string id) =>
        id == CursorId ? CursorStorageKey : GrokBotStorageKey;

    /// <summary>Record <paramref name="changingId"/>'s answer and install the
    /// payload composed from it and the OTHER id's stored answer. Reads only
    /// the other id from the store (the changing id's new answer is the
    /// argument). Core first, then the store, so the stored answer never claims
    /// a state the core is not in: a throwing setter leaves both unchanged.
    /// <para>The core is told on every answer (idempotent, macOS parity),
    /// except a denial that composes to the empty payload while nothing is
    /// installed — the core is already empty, so that makes no call (C3).
    /// Returns whether the installed payload CHANGED (not merely whether a call
    /// was made): a re-sent identical payload returns false, so the Grok Bot
    /// card signals <see cref="QuotaEpoch"/> only on a real change.</para></summary>
    public bool Install(string changingId, bool newAnswer)
    {
        lock (_gate)
        {
            var otherId = changingId == GrokBotId ? CursorId : GrokBotId;
            var otherAnswer = store.GetNullableBool(StorageKeyFor(otherId)) == true;
            var grok = changingId == GrokBotId ? newAnswer : otherAnswer;
            var cursor = changingId == CursorId ? newAnswer : otherAnswer;
            var payload = Compose(grok, cursor);

            // Skip only the empty-and-nothing-installed no-op; otherwise re-send
            // even an unchanged payload (the core is idempotent).
            var changed = false;
            if (payload != EmptyPayload || _lastInstalled is not null)
            {
                changed = payload != (_lastInstalled ?? EmptyPayload);
                setConsent(payload); // throws -> store not written below
                _lastInstalled = payload;
            }

            store.SetBool(StorageKeyFor(changingId), newAnswer);
            return changed;
        }
    }

    /// <summary>Launch and the RunBeforeFirstFetch hook: install the payload
    /// composed from BOTH stored answers, so a stored yes for either id is in
    /// the core before its first read (a Cursor yes before Cursor sync's first
    /// round, a Grok yes before the first agent-usage fetch). Writes no store
    /// and never throws — a failure must not take down launch; the grant is
    /// then simply not installed and the surface asks again. Does not signal
    /// <see cref="QuotaEpoch"/>: it runs before the first payload is built, so a
    /// signal would only discard it.</summary>
    public void InstallComposedIfAnyGranted()
    {
        lock (_gate)
        {
            var grok = store.GetNullableBool(GrokBotStorageKey) == true;
            var cursor = store.GetNullableBool(CursorStorageKey) == true;
            if (!grok && !cursor)
            {
                return; // the empty registry already denies both
            }

            try
            {
                var payload = Compose(grok, cursor);
                setConsent(payload);
                _lastInstalled = payload;
            }
            catch (Exception)
            {
                // Fail closed: nothing installed.
            }
        }
    }

    // A granted id is listed true; null and false are omitted (the core reads
    // absence as "no"). Stable order so the payload compares by string.
    private static string Compose(bool grok, bool cursor)
    {
        var parts = new List<string>(2);
        if (grok)
        {
            parts.Add("\"grok-bot\":true");
        }

        if (cursor)
        {
            parts.Add("\"cursor\":true");
        }

        return "{" + string.Join(",", parts) + "}";
    }
}
