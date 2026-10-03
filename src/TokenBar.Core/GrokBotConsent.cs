using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Whether the user has agreed to let Syrtis unlock and use the Grok
/// Bot desktop sign-in (the secrets file itself is parsed every refresh to
/// learn whether a signed-in account exists, consent or not), and the wiring
/// that carries that answer into the core.
/// Ported from macOS <c>GrokBotKeychainConsent.swift</c> (451b4329) minus the
/// <c>keychain-denied</c> auto-revoke, which models an OS dialog Windows does
/// not have (R6-3).
/// <para>
/// On Windows this answer is the ONLY gate: DPAPI decrypts Grok Bot's sign-in
/// for any process running as the user without asking. The core registry
/// (<c>tb_set_keychain_consent</c>) is in-memory and empty at every launch, so
/// the settings file is the source of truth: <see cref="ApplyIfGranted"/>
/// re-installs a stored yes before the first agent-usage fetch.
/// </para>
/// <para>
/// Three states here (null = never asked, false, true), two in the core:
/// "never asked" and "no" both mean "do not read", and only the copy differs.
/// The value records an ANSWER, never the fact that the card was shown.
/// </para></summary>
public sealed class GrokBotConsent(SettingsStore store, Action<string> setConsent)
{
    /// <summary>The macOS UserDefaults key, kept for a shared vocabulary (Q6-3);
    /// the UI never says "Keychain" on Windows.</summary>
    public const string StorageKey = "tokenbar.grokBot.keychainConsent";

    /// <summary>The exact payloads; the core rejects unknown ids, so a typo
    /// here would be a grant nothing honours.</summary>
    public const string GrantedPayload = """{"grok-bot":true}""";

    /// <summary>Full-replace with nothing: how the core spells "revoked".</summary>
    public const string DeniedPayload = "{}";

    private readonly object _gate = new();

    /// <summary>The production setter over the FFI.</summary>
    public static Action<string> NativeSetter { get; } = json => TbCore.SetKeychainConsent(json);

    /// <summary>The stored answer: null = never asked.</summary>
    public bool? Stored => store.GetNullableBool(StorageKey);

    /// <summary>Record the user's answer and make it take effect now — both
    /// answers, so a "no" after a "yes" clears the core at once rather than at
    /// the next launch. Serialized under one lock so the last click wins in
    /// both the settings file and the core. The core is told first: if the
    /// call throws, nothing is persisted and the exception reaches the caller,
    /// so the stored answer never claims a state the core is not in.</summary>
    public void Answer(bool granted)
    {
        lock (_gate)
        {
            setConsent(granted ? GrantedPayload : DeniedPayload);
            store.SetBool(StorageKey, granted);
        }
    }

    /// <summary>Settings' "Use Grok Bot's sign-in" switch turned off (Q6-2),
    /// the same answer as "Not now" on the card. The core refuses at its next
    /// consent check — before each decode, before the account-scope
    /// fingerprint and just before the request — so Syrtis stops before it
    /// next sends the sign-in, even during a refresh already under way. While
    /// Grok Bot is signed in the card then shows the declined line; the
    /// Cursor IDE sign-in is used only when Grok Bot is signed out. The answer
    /// sticks.</summary>
    public void Withdraw() => Answer(false);

    /// <summary>Launch: re-install a stored yes. Anything else is already what
    /// the empty registry does. Never throws — a failure here must not take
    /// down the first fetch; the card then simply asks again.</summary>
    public void ApplyIfGranted()
    {
        lock (_gate)
        {
            if (Stored != true)
            {
                return;
            }

            try
            {
                setConsent(GrantedPayload);
            }
            catch (Exception)
            {
                // Fail closed: the grant is simply not installed.
            }
        }
    }

    /// <summary>What the Grok Bot card shows instead of its error line.</summary>
    public enum Card
    {
        /// <summary>Not a consent snapshot: render normally.</summary>
        None,

        /// <summary>The full explanation with Allow / Not now.</summary>
        Ask,

        /// <summary>The one-line "isn't reading" row, Allow kept.</summary>
        Declined,
    }

    /// <summary>The projection the limits card renders: only a
    /// <c>grok-bot</c> snapshot the core marked <c>keychain-consent</c> becomes
    /// a consent card. A stored no is <see cref="Card.Declined"/>; anything
    /// else asks, a stored yes included (the fetch that honours it has not
    /// landed yet, or the yes never reached the core), so Allow can always
    /// send the answer to the core again.</summary>
    public static Card CardFor(AgentUsageSnapshot agent, bool? answer) =>
        agent.ClientId == "grok-bot" && agent.Source == "keychain-consent"
            ? answer == false ? Card.Declined : Card.Ask
            : Card.None;

    /// <summary>English source strings (the localization keys). Windows copy
    /// per Plan W6.6/W6.7: no "Keychain", names the one destination, says how
    /// to stop.</summary>
    public static class Copy
    {
        public const string Explanation =
            "To show your weekly Grok Bot limits, Syrtis needs to unlock and use Grok Bot's "
            + "saved sign-in on this PC. Windows does not ask separately, so this is the only "
            + "prompt: choose Allow here, or turn on \"Use Grok Bot's sign-in\" in Settings. "
            + "Until you allow it, Syrtis only checks whether Grok Bot is signed in. "
            + "The sign-in is sent only to Grok Bot's usage service (api2.cursor.sh). Syrtis "
            + "keeps no copy, only a one-way fingerprint to tell accounts apart, and doesn't "
            + "log it. Turning the switch off in Settings stops Syrtis before it next sends "
            + "the sign-in, even during a refresh already under way. If you sign out of Grok "
            + "Bot, Syrtis uses the Cursor IDE sign-in instead, if there is one, and sends it "
            + "only to cursor.com.";

        public const string Declined = "Syrtis isn't reading your Grok Bot limits.";

        public const string Allow = "Allow";

        public const string NotNow = "Not now";

        public const string SettingsToggle = "Use Grok Bot's sign-in";

        public const string SettingsHint =
            "When on, Syrtis unlocks Grok Bot's saved sign-in each time it refreshes — "
            + "including sign-ins Grok Bot saved without encryption — and sends it only to "
            + "api2.cursor.sh. Choosing Allow on the Grok Bot card turns this on too. When "
            + "off, Syrtis still checks Grok Bot's sign-in file each refresh to see whether "
            + "it is signed in, but does not unlock or send the sign-in. Turning this off "
            + "stops Syrtis before it next sends the sign-in, even during a refresh already "
            + "under way. When Grok Bot is signed out, Syrtis uses the Cursor IDE sign-in "
            + "instead, if there is one, and sends it only to cursor.com.";
    }
}
