using System.Collections.Concurrent;
using System.Text.Json;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The C# half of captured Antigravity accounts (W7b): the launch install,
/// the confirmation before automatic capture, the auto-capture state machine,
/// dedup with history adoption, the pre-fetch step, pills and the error map.
/// Every native call is a fake; nothing here reads a real credential.
/// </summary>
public class AntigravityAccountsTests
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const long Hour = 3600;

    private static SettingsStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "settings.json"));

    /// <summary>Fake native calls; every call is recorded in order.</summary>
    private sealed class FakeIo
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public readonly Queue<string> Markers = new();
        public string LastMarker = "M1";
        public Func<AntigravityAccount> CaptureResult = () => new AntigravityAccount(KeyA, "a@example.com");
        public Func<AntigravityAutoCaptureResult> AutoResult =
            () => new AntigravityAutoCaptureResult("captured", KeyA, "a@example.com");
        public Exception? RemoveError;
        public ManualResetEventSlim? AutoGate;

        public AntigravityAutoCapture.Io Io => new(
            Marker: () =>
            {
                Calls.Enqueue("marker");
                lock (Markers)
                {
                    if (Markers.Count > 0)
                    {
                        LastMarker = Markers.Dequeue();
                    }

                    return LastMarker;
                }
            },
            AutoCapture: removed =>
            {
                Calls.Enqueue("auto");
                AutoGate?.Wait(TimeSpan.FromSeconds(10));
                return AutoResult();
            },
            Capture: () =>
            {
                Calls.Enqueue("capture");
                return CaptureResult();
            },
            Remove: key =>
            {
                Calls.Enqueue("remove");
                if (RemoveError is { } error)
                {
                    throw error;
                }
            },
            Install: () => Calls.Enqueue("install"));
    }

    private static UsageWindow Window(string cardId, double remaining, PaceStatus? pace = null, HistoricalPace? historical = null) =>
        new(Label: "Weekly", UsedPercent: 100 - remaining, RemainingPercent: remaining,
            ResetsAt: "2026-10-10T00:00:00Z", CardId: cardId, PaceStatus: pace, HistoricalPace: historical);

    private static readonly PaceStatus AgyPace = new(UsagePaceState.Unavailable, WindowKey: "weekly.v1", Reason: UsagePaceUnavailableReason.AccountScope);

    private static readonly PaceStatus CapturedPace = new(
        UsagePaceState.Available, WindowKey: "weekly.v1", DurationSeconds: 168 * Hour,
        DurationSource: UsagePaceDurationSource.Contract, CompleteCycles: 3);

    private static AgentUsageSnapshot Primary(
        string source = "agy", string? marker = "M1", string? error = null) =>
        new("antigravity", source, "2026-10-04T00:00:00Z",
            [Window("antigravity.weekly", 70, AgyPace)],
            Identity: new AgentIdentity("primary@example.com", "Pro"),
            Error: error,
            HistoryScope: new AccountScopeStatus(Error: "noTrustedEvidence"),
            AgyLoginMarker: marker);

    private static AgentUsageSnapshot Captured(
        string key = KeyA, string? error = null, bool windows = true) =>
        new("antigravity", "oauth", "2026-10-04T00:00:00Z",
            windows ? [Window("antigravity.weekly", 70, CapturedPace, new HistoricalPace(40, WillLastToReset: true))] : [],
            Identity: new AgentIdentity("a@example.com"),
            Error: error,
            HistoryScope: new AccountScopeStatus(Scope: "scope-a"),
            AccountKey: key);

    private static AgentUsagePayload Payload(params AgentUsageSnapshot[] agents) => new("2026-10-04T00:00:00Z", agents);

    // ---- 1. launch reinstall --------------------------------------------------

    [Fact]
    public void LaunchInstallReachesTheSetterBeforeTheFirstFetch()
    {
        var store = TempStore();
        AntigravityAccounts.Save(store, [new AntigravityAccount(KeyA, "a@example.com")]);
        var calls = new List<string>();
        var installer = new AntigravityAccountsInstaller(
            () => AntigravityAccounts.PayloadJson(AntigravityAccounts.Load(store)),
            json =>
            {
                calls.Add("set:" + json);
                return new RootsResult(1, []);
            },
            _ => { });

        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, installer, null);
        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, installer, null);

        Assert.Equal(
            ["set:[{\"key\":\"" + KeyA + "\",\"label\":\"a@example.com\"}]", "fetch", "fetch"],
            calls);
    }

    [Fact]
    public void AnEmptyListCostsNoSetterCallAndAFailedInstallIsRetried()
    {
        var store = TempStore();
        var sets = 0;
        var fail = true;
        var installer = new AntigravityAccountsInstaller(
            () => AntigravityAccounts.PayloadJson(AntigravityAccounts.Load(store)),
            _ =>
            {
                sets++;
                return fail ? throw new TbCoreException("invalid_accounts_json") : new RootsResult(1, []);
            },
            _ => { });

        installer.Install();
        Assert.Equal(0, sets);

        AntigravityAccounts.Save(store, [new AntigravityAccount(KeyA, "a@example.com")]);
        installer.Install();
        fail = false;
        installer.Install();
        installer.Install();
        Assert.Equal(2, sets);
    }

    // ---- 2. confirmation ------------------------------------------------------

    [Fact]
    public async Task EnablingAsksFirstAndCancelStoresAndCallsNothing()
    {
        var store = TempStore();
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);

        var on = await capture.TurnOn(() =>
        {
            io.Calls.Enqueue("confirm");
            return Task.FromResult(false);
        });

        Assert.False(on);
        Assert.False(store.TryGetString(AntigravityAutoCapture.EnabledKey, out _));
        Assert.False(capture.IsEnabled);
        Assert.Equal(["confirm"], io.Calls.ToList());

        on = await capture.TurnOn(() =>
        {
            io.Calls.Enqueue("confirm");
            return Task.FromResult(true);
        });

        Assert.True(on);
        Assert.True(capture.IsEnabled);
        Assert.Equal(["confirm", "confirm", "marker", "auto", "install"], io.Calls.ToList());
    }

    // ---- 3. toggle off (R7-1) -------------------------------------------------

    [Fact]
    public async Task WithAutomaticCaptureOffAMarkerChangeNeverCallsAutoCapture()
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M2");
        io.Markers.Enqueue("M3");
        var capture = new AntigravityAutoCapture(io.Io, store);
        var fetches = 0;

        for (var i = 0; i < 3; i++)
        {
            AntigravityFetch.Run(() => { fetches++; return Payload(Primary()); }, null, capture);
        }

        await Task.Delay(300); // a capture attempt is started, not awaited
        Assert.Equal(3, fetches);
        Assert.DoesNotContain("auto", io.Calls);
        Assert.DoesNotContain("marker", io.Calls);
    }

    // ---- 4. dedup truth table -------------------------------------------------

    public static TheoryData<string> GuardCases() => new()
    {
        "noCurrentKey", "noMarker", "markerPresent", "markerMismatch",
        "primaryError", "sourceNotAgy", "noCapturedSnapshot",
    };

    [Fact]
    public void DedupMergesWhenEveryGuardHolds()
    {
        var merged = AntigravityDedup.Apply(Payload(Primary(), Captured()), KeyA, "M1");

        var card = Assert.Single(merged.Agents);
        Assert.Null(card.AccountKey);
        Assert.Equal("a@example.com", card.Identity?.Email);
        Assert.Equal("Pro", card.Identity?.Plan);
        Assert.Equal(merged, AntigravityDedup.Apply(merged, KeyA, "M1")); // idempotent
    }

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void DedupLeavesBothCardsWhenAnyOneGuardFails(string failing)
    {
        var key = failing == "noCurrentKey" ? null : KeyA;
        var marker = failing switch
        {
            "noMarker" => null,
            "markerPresent" => "present",
            _ => "M1",
        };
        var primary = Primary(
            source: failing == "sourceNotAgy" ? "cli" : "agy",
            marker: failing switch
            {
                "markerPresent" => "present",
                "markerMismatch" => "M2",
                _ => "M1",
            },
            error: failing == "primaryError" ? "paused" : null);
        var payload = Payload(primary, Captured(failing == "noCapturedSnapshot" ? KeyB : KeyA));

        Assert.Same(payload, AntigravityDedup.Apply(payload, key, marker));
    }

    // ---- 5. history adoption --------------------------------------------------

    [Fact]
    public void HistoryIsAdoptedOnlyFromACapturedCardWithoutErrorAndWithWindows()
    {
        var withError = AntigravityDedup.AdoptingHistory(Primary(), Captured(error: "refresh_rejected"));
        Assert.Null(withError.HistoryAccountKey);
        Assert.Equal(AgyPace, Assert.Single(withError.Windows).PaceStatus);

        var noWindows = AntigravityDedup.AdoptingHistory(Primary(), Captured(windows: false));
        Assert.Null(noWindows.HistoryAccountKey);

        var adopted = AntigravityDedup.AdoptingHistory(Primary(), Captured());
        Assert.Equal(KeyA, adopted.HistoryAccountKey);
        Assert.Null(adopted.AccountKey);
        var window = Assert.Single(adopted.Windows);
        Assert.Equal(CapturedPace, window.PaceStatus);
        Assert.Equal(30, window.UsedPercent); // the primary's own value is kept
        Assert.Equal("scope-a", adopted.HistoryReadScope?.Scope);
    }

    [Fact]
    public void AMergedCardWithAnErroredCapturedCardKeepsItsOwnHistory()
    {
        var merged = AntigravityDedup.Apply(Payload(Primary(), Captured(error: "refresh_rejected")), KeyA, "M1");

        var card = Assert.Single(merged.Agents);
        Assert.Null(card.HistoryAccountKey);
        Assert.Equal("noTrustedEvidence", card.HistoryReadScope?.Error);
    }

    [Fact]
    public void AMergedCardReadsItsHistoryUnderTheCapturedAccount()
    {
        QuotaHistorySample Sample(double used) => new(
            ResetAt: 200 * Hour, DurationSeconds: 168 * Hour, DurationSource: QuotaHistoryDurationSource.Contract,
            UsedPercent: used, SampledAt: 100 * Hour, Origin: QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true);
        QuotaHistorySeries[] history =
        [
            new("antigravity", "scope-other", "weekly.v1", [Sample(90)]),
            new("antigravity", "scope-a", "weekly.v1", [Sample(30)]),
        ];
        var merged = AntigravityDedup.Apply(Payload(Primary(), Captured()), KeyA, "M1");

        var tab = Assert.Single(WindowCardText.Tabs(history, merged, "antigravity"));
        Assert.Equal("scope-a", tab.Id.AccountScope);
        Assert.True(tab.HasHistory);
        Assert.Equal(KeyA, merged.Agents[0].HistoryAccountKey);
    }

    // ---- 6. pre-fetch ---------------------------------------------------------

    [Fact]
    public async Task WhenOnEveryFetchRunsThePreFetchStepFirstAndNeverWaitsForTheCapture()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var gate = new ManualResetEventSlim(false);
        var io = new FakeIo { AutoGate = gate };
        var capture = new AntigravityAutoCapture(io.Io, store);

        var run = Task.Run(() => AntigravityFetch.Run(
            () => { io.Calls.Enqueue("fetch"); return Payload(Primary(), Captured()); }, null, capture));

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        var calls = io.Calls.ToList();
        Assert.True(calls.IndexOf("marker") >= 0 && calls.IndexOf("marker") < calls.IndexOf("fetch"));
        gate.Set();
    }

    [Fact]
    public async Task TheFetchStepAppliesDedupToWhatItReturns()
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M1");
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();

        var payload = AntigravityFetch.Run(() => Payload(Primary(), Captured()), null, capture);

        Assert.Single(payload.Agents);
    }

    // ---- 7. manual capture binding -------------------------------------------

    [Theory]
    [InlineData("M1", "M1", true)]
    [InlineData("M1", "M2", false)]
    public async Task ManualCaptureBindsOnlyWhenTheMarkerDidNotMove(string before, string after, bool bound)
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue(before);
        io.Markers.Enqueue(after);
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.ManualCapture();

        if (bound)
        {
            Assert.Equal((KeyA, after), capture.Current);
        }
        else
        {
            Assert.Null(capture.Current.Key);
        }

        Assert.Equal([new AntigravityAccount(KeyA, "a@example.com")], AntigravityAccounts.Load(store));
        Assert.Equal(bound, store.GetString(AntigravityAutoCapture.CurrentKey) is not null);
    }

    [Fact]
    public async Task TheBindingSurvivesARelaunch()
    {
        var store = TempStore();
        var io = new FakeIo();
        await new AntigravityAutoCapture(io.Io, store).ManualCapture();

        Assert.Equal((KeyA, "M1"), new AntigravityAutoCapture(io.Io, store).Current);
    }

    // ---- parity rules of the state machine (mac SW:267-415) -------------------

    /// <summary>An attempt that lands while the toggle is off (an owed poll
    /// finishing after the toggle was turned off) adds the account but never
    /// binds it as agy's current account.</summary>
    [Fact]
    public async Task AnAttemptWhileTheToggleIsOffNeverBinds()
    {
        var store = TempStore();
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.Poll();

        Assert.Contains("auto", io.Calls);
        Assert.Equal([new AntigravityAccount(KeyA, "a@example.com")], AntigravityAccounts.Load(store));
        Assert.Null(capture.Current.Key);
        Assert.Null(store.GetString(AntigravityAutoCapture.CurrentKey));
    }

    /// <summary>Maintainer decision (mac SW:354-367): turning the toggle off
    /// keeps the current binding, still bound to its marker.</summary>
    [Fact]
    public async Task TurningTheToggleOffKeepsTheBinding()
    {
        var store = TempStore();
        var capture = new AntigravityAutoCapture(new FakeIo().Io, store);
        await capture.ManualCapture();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);

        await capture.SetEnabled(false);

        Assert.Equal((KeyA, "M1"), capture.Current);
        Assert.False(capture.IsEnabled);
    }

    /// <summary>A <c>paused</c> result pauses and leaves no binding.</summary>
    [Fact]
    public async Task APausedResultPausesAndClearsTheBinding()
    {
        var store = TempStore();
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture(); // binds (KeyA, M1)
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        io.AutoResult = () => throw new TbCoreException("paused");
        io.Markers.Enqueue("M2");

        await capture.Poll();

        Assert.True(capture.Paused);
        Assert.Null(capture.Current.Key);
        Assert.Null(store.GetString(AntigravityAutoCapture.CurrentKey));
    }

    /// <summary>A successful manual Capture ends a pause, forgets the last
    /// attempted marker and, with the toggle on, polls again at once.</summary>
    [Fact]
    public async Task AManualCaptureEndsAPauseAndResumesPolling()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo();
        var attempts = 0;
        io.AutoResult = () => ++attempts == 1
            ? throw new TbCoreException("paused")
            : new AntigravityAutoCaptureResult("unchanged", KeyA, "a@example.com");
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.Poll();
        Assert.True(capture.Paused);
        Assert.Equal("M1", capture.LastAttemptedMarker);

        await capture.ManualCapture();

        Assert.False(capture.Paused);
        Assert.Equal(2, attempts); // the resumed poll re-attempted the same marker
        Assert.Equal("M1", capture.LastAttemptedMarker);
        Assert.Equal((KeyA, "M1"), capture.Current);
    }

    // ---- 8. remove ------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveClearsTheBindingAndListsTheKeyOnlyWhileEnabled(bool enabled)
    {
        var store = TempStore();
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();
        Assert.Equal(KeyA, capture.Current.Key);
        store.SetBool(AntigravityAutoCapture.EnabledKey, enabled);

        await capture.Remove(KeyA);

        Assert.Null(capture.Current.Key);
        Assert.Empty(AntigravityAccounts.Load(store));
        Assert.Equal(enabled ? new[] { KeyA } : [], AntigravityAccounts.RemovedKeys(store));
    }

    [Fact]
    public async Task AFailedRemoveKeepsTheRowAndRecordsOnlyTheCode()
    {
        var store = TempStore();
        var io = new FakeIo { RemoveError = new TbCoreException("keychain_delete_failed") };
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();

        await capture.Remove(KeyA);

        Assert.Single(AntigravityAccounts.Load(store));
        Assert.Equal("keychain_delete_failed", capture.LastErrorCode);
        Assert.Equal(KeyA, capture.Current.Key);
    }

    [Fact]
    public async Task ManualCaptureTakesTheKeyOffTheRemovedList()
    {
        var store = TempStore();
        AntigravityAccounts.SaveRemovedKeys(store, [KeyA, KeyB]);
        await new AntigravityAutoCapture(new FakeIo().Io, store).ManualCapture();

        Assert.Equal([KeyB], AntigravityAccounts.RemovedKeys(store));
    }

    // ---- 9. pills (existing window-card behaviour, W7b data) ------------------

    [Fact]
    public void ACapturedAccountGetsAPillNamedByItsLabelNeverItsKey()
    {
        var previous = AccountLabel.AntigravityLabel;
        Localization.Load("en", AppContext.BaseDirectory);
        AccountLabel.AntigravityLabel = key => key == KeyA ? "a@example.com" : null;
        try
        {
            var pills = WindowCardText.AccountPills(Payload(Primary(), Captured()), "antigravity");

            Assert.Equal(new string?[] { null, KeyA }, pills.Select(p => p.Key));
            Assert.Contains("a@example.com", pills[1].Label);
            Assert.All(pills, p => Assert.DoesNotContain(KeyA, p.Label));
            Assert.Null(AccountLabel.Detail(new AccountIdentity("antigravity", KeyA)));
            Assert.Equal("Antigravity account", AccountLabel.Of(new AccountIdentity("antigravity", KeyB)));
        }
        finally
        {
            AccountLabel.AntigravityLabel = previous;
        }
    }

    [Fact]
    public void ACapturedAccountShowsTheUnattributedRuleAndRequestsNoScan()
    {
        Localization.Load("en", AppContext.BaseDirectory);
        var quota = Payload(Primary(), Captured());
        var primaryMessage = new WindowMessage(1_000, "antigravity-cli", "google", "m", 100, 0, 0, 0, 0, 0, true);
        var client = QuotaLensProjection.Build(
            history: [],
            quota,
            new UsagePayload(
                new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                    PricingMode.BestEffort, CostCoverage.Complete),
                new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []),
            windowUsage: new WindowUsage([primaryMessage], 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            new UsageAttribution.Table([], IsWritable: true),
            year: null,
            new QuotaLensProjection.Selection("antigravity", string.Empty, WindowCardAccount: KeyA)).Client!;

        Assert.Equal(KeyA, client.SelectedAccount);
        Assert.True(client.LocalUsageUnattributed);
        Assert.Empty(client.Messages);
        Assert.Equal("Local usage can't be attributed to this account yet.", WindowCardText.LocalUsageUnattributed());
        Assert.Empty(ClaudeExtraRoots.AttributableAccountKeys(quota));
    }

    // ---- 10. error map --------------------------------------------------------

    [Fact]
    public void EveryCaptureAndRemoveCodeHasItsOwnSentenceAndNoCodeIsShown()
    {
        var generic = AntigravityAccountsCopy.Message("not-a-code");
        Assert.Equal(AntigravityAccountsCopy.Generic, generic);
        foreach (var code in AntigravityAccountsCopy.Codes)
        {
            var sentence = AntigravityAccountsCopy.Message(code);
            Assert.NotEqual(generic, sentence);
            Assert.DoesNotContain(code, sentence);
        }

        foreach (var other in new[]
                 {
                     null, AntigravityAutoCapture.UnexpectedError, "invalid_key", "paused",
                     "tb_antigravity_capture panicked: SENTINEL-7f3a",
                 })
        {
            Assert.Equal(generic, AntigravityAccountsCopy.Message(other));
        }
    }

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    public void EverySettingsStringIsTranslated(string language)
    {
        Localization.Load(language, AppContext.BaseDirectory);
        try
        {
            foreach (var english in AntigravityAccountsCopy.All())
            {
                Assert.NotEqual(english, english.Localized());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // ---- DTO -------------------------------------------------------------------

    [Fact]
    public void TheLoginMarkerDecodesAndTheHistoryAccountIsNeverSerialized()
    {
        var payload = TbCore.DecodeEnvelope<AgentUsagePayload>(
            """
            {"ok":true,"data":{"generatedAt":"n","agents":[
              {"clientId":"antigravity","source":"agy","updatedAt":"n","windows":[],"agyLoginMarker":"133456"}]}}
            """);
        var primary = Assert.Single(payload.Agents);
        Assert.Equal("133456", primary.AgyLoginMarker);

        var json = JsonSerializer.Serialize(primary with { HistoryAccountKey = KeyA });
        Assert.DoesNotContain(KeyA, json);
    }
}
