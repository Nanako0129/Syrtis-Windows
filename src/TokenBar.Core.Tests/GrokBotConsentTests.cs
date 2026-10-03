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
        Assert.Equal(GrokBotConsent.Card.Ask, GrokBotConsent.CardFor(marked, true));
        Assert.Equal(GrokBotConsent.Card.Declined, GrokBotConsent.CardFor(marked, false));
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

    [Fact]
    public void ConsentCardSaysWhereItGoesAndHowToStop()
    {
        Assert.Contains("api2.cursor.sh", GrokBotConsent.Copy.Explanation);
        Assert.EndsWith("You can stop this any time in Settings.", GrokBotConsent.Copy.Explanation);
        Assert.Contains("without encryption", GrokBotConsent.Copy.SettingsHint);
    }
}
