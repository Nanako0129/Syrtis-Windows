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
    /// the same answer as "Not now" on the card. It takes effect from the next
    /// refresh; a refresh already under way may finish. (The core also
    /// re-checks consent before each decode, before the account-scope
    /// fingerprint and just before the request — best-effort hardening that
    /// often stops an in-flight refresh sooner, not a guarantee.) While Grok
    /// Bot is signed in the card then shows the declined line; the Cursor IDE
    /// sign-in is used only when Grok Bot is signed out. The answer
    /// sticks.</summary>
    public void Withdraw() => Answer(false);

    /// <summary>Launch: re-install a stored yes. Anything else is already what
    /// the empty registry does. Never throws — a failure here must not take
    /// down the first fetch; the grant is then not installed and the card
    /// asks again.</summary>
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
    /// a consent card. A stored no is <see cref="Card.Declined"/>; never asked
    /// and a stored yes are both <see cref="Card.Ask"/> (macOS parity): a yes
    /// the core has not acted on — no fetch since it, or it never reached the
    /// core — keeps the full card and Allow, so the grant can be sent
    /// again.</summary>
    public static Card CardFor(AgentUsageSnapshot agent, bool? answer) =>
        agent.ClientId == "grok-bot" && agent.Source == "keychain-consent"
            ? answer == false ? Card.Declined : Card.Ask
            : Card.None;

    /// <summary>What the consent card draws: the card, whether a grant is in
    /// flight, and the button states that follow from both. Decided here
    /// because the view compiles under no test project.</summary>
    public readonly record struct Prompt(Card Card, bool Waiting)
    {
        public string Text => TextFor(Card);

        /// <summary>Not now exists only on the full card: the declined line
        /// already is its answer (macOS <c>if !consentDeclined</c>).</summary>
        public bool ShowsNotNow => Card == Card.Ask;

        /// <summary>Disabled, not hidden, while a grant is in flight (macOS
        /// <c>.disabled(granting)</c>): offering "no" under a pending "yes"
        /// invites answering the same question twice. Allow is disabled for
        /// the same span and reads <see cref="Copy.Waiting"/>.</summary>
        public bool NotNowEnabled => !Waiting;
    }

    /// <summary>The Allow button's "Waiting…" (macOS "Waiting for macOS…").
    /// Set when a grant the core accepted lands over a consent card — Allow on
    /// the card, or the Settings switch (<see cref="GrantedElsewhere"/>) — and
    /// records the card that was showing. While it holds,
    /// <see cref="Decide"/> keeps drawing that card, so a grant never brings
    /// new text: Declined → Allow stays one line, Ask → Allow stays the
    /// disclosure (macOS re-reads the text only when the payload changes).
    /// Every decision asks IsWaiting, which ends it for good
    /// when the stored answer is no longer yes (Not now, Settings off), a
    /// different payload is shown, or a quota fetch has failed since the
    /// grant; the normal <see cref="CardFor"/> decision then applies. The
    /// refresh after a grant is best effort and may wait for the next poll
    /// (macOS guarantees a refetch; Windows aligns in slice W6c), so without
    /// these exits the button could stay disabled.</summary>
    public sealed class WaitingState
    {
        private bool _waiting;
        private object? _over;
        private int _failuresAtGrant;
        private Card _card;
        private (Card Card, object? Over, int Failures)? _shown;

        public void Granted(Card shownCard, object? shownPayload, int failedFetches)
        {
            _waiting = true;
            _card = shownCard;
            _over = shownPayload;
            _failuresAtGrant = failedFetches;
        }

        /// <summary>A yes that did not come from the card's Allow (the Settings
        /// switch): wait over the consent card last decided, if one was. A
        /// no-op while already waiting, so Allow's own grant is not
        /// re-recorded.</summary>
        public void GrantedElsewhere()
        {
            if (!_waiting && _shown is { } shown)
            {
                Granted(shown.Card, shown.Over, shown.Failures);
            }
        }

        private bool IsWaiting(bool? stored, object? shownPayload, int failedFetches)
        {
            if (stored != true
                || !ReferenceEquals(shownPayload, _over)
                || failedFetches != _failuresAtGrant)
            {
                _waiting = false;
                _over = null;
            }

            return _waiting;
        }

        /// <summary>The card for <paramref name="agent"/>: the recorded card
        /// while IsWaiting holds, else <see cref="CardFor"/>.</summary>
        public Prompt Decide(
            AgentUsageSnapshot agent, bool? stored, object? shownPayload, int failedFetches)
        {
            var card = CardFor(agent, stored);
            if (card == Card.None)
            {
                return new(Card.None, false);
            }

            var waiting = IsWaiting(stored, shownPayload, failedFetches);
            var prompt = new Prompt(waiting ? _card : card, waiting);
            _shown = (prompt.Card, shownPayload, failedFetches);
            return prompt;
        }
    }

    /// <summary>The card's line for <paramref name="card"/> (English source;
    /// localize at the view).</summary>
    public static string TextFor(Card card) => card switch
    {
        Card.Declined => Copy.Declined,
        _ => Copy.Explanation,
    };

    /// <summary>English source strings (the localization keys). Windows copy
    /// per Plan W6.6/W6.7: no "Keychain", names the one destination, says how
    /// to stop.</summary>
    public static class Copy
    {
        /// <summary>The pre-consent disclosure only: what is unlocked, that
        /// Windows doesn't ask, the one destination, nothing kept or logged.
        /// The operational detail (the switch, when a
        /// withdrawal takes effect, the Cursor fallback) is
        /// <see cref="SettingsHint"/>.</summary>
        public const string Explanation =
            "To show your weekly Grok Bot limits, Syrtis needs to unlock Grok Bot's saved "
            + "sign-in on this PC; Windows doesn't ask separately. The sign-in is sent only to "
            + "Grok Bot's usage service (api2.cursor.sh). Syrtis keeps no copy and doesn't "
            + "log it.";

        public const string Declined = "Syrtis isn't reading your Grok Bot limits.";

        /// <summary>The Allow button after a grant the core accepted, until
        /// <see cref="WaitingState"/> ends it (macOS "Waiting for macOS…").</summary>
        public const string Waiting = "Waiting…";

        /// <summary>The Allow button's table key, rendered with
        /// <c>LocalizedKey(AllowEnglish)</c>. Not the bare "Allow" key: that
        /// one is the status badge (<c>AgentLimitsText.SetupBadgeKey</c>), a
        /// state ("待授權") where this is an action ("允許"), and one key
        /// cannot carry both (macOS <c>consent.action.allow</c>).</summary>
        public const string Allow = "consent.action.allow";

        public const string AllowEnglish = "Allow";

        public const string NotNow = "Not now";

        public const string SettingsToggle = "Use Grok Bot's sign-in";

        public const string SettingsHint =
            "When on, Syrtis unlocks Grok Bot's saved sign-in each time it refreshes — "
            + "including sign-ins Grok Bot saved without encryption — and sends it only to "
            + "api2.cursor.sh. Choosing Allow on the Grok Bot card turns this on too. When "
            + "off, Syrtis still checks Grok Bot's sign-in file each refresh to see whether "
            + "it is signed in, but does not unlock or send the sign-in. Turning this off "
            + "takes effect from the next refresh; a refresh already under way may finish. "
            + "When Grok Bot is signed out, Syrtis uses the Cursor IDE sign-in "
            + "instead, if there is one, and sends it only to cursor.com.";
    }
}
