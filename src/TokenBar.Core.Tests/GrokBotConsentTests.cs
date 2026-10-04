using System.Text.Json;
using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// W6b: the Grok Bot consent answer and its wiring into the core. On Windows
// this answer is the only gate before DPAPI decrypts Grok Bot's sign-in, so
// every test here asserts what reaches the (fake) FFI setter, not only what
// lands in the settings file.
public class GrokBotConsentTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _calls = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private SettingsStore Store() => new(Path.Combine(_dir, "settings.json"));

    private GrokBotConsent Consent(SettingsStore store) =>
        new(store, json =>
        {
            lock (_calls)
            {
                _calls.Add(json);
            }
        });

    [Fact]
    public void NeverAskedIsNullAndAnswersPersistAcrossInstances()
    {
        var store = Store();
        Assert.Null(Consent(store).Stored);
        Consent(store).Answer(false);
        Assert.False(Consent(Store()).Stored);
        Consent(store).Answer(true);
        Assert.True(Consent(Store()).Stored);
    }

    [Fact]
    public void AllowPersistsTrueAndInstallsTheGrant()
    {
        var store = Store();
        Consent(store).Answer(true);
        Assert.True(store.GetNullableBool(GrokBotConsent.StorageKey));
        Assert.Equal(["""{"grok-bot":true}"""], _calls);
    }

    [Fact]
    public void NotNowPersistsFalseAndClearsTheCore()
    {
        var store = Store();
        Consent(store).Answer(false);
        Assert.False(store.GetNullableBool(GrokBotConsent.StorageKey));
        Assert.Equal(["{}"], _calls);
    }

    // Q6-2: the Settings switch. Persisting alone would leave the core reading
    // until the next launch while the switch says off.
    [Fact]
    public void WithdrawPersistsFalseAndClearsTheCore()
    {
        var store = Store();
        var consent = Consent(store);
        consent.Answer(true);
        consent.Withdraw();
        Assert.False(store.GetNullableBool(GrokBotConsent.StorageKey));
        Assert.Equal(["""{"grok-bot":true}""", "{}"], _calls);
    }

    [Fact]
    public void AFailedSetterPersistsNothing()
    {
        var store = Store();
        var consent = new GrokBotConsent(store, _ => throw new TbCoreException("boom"));
        Assert.Throws<TbCoreException>(() => consent.Answer(true));
        Assert.Null(consent.Stored);
    }

    // Launch: the registry is empty in a fresh process. A stored yes must be in
    // the core before the first agent-usage fetch runs, through the same
    // coordinator every poll uses.
    [Fact]
    public async Task LaunchReappliesAStoredGrantBeforeTheFirstFetch()
    {
        var store = Store();
        store.SetBool(GrokBotConsent.StorageKey, true);
        var consent = Consent(store);
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            lock (_calls)
            {
                _calls.Add("fetch");
            }

            return new AgentUsagePayload("now", []);
        });
        coordinator.RunBeforeFirstFetch(consent.ApplyIfGranted);

        await coordinator.FetchAsync();
        await coordinator.FetchAsync();

        Assert.Equal(["""{"grok-bot":true}""", "fetch", "fetch"], _calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void LaunchInstallsNothingWithoutAStoredYes(bool? stored)
    {
        var store = Store();
        if (stored is { } value)
        {
            store.SetBool(GrokBotConsent.StorageKey, value);
        }

        Consent(store).ApplyIfGranted();
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ALaunchReapplyFailureDoesNotFailTheFetch()
    {
        var store = Store();
        store.SetBool(GrokBotConsent.StorageKey, true);
        var consent = new GrokBotConsent(store, _ => throw new TbCoreException("boom"));
        var payload = new AgentUsagePayload("now", []);
        var coordinator = new AgentUsageFetchCoordinator(() => payload);
        coordinator.RunBeforeFirstFetch(consent.ApplyIfGranted);
        Assert.Same(payload, await coordinator.FetchAsync());
    }

    private static AgentUsageSnapshot Snapshot(string clientId, string source) =>
        new(clientId, source, "2026-10-04T00:00:00Z", [],
            Error: "Syrtis needs your permission to read the Grok Bot sign-in.");

    [Fact]
    public void OnlyAGrokBotConsentSnapshotBecomesTheConsentCard()
    {
        var marked = Snapshot("grok-bot", "keychain-consent");
        Assert.Equal(GrokBotConsent.Card.Ask, GrokBotConsent.CardFor(marked, null));
        // A stored yes the core has not acted on (no fetch since the yes, or
        // the yes never reached the core) keeps the full card and Allow, as on
        // macOS: never the declined line.
        Assert.Equal(GrokBotConsent.Card.Ask, GrokBotConsent.CardFor(marked, true));
        Assert.Equal(GrokBotConsent.Card.Declined, GrokBotConsent.CardFor(marked, false));
        Assert.Equal(GrokBotConsent.Copy.Explanation, GrokBotConsent.TextFor(GrokBotConsent.CardFor(marked, true)));
        Assert.Equal(GrokBotConsent.Copy.Explanation, GrokBotConsent.TextFor(GrokBotConsent.CardFor(marked, null)));
        Assert.Equal(GrokBotConsent.Copy.Declined, GrokBotConsent.TextFor(GrokBotConsent.CardFor(marked, false)));
        Assert.Equal(GrokBotConsent.Card.None, GrokBotConsent.CardFor(Snapshot("grok-bot", "oauth"), null));
        Assert.Equal(GrokBotConsent.Card.None, GrokBotConsent.CardFor(Snapshot("grok", "keychain-consent"), null));
    }

    // The grouped "Grok Build & Bot" tab: its limits card carries grok-bot's
    // rows, so the consent snapshot reaches the card that renders it.
    [Fact]
    public void TheGrokTabLimitsCardCarriesTheGrokBotConsentCard()
    {
        var agents = new[]
        {
            Snapshot("grok", "oauth"),
            Snapshot("grok-bot", "keychain-consent"),
            Snapshot("codex", "oauth"),
        };
        var clients = OverviewScope.LimitsClients(OverviewScope.SingleClient("grok"))!;
        var shown = agents.Where(agent => clients.Contains(agent.ClientId)).ToList();
        Assert.Equal(["grok", "grok-bot"], shown.Select(agent => agent.ClientId));
        Assert.Equal(
            [GrokBotConsent.Card.None, GrokBotConsent.Card.Ask],
            shown.Select(agent => GrokBotConsent.CardFor(agent, null)));
        Assert.Null(OverviewScope.LimitsClients(null));
        Assert.Equal(["antigravity", "antigravity-cli"], OverviewScope.LimitsClients("antigravity-cli")!);
    }

    public static TheoryData<string> CopyStrings() =>
    [
        GrokBotConsent.Copy.Explanation,
        GrokBotConsent.Copy.Declined,
        GrokBotConsent.Copy.Waiting,
        GrokBotConsent.Copy.Allow,
        GrokBotConsent.Copy.NotNow,
        GrokBotConsent.Copy.SettingsToggle,
        GrokBotConsent.Copy.SettingsHint,
    ];

    [Theory]
    [MemberData(nameof(CopyStrings))]
    public void EveryCopyStringIsTranslatedAndNeverSaysKeychain(string english)
    {
        Assert.DoesNotContain("Keychain", english);
        foreach (var table in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;
            Assert.True(entries.TryGetValue(english, out var translated), $"{table}: {english}");
            Assert.False(string.IsNullOrWhiteSpace(translated));
        }
    }

    // The badge on a consent card names a state ("待授權"), the button an
    // action ("允許"). Sharing the bare "Allow" key, one table entry would
    // overwrite the other (seen when merging main's badge into #190).
    [Fact]
    public void AllowButtonAndAllowBadgeAreDifferentKeys()
    {
        var badge = AgentLimitsText.SetupBadgeKey(Snapshot("grok-bot", "keychain-consent"));
        foreach (var table in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;
            Assert.NotEqual(entries[badge!], entries[GrokBotConsent.Copy.Allow]);
        }
    }

    // The real export through the P/Invoke: both payloads the store sends are
    // accepted, and an error envelope surfaces as an exception (which is what
    // keeps Answer from persisting a state the core never took). Needs the
    // native library, like the other TbCore tests. Leaves the process's
    // registry empty.
    [Fact]
    public void NativeSetterAcceptsBothPayloadsAndRejectsMalformedJson()
    {
        GrokBotConsent.NativeSetter(GrokBotConsent.GrantedPayload);
        GrokBotConsent.NativeSetter(GrokBotConsent.DeniedPayload);
        var error = Assert.Throws<TbCoreException>(() => TbCore.SetKeychainConsent("{not json"));
        Assert.Equal("invalidJson", error.Message);
    }

    // The card is the pre-consent disclosure only: what is unlocked, that
    // Windows doesn't ask, the one destination, nothing kept or logged. The
    // operational contract (the switch, when a withdrawal takes effect, the
    // Cursor fallback, the sign-in file checked before consent) is the
    // Settings hint's.
    [Fact]
    public void ConsentCardIsTheDisclosureAndTheSettingsHintCarriesTheRest()
    {
        var card = GrokBotConsent.Copy.Explanation;
        Assert.Contains("unlock Grok Bot's saved sign-in on this PC; Windows doesn't ask separately", card);
        Assert.Contains("sent only to Grok Bot's usage service (api2.cursor.sh)", card);
        Assert.Contains("keeps no copy and doesn't log it", card);
        foreach (var later in new[] { "next refresh", "Cursor", "fingerprint" })
        {
            Assert.DoesNotContain(later, card);
        }

        var hint = GrokBotConsent.Copy.SettingsHint;
        Assert.Contains("takes effect from the next refresh; a refresh already under way may finish", hint);
        Assert.Contains("Cursor IDE sign-in instead, if there is one, and sends it only to cursor.com", hint);
        Assert.Contains("Choosing Allow on the Grok Bot card turns this on too", hint);
        Assert.Contains("without encryption", hint);
        // The stored HMAC is disclosed here, since the card only says no copy is kept.
        Assert.Contains("only a one-way fingerprint to tell accounts apart", hint);
        // sand-secrets.json is read every refresh to learn whether Grok Bot is
        // signed in; the hint must not claim it is untouched before Allow.
        Assert.Contains("still checks Grok Bot's sign-in file", hint);
    }

    // The Waiting state after a grant. A grant never brings new text: the card
    // that was showing stays until Waiting ends (macOS re-reads the text only
    // when the payload changes). The refresh after a grant is best effort, so
    // each exit is what keeps the buttons from latching disabled. Every test
    // first shows Waiting holding (stored yes, same payload, no new failure),
    // so a state that never waits cannot pass.
    private static readonly AgentUsageSnapshot Marked = Snapshot("grok-bot", "keychain-consent");

    private static GrokBotConsent.WaitingState GrantedOver(
        AgentUsagePayload shown, GrokBotConsent.Card card = GrokBotConsent.Card.Ask)
    {
        var waiting = new GrokBotConsent.WaitingState();
        waiting.Granted(card, shown, failedFetches: 2);
        Assert.Equal(new GrokBotConsent.Prompt(card, Waiting: true), waiting.Decide(Marked, true, shown, 2));
        return waiting;
    }

    [Fact]
    public void AllowOnTheDeclinedLineStaysOneLineWhileWaiting()
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = new GrokBotConsent.WaitingState();
        var before = waiting.Decide(Marked, false, shown, 2);
        Assert.Equal(GrokBotConsent.Card.Declined, before.Card);
        waiting.Granted(before.Card, shown, 2);
        // The re-render after Allow: stored is now yes, which alone would be Ask.
        var after = waiting.Decide(Marked, true, shown, 2);
        Assert.Equal(GrokBotConsent.Card.Declined, after.Card);
        Assert.True(after.Waiting);
        Assert.Equal(GrokBotConsent.Copy.Declined, after.Text);
        Assert.False(after.ShowsNotNow);
    }

    [Fact]
    public void AllowOnTheFullCardKeepsTheDisclosureWhileWaiting()
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = new GrokBotConsent.WaitingState();
        var before = waiting.Decide(Marked, null, shown, 2);
        Assert.Equal(new GrokBotConsent.Prompt(GrokBotConsent.Card.Ask, Waiting: false), before);
        Assert.True(before.NotNowEnabled);
        waiting.Granted(before.Card, shown, 2);
        var after = waiting.Decide(Marked, true, shown, 2);
        Assert.Equal(GrokBotConsent.Card.Ask, after.Card);
        Assert.True(after.Waiting);
        Assert.Equal(GrokBotConsent.Copy.Explanation, after.Text);
        // Not now stays on the card, disabled rather than hidden.
        Assert.True(after.ShowsNotNow);
        Assert.False(after.NotNowEnabled);
    }

    [Fact]
    public void SettingsSwitchOnOverTheDeclinedLineKeepsIt()
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = new GrokBotConsent.WaitingState();
        Assert.Equal(GrokBotConsent.Card.Declined, waiting.Decide(Marked, false, shown, 2).Card);
        waiting.GrantedElsewhere();
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.Declined, Waiting: true),
            waiting.Decide(Marked, true, shown, 2));
    }

    [Fact]
    public void SettingsSwitchOnWithNoConsentCardDrawnRecordsTheDeclinedLine()
    {
        var waiting = new GrokBotConsent.WaitingState();
        waiting.GrantedElsewhere();
        // A consent snapshot under the yes (a fetch begun before it): the one
        // line, not the disclosure the user already answered; Allow enabled
        // to re-send, since there was no payload to wait on.
        var after = waiting.Decide(Marked, true, new AgentUsagePayload("now", []), 2);
        Assert.Equal(new GrokBotConsent.Prompt(GrokBotConsent.Card.Declined, Waiting: false), after);
        Assert.Equal(GrokBotConsent.Copy.Declined, after.Text);
    }

    // Reachable through the Settings switch: it stays enabled while the card
    // waits, and turning it off ends the grant.
    [Theory]
    [InlineData(GrokBotConsent.Card.Ask)]
    [InlineData(GrokBotConsent.Card.Declined)]
    public void SettingsSwitchOffDuringWaitingEndsTheGrantAndShowsTheDeclinedCard(GrokBotConsent.Card recorded)
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = GrantedOver(shown, recorded);
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.Declined, Waiting: false),
            waiting.Decide(Marked, false, shown, 2));
        // Cleared, not paused: a later yes over the same payload is the normal
        // decision (Ask, Allow kept to re-send), not the recorded card.
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.Ask, Waiting: false),
            waiting.Decide(Marked, true, shown, 2));
    }

    // Scenario A: Allow, then the fetch fails. Waiting ends; the text does not.
    [Theory]
    [InlineData(GrokBotConsent.Card.Ask)]
    [InlineData(GrokBotConsent.Card.Declined)]
    public void FailedFetchEndsWaitingAndKeepsTheText(GrokBotConsent.Card recorded)
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = GrantedOver(shown, recorded);
        var after = waiting.Decide(Marked, true, shown, 3);
        Assert.Equal(new GrokBotConsent.Prompt(recorded, Waiting: false), after);
        Assert.Equal(GrokBotConsent.TextFor(recorded), after.Text);
        Assert.Equal(recorded == GrokBotConsent.Card.Ask, after.ShowsNotNow && after.NotNowEnabled);
    }

    // Scenario B: the refresh joined a fetch begun before the grant and
    // brought a new consent payload. Waiting ends; the text does not.
    [Theory]
    [InlineData(GrokBotConsent.Card.Ask)]
    [InlineData(GrokBotConsent.Card.Declined)]
    public void NewConsentPayloadEndsWaitingAndKeepsTheText(GrokBotConsent.Card recorded)
    {
        var waiting = GrantedOver(new AgentUsagePayload("now", []), recorded);
        var after = waiting.Decide(Marked, true, new AgentUsagePayload("now", []), 2);
        Assert.Equal(new GrokBotConsent.Prompt(recorded, Waiting: false), after);
        Assert.Equal(GrokBotConsent.TextFor(recorded), after.Text);
    }

    [Fact]
    public void ARealPayloadClearsTheRecord()
    {
        var shown = new AgentUsagePayload("now", []);
        var waiting = GrantedOver(shown, GrokBotConsent.Card.Declined);
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.None, Waiting: false),
            waiting.Decide(Snapshot("grok-bot", "oauth"), true, shown, 2));
        // No record left: a later consent snapshot under the yes is the normal
        // decision, and the Settings switch then has no card drawn to keep.
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.Ask, Waiting: false),
            waiting.Decide(Marked, true, shown, 2));
        var cleared = new GrokBotConsent.WaitingState();
        cleared.Decide(Marked, null, shown, 2);
        cleared.Decide(Snapshot("grok-bot", "oauth"), null, shown, 2);
        cleared.GrantedElsewhere();
        Assert.Equal(
            new GrokBotConsent.Prompt(GrokBotConsent.Card.Declined, Waiting: false),
            cleared.Decide(Marked, true, new AgentUsagePayload("now", []), 2));
    }

    [Fact]
    public void ConsentTablesHaveTheSameKeys()
    {
        var keys = new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" }
            .Select(table => JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!.Keys.ToHashSet())
            .ToList();
        Assert.True(keys[0].SetEquals(keys[1]));
        // The old long card copy is gone from both tables, not left orphaned.
        Assert.DoesNotContain(keys[0], key => key.StartsWith("To show your weekly Grok Bot limits", StringComparison.Ordinal)
            && key != GrokBotConsent.Copy.Explanation);
    }
}
