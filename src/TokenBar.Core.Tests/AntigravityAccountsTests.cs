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
        public Exception? InstallError;
        /// <summary>The core refuses the NEXT list only (the production
        /// installer logs and answers false, never throws; a later install
        /// of the list the core still holds costs no setter call and answers
        /// true, so Remove's restore succeeds as in production).</summary>
        public bool InstallFails;
        public ManualResetEventSlim? AutoGate;
        public readonly TaskCompletionSource AutoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? MarkerGate;
        public HashSet<int> FailingMarkerReads = [];
        public int BlockedRead;
        public readonly TaskCompletionSource BlockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim BlockedReadGate = new(false);
        private int _markerReads;

        public AntigravityAutoCapture.Io Io => new(
            Marker: () =>
            {
                Calls.Enqueue("marker");
                MarkerGate?.Wait(TimeSpan.FromSeconds(10));
                var read = Interlocked.Increment(ref _markerReads);
                if (read == BlockedRead)
                {
                    BlockedReadStarted.TrySetResult();
                    BlockedReadGate.Wait(TimeSpan.FromSeconds(10));
                }

                if (FailingMarkerReads.Contains(read))
                {
                    throw new TbCoreException("marker_unavailable");
                }

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
                AutoStarted.TrySetResult();
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
            Install: () =>
            {
                Calls.Enqueue("install");
                if (InstallError is { } error)
                {
                    throw error;
                }

                if (InstallFails)
                {
                    InstallFails = false;
                    return false;
                }

                return true;
            });
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

    /// <summary>A failed primary. Production shape (the defaults): a fresh
    /// error card carries no agy source, no login marker and a
    /// <c>noTrustedEvidence</c> scope (<paramref name="scope"/> null). The
    /// core's re-sent last-good card (<paramref name="lastGood"/>) may carry
    /// the marker it was fetched under (<paramref name="marker"/>) and the
    /// scope it was fetched with.</summary>
    private static AgentUsageSnapshot ErroredPrimary(
        bool lastGood = false, string? marker = null, string? scope = null) => new(
        "antigravity", "oauth", "2026-10-04T00:00:00Z",
        lastGood ? [Window("antigravity.weekly", 60)] : [],
        Identity: lastGood ? new AgentIdentity(Plan: "Free") : null, // remote_identity(plan): no email
        Error: "timeout",
        HistoryScope: scope is null ? new AccountScopeStatus(Error: "noTrustedEvidence") : new AccountScopeStatus(Scope: scope),
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

    /// <summary>A binding setter that ignores the binding (tests that are not
    /// about plan E).</summary>
    private static readonly Action<string> NoBinding = _ => { };

    /// <summary>Plan E: the primary as the engine fills it from the bound
    /// captured account's OAuth result: source <c>oauth</c>, the pre-fetch
    /// marker, and that account's history scope.</summary>
    private static AgentUsageSnapshot Substituted(string marker = "M1", string? scope = "scope-a") =>
        new("antigravity", "oauth", "2026-10-04T00:00:00Z",
            [Window("antigravity.weekly", 70, CapturedPace, new HistoricalPace(40, WillLastToReset: true))],
            Identity: new AgentIdentity("a@example.com"),
            HistoryScope: new AccountScopeStatus(Scope: scope),
            AgyLoginMarker: marker);

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

        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, installer, null, NoBinding);
        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, installer, null, NoBinding);

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

        Assert.True(installer.Install(), "an unchanged empty list is held");
        Assert.Equal(0, sets);

        AntigravityAccounts.Save(store, [new AntigravityAccount(KeyA, "a@example.com")]);
        Assert.False(installer.Install(), "a failed setter must report it (review F3)");
        fail = false;
        Assert.True(installer.Install());
        Assert.True(installer.Install(), "unchanged after a success is held");
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
        // The second "marker" is the re-read before binding.
        Assert.Equal(["confirm", "confirm", "marker", "auto", "install", "marker"], io.Calls.ToList());
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
            AntigravityFetch.Run(() => { fetches++; return Payload(Primary()); }, null, capture, NoBinding);
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
        "sourceNotAgy", "noCapturedSnapshot",
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
            error: null);
        var payload = Payload(primary, Captured(failing == "noCapturedSnapshot" ? KeyB : KeyA));

        Assert.Same(payload, AntigravityDedup.Apply(payload, key, marker));
    }

    /// <summary>macOS 6dbd8cca (maintainer decision 2026-10-05, ported
    /// 2026-10-10): agy's verified captured account takes an errored
    /// primary's slot, so the error card, its empty rows and the duplicate
    /// account card are gone.</summary>
    [Fact]
    public void AnErroredPrimaryCarryingAgysMarkerIsReplacedByAgysVerifiedCapturedAccount()
    {
        var captured = Captured() with { Identity = new AgentIdentity("a@example.com", "Google AI Pro") };
        var payload = Payload(ErroredPrimary(lastGood: true, marker: "M1", scope: "scope-a"), captured, Captured(KeyB));

        var agents = AntigravityDedup.Apply(payload, KeyA, "M1").Agents;

        Assert.Equal(2, agents.Count);
        var card = agents[0];
        Assert.Null(card.AccountKey);
        Assert.Null(card.Error);
        Assert.Equal("oauth", card.Source);
        Assert.Equal(new AgentIdentity("a@example.com", "Google AI Pro"), card.Identity);
        Assert.Equal(captured.Windows, card.Windows);
        Assert.Equal(KeyA, card.HistoryAccountKey);
        Assert.Equal(new AccountScopeStatus(Scope: "scope-a"), card.HistoryReadScope);
        Assert.Equal(KeyB, agents[1].AccountKey);
    }

    /// <summary>MA-7: an errored primary with no marker evidence (a fresh
    /// error card, or a last-good card fetched under another login) is not
    /// replaced: both cards show and agy's own error stays visible.</summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "M0")]
    public void AnErroredPrimaryWithoutMatchingMarkerEvidenceStaysBesideTheCapturedCard(bool lastGood, string? marker)
    {
        var payload = Payload(ErroredPrimary(lastGood, marker), Captured());

        Assert.Same(payload, AntigravityDedup.Apply(payload, KeyA, "M1"));
    }

    /// <summary>The errored branch needs the merge's whole evidence: a
    /// substituted primary whose history scope differs from the captured
    /// card's (or has none) is not agy's account.</summary>
    [Theory]
    [InlineData("scope-other")]
    [InlineData(null)]
    public void AnErroredSubstitutedPrimaryWithAnotherScopeStaysBesideTheCapturedCard(string? scope)
    {
        var payload = Payload(ErroredPrimary(lastGood: true, marker: "M1", scope: scope), Captured());

        Assert.Same(payload, AntigravityDedup.Apply(payload, KeyA, "M1"));
    }

    public static TheoryData<string> StandInControls() => new()
    {
        "capturedError", "capturedNoWindows", "noCurrentKey", "noCapturedSnapshot", "markerPresent",
    };

    [Theory]
    [MemberData(nameof(StandInControls))]
    public void AnErroredPrimaryStaysWhenAgysAccountIsNotVerifiedOrCannotStandIn(string failing)
    {
        var payload = Payload(
            ErroredPrimary(lastGood: true, marker: "M1", scope: "scope-a"),
            Captured(
                failing == "noCapturedSnapshot" ? KeyB : KeyA,
                error: failing == "capturedError" ? "refresh_rejected" : null,
                windows: failing != "capturedNoWindows"));

        Assert.Same(payload, AntigravityDedup.Apply(
            payload, failing == "noCurrentKey" ? null : KeyA, failing == "markerPresent" ? "present" : "M1"));
    }

    [Fact]
    public void MergedPrimaryTakesTheCapturedPlanWhenItHasNone()
    {
        var noPlan = Primary() with { Identity = new AgentIdentity("primary@example.com") };
        var captured = Captured() with { Identity = new AgentIdentity("a@example.com", "Google AI Pro") };

        var card = Assert.Single(AntigravityDedup.Apply(Payload(noPlan, captured), KeyA, "M1").Agents);
        Assert.Equal("Google AI Pro", card.Identity?.Plan);
    }

    [Fact]
    public void TheCapturedPlanIsTakenEvenWhenTheCapturedIdentityHasNoEmail()
    {
        var noPlan = Primary() with { Identity = new AgentIdentity("primary@example.com") };
        var captured = Captured() with { Identity = new AgentIdentity(null, "Google AI Pro") };

        var card = Assert.Single(AntigravityDedup.Apply(Payload(noPlan, captured), KeyA, "M1").Agents);
        Assert.Equal("Google AI Pro", card.Identity?.Plan);
        Assert.Equal("primary@example.com", card.Identity?.Email);
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
            () => { io.Calls.Enqueue("fetch"); return Payload(Primary(), Captured()); }, null, capture, NoBinding));

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

        var payload = AntigravityFetch.Run(() => Payload(Primary(), Captured()), null, capture, NoBinding);

        Assert.Single(payload.Agents);
    }

    // ---- 6b. plan E: the bound substitution ----------------------------------

    [Fact]
    public async Task TheFetchHandsTheEngineTheCurrentBindingBeforeFetching()
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M1");
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();
        var calls = new List<string>();

        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, null, capture, json => calls.Add("bind:" + json));
        AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, null, null, json => calls.Add("bind:" + json));

        Assert.Equal(
            [
                "bind:{\"key\":\"" + KeyA + "\",\"marker\":\"M1\"}", "fetch",
                "bind:{\"key\":null}", "fetch",
            ],
            calls);
    }

    /// <summary>Plan E with automatic capture on: agy rewrote its credential
    /// (routine refresh) since the binding was stored, so the stored marker is
    /// stale. That poll's PrepareForFetch clears the binding and starts a
    /// re-capture it does not wait for (W7b: the fetch is not held for a
    /// capture), so THAT poll hands the engine no binding (agy route). Once
    /// the re-capture (unchanged account) rebinds under the new marker, the
    /// NEXT poll hands the engine (key, new marker), so E applies again. One
    /// agy run per credential rewrite; with E in effect Syrtis itself no
    /// longer runs agy, so rewrites come only from agy run elsewhere.</summary>
    [Fact]
    public async Task WithAutoCaptureOnARewrittenCredentialIsReboundForTheNextPoll()
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M1");
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();
        Assert.Equal((KeyA, "M1"), capture.Current);

        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        // Held until the first poll has handed over its binding: unheld, the
        // re-capture could finish first (it is started, not awaited), and that
        // poll would already be bound (seen on 188, 2026-10-09).
        using var captureGate = new ManualResetEventSlim();
        io.AutoResult = () =>
        {
            captureGate.Wait();
            return new AntigravityAutoCaptureResult("unchanged", KeyA, "a@example.com");
        };
        io.Markers.Enqueue("M2");
        var calls = new List<string>();
        void Poll() => AntigravityFetch.Run(() => { calls.Add("fetch"); return Payload(); }, null, capture, json => calls.Add("bind:" + json));

        Poll();
        captureGate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (capture.Current != (KeyA, "M2") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal((KeyA, "M2"), capture.Current);
        Poll();

        Assert.Equal(
            [
                "bind:{\"key\":null}", "fetch",
                "bind:{\"key\":\"" + KeyA + "\",\"marker\":\"M2\"}", "fetch",
            ],
            calls);
    }

    /// <summary>The poll right after agy rewrote its credential runs agy
    /// (its binding was cleared), and here agy fails. The background re-capture
    /// may rebind the account, under the new marker, before the fetch returns
    /// (dedup reads the binding after the fetch). Rebound in time (the re-capture finished
    /// during agy's run, up to its 35 s timeout): one card, the captured
    /// account. Not yet: the error card stays, with the captured card beside
    /// it (no account is verified for the new marker; limits-card shared spec
    /// §2.3). The errored card here is a last-good one carrying the new
    /// marker, so the promotion path stays reachable.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AfterARewriteAnErroredAgyPollIsStoodInOnlyOnceTheReCaptureRebound(bool reboundInTime)
    {
        var store = TempStore();
        var io = new FakeIo();
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M1");
        var capture = new AntigravityAutoCapture(io.Io, store);
        await capture.ManualCapture();

        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        io.AutoResult = () => new AntigravityAutoCaptureResult("unchanged", KeyA, "a@example.com");
        using var captureGate = new ManualResetEventSlim();
        io.AutoGate = captureGate;
        io.Markers.Enqueue("M2");
        string? bound = null;

        var payload = AntigravityFetch.Run(
            () =>
            {
                if (reboundInTime)
                {
                    captureGate.Set();
                    Assert.True(
                        SpinWait.SpinUntil(() => capture.Current == (KeyA, "M2"), TimeSpan.FromSeconds(10)),
                        "re-capture did not rebind in time");
                }

                // A re-sent last-good card carrying the new login's marker
                // is the one errored card the stand-in may replace.
                return Payload(ErroredPrimary(lastGood: true, marker: "M2", scope: "scope-a"), Captured());
            },
            null,
            capture,
            json => bound = json);
        captureGate.Set();

        Assert.Equal("{\"key\":null}", bound); // this poll ran agy
        if (reboundInTime)
        {
            var card = Assert.Single(payload.Agents);
            Assert.Null(card.Error);
            Assert.Equal(KeyA, card.HistoryAccountKey);
        }
        else
        {
            Assert.Equal(2, payload.Agents.Count);
            Assert.Equal("timeout", payload.Agents[0].Error);
        }
    }

    [Fact]
    public void ARefusedBindingDoesNotStopTheFetch()
    {
        var fetched = false;
        AntigravityFetch.Run(
            () => { fetched = true; return Payload(); }, null, null,
            _ => throw new TbCoreException("invalid_marker"));
        Assert.True(fetched);
        Assert.Equal("{\"key\":null}", AntigravityFetch.BindingJson(KeyA, null));
        Assert.Equal("{\"key\":null}", AntigravityFetch.BindingJson(null, "M1"));
    }

    /// <summary>T8, T10: a substituted primary with the current marker and the
    /// captured card's history scope merges into one card under the captured
    /// account's history, and reads OAUTH; an agy-route primary reads AGY.</summary>
    [Fact]
    public void DedupMergesASubstitutedPrimaryUnderTheCapturedHistory()
    {
        var merged = AntigravityDedup.Apply(Payload(Substituted(), Captured()), KeyA, "M1");

        var card = Assert.Single(merged.Agents);
        Assert.Null(card.AccountKey);
        Assert.Equal("oauth", card.Source);
        Assert.Equal(KeyA, card.HistoryAccountKey);
        Assert.Equal("scope-a", card.HistoryReadScope?.Scope);
        Assert.Equal("a@example.com", card.Identity?.Email);
        Assert.Equal(merged, AntigravityDedup.Apply(merged, KeyA, "M1")); // idempotent
        Assert.Equal(new LimitsBadge("OAUTH", LimitsTone.Secondary), AgentLimitsText.StatusBadge(card, isLive: false));
        Assert.Equal(new LimitsBadge("AGY", LimitsTone.Secondary), AgentLimitsText.StatusBadge(Primary(), isLive: false));
    }

    /// <summary>T9: substituted, but not under agy's current marker.</summary>
    [Fact]
    public void DedupKeepsBothCardsForASubstitutedPrimaryUnderAnotherMarker()
    {
        var payload = Payload(Substituted(marker: "M2"), Captured());
        Assert.Same(payload, AntigravityDedup.Apply(payload, KeyA, "M1"));
    }

    /// <summary>T12: the <c>oauth_creds.json</c> route also reads
    /// <c>oauth</c> but never carries a marker: no merge, even with the
    /// captured card's history scope (so the marker guard alone decides).</summary>
    [Fact]
    public void DedupNeverMergesAnOauthCredsPrimary()
    {
        var oauthCreds = Primary(source: "oauth", marker: null) with { HistoryScope = new AccountScopeStatus(Scope: "scope-a") };
        var payload = Payload(oauthCreds, Captured());
        Assert.Same(payload, AntigravityDedup.Apply(payload, KeyA, "M1"));
    }

    /// <summary>T15: the marker matches but the substituted primary is not the
    /// current key's account (another history scope, or none).</summary>
    [Theory]
    [InlineData("scope-b")]
    [InlineData(null)]
    public void DedupKeepsBothCardsWhenTheSubstitutedHistoryScopeDiffers(string? scope)
    {
        var payload = Payload(Substituted(scope: scope), Captured());
        Assert.Same(payload, AntigravityDedup.Apply(payload, KeyA, "M1"));
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

    /// <summary>An attempt that lands after the toggle went off (turned off
    /// while the native call ran) adds the account but never binds it as
    /// agy's current account.</summary>
    [Fact]
    public async Task AnAttemptThatLandsAfterTheToggleWentOffNeverBinds()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var gate = new ManualResetEventSlim(false);
        var io = new FakeIo { AutoGate = gate };
        var capture = new AntigravityAutoCapture(io.Io, store);

        var attempt = capture.Poll();
        Assert.Same(io.AutoStarted.Task, await Task.WhenAny(io.AutoStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.SetEnabled(false);
        gate.Set();
        await attempt;

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

    // ---- review fixes (f404f05..9c57337 /code-review) ------------------------

    /// <summary>An automatic attempt binds only when agy's marker is the same
    /// after the attempt as when it started (stricter than macOS SW:306).</summary>
    [Theory]
    [InlineData("M1", "M1", true)]
    [InlineData("M1", "M2", false)]
    public async Task AnAttemptBindsOnlyIfTheMarkerHeldDuringIt(string before, string after, bool bound)
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo();
        io.Markers.Enqueue(before);
        io.Markers.Enqueue(after);
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.Poll();

        Assert.Equal([new AntigravityAccount(KeyA, "a@example.com")], AntigravityAccounts.Load(store));
        Assert.Null(capture.LastErrorCode);
        if (bound)
        {
            Assert.Equal((KeyA, before), capture.Current);
        }
        else
        {
            Assert.Null(capture.Current.Key);
        }
    }

    /// <summary>Verifier F1 on dd1a6e0: the install the shared fetch does
    /// before its own tb_agent_usage call (a stored account at launch) owes
    /// no follow-up, while a list change during an in-flight fetch (Mutate:
    /// a user action or a capture landing) still owes exactly one.</summary>
    [Fact]
    public async Task TheFetchPathInstallOwesNoFollowUpButAMutateDuringTheFetchDoes()
    {
        var store = TempStore();
        AntigravityAccounts.Save(store, [new AntigravityAccount(KeyA, "a@example.com")]);
        var installer = new AntigravityAccountsInstaller(
            () => AntigravityAccounts.PayloadJson(AntigravityAccounts.Load(store)),
            _ => new RootsResult(1, []),
            _ => { });
        var calls = 0;
        Action? duringFirst = null;
        var coordinator = new AgentUsageFetchCoordinator(() => AntigravityFetch.Run(
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    duringFirst?.Invoke();
                }

                return Payload();
            },
            installer,
            null,
            NoBinding));
        AntigravityAccounts.Changed += coordinator.RequestFollowUp;
        try
        {
            await coordinator.FetchAsync(); // launch: installs [KeyA]
            Assert.Equal(1, Volatile.Read(ref calls));

            calls = 0;
            duringFirst = () => AntigravityAccounts.Mutate(store, installer.Install, accounts =>
                AntigravityAccounts.Adding(new AntigravityAccount(KeyB, "b@example.com"), accounts));
            await coordinator.FetchAsync();
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            AntigravityAccounts.Changed -= coordinator.RequestFollowUp;
        }
    }

    /// <summary>Verifier A1 on dd1a6e0: a post-attempt marker re-read that
    /// fails (not a moved marker) forgets the attempted marker, so the next
    /// poll retries and binds.</summary>
    [Fact]
    public async Task AnUnreadableReReadIsRetriedByTheNextPoll()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo { FailingMarkerReads = [2] };
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.Poll();
        Assert.Null(capture.Current.Key);
        Assert.Null(capture.LastAttemptedMarker);

        io.AutoResult = () => new AntigravityAutoCaptureResult("unchanged", KeyA, "a@example.com");
        await capture.Poll();
        Assert.Equal((KeyA, "M1"), capture.Current);
    }

    /// <summary>The toggle turned off while the post-attempt marker re-read
    /// runs: the re-read then returns the same marker, yet nothing is bound
    /// or persisted, the retry is not armed, and the account stays listed
    /// (macOS 215df805).</summary>
    [Fact]
    public async Task TurningTheToggleOffDuringTheReReadBindsNothing()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        // A bound longer than the test's own wait, so the read is not timed out.
        var io = new FakeIo { BlockedRead = 2 };
        var capture = new AntigravityAutoCapture(io.Io, store, markerTimeout: TimeSpan.FromSeconds(10));

        var poll = capture.Poll();
        Assert.Same(io.BlockedReadStarted.Task, await Task.WhenAny(io.BlockedReadStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.SetEnabled(false);
        io.BlockedReadGate.Set();
        await poll;

        Assert.Null(capture.Current.Key);
        Assert.Null(store.GetString(AntigravityAutoCapture.CurrentKey));
        Assert.Equal("M1", capture.LastAttemptedMarker);
        Assert.Equal([new AntigravityAccount(KeyA, "a@example.com")], AntigravityAccounts.Load(store));
    }

    /// <summary>macOS 065df148: a poll owed during an attempt (refused while
    /// busy) does not run once the toggle is off. The re-read fails while
    /// still on (arming the retry), and the toggle goes off just after the
    /// attempt's busy section ends, before the owed poll would run.</summary>
    [Fact]
    public async Task AnOwedPollDoesNotCaptureAfterTheToggleWentOff()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var gate = new ManualResetEventSlim(false);
        var io = new FakeIo { AutoGate = gate, FailingMarkerReads = [2] };
        var capture = new AntigravityAutoCapture(io.Io, store);

        var attempt = capture.Poll();
        Assert.Same(io.AutoStarted.Task, await Task.WhenAny(io.AutoStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.Poll(); // refused while busy: owed
        // The production toggle writes this key first (SetEnabled(false)).
        capture.StateChanged += () =>
        {
            if (!capture.Busy)
            {
                store.SetBool(AntigravityAutoCapture.EnabledKey, false);
            }
        };
        gate.Set();
        await attempt;

        Assert.Equal(1, io.Calls.Count(c => c == "auto"));
        Assert.Null(capture.Current.Key);
        Assert.Null(store.GetString(AntigravityAutoCapture.CurrentKey));
    }

    /// <summary>The owed poll's own marker read blocks; the toggle goes off
    /// meanwhile and the read returns a NEW marker: Poll's commit point
    /// starts nothing (one native auto-capture call in total).</summary>
    [Fact]
    public async Task AnOwedPollWhoseMarkerReadOutlastsTheToggleCapturesNothing()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var gate = new ManualResetEventSlim(false);
        var io = new FakeIo { AutoGate = gate, BlockedRead = 3 };
        io.Markers.Enqueue("M1"); // the attempt
        io.Markers.Enqueue("MX"); // its re-read: moved, so nothing binds
        io.Markers.Enqueue("M2"); // the owed poll's read: a new login
        var capture = new AntigravityAutoCapture(io.Io, store, markerTimeout: TimeSpan.FromSeconds(10));

        var attempt = capture.Poll();
        Assert.Same(io.AutoStarted.Task, await Task.WhenAny(io.AutoStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.Poll(); // refused while busy: owed
        gate.Set();
        Assert.Same(io.BlockedReadStarted.Task, await Task.WhenAny(io.BlockedReadStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.SetEnabled(false);
        io.BlockedReadGate.Set();
        await attempt;

        Assert.Equal(1, io.Calls.Count(c => c == "auto"));
        Assert.Null(capture.Current.Key);
        Assert.Null(store.GetString(AntigravityAutoCapture.CurrentKey));
    }

    /// <summary>The same window on the fetch path: PrepareForFetch's marker
    /// read blocks, the toggle goes off, the read returns a new marker, and
    /// the poll it starts captures nothing.</summary>
    [Fact]
    public async Task APreFetchMarkerReadThatOutlastsTheToggleCapturesNothing()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo { BlockedRead = 1 };
        io.Markers.Enqueue("M2");
        var capture = new AntigravityAutoCapture(io.Io, store, markerTimeout: TimeSpan.FromSeconds(10));

        var prepare = capture.PrepareForFetch();
        Assert.Same(io.BlockedReadStarted.Task, await Task.WhenAny(io.BlockedReadStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await capture.SetEnabled(false);
        io.BlockedReadGate.Set();
        if (await prepare is { } poll)
        {
            await poll;
        }

        Assert.DoesNotContain("auto", io.Calls);
        Assert.Null(capture.Current.Key);
    }

    [Fact]
    public async Task AThrowingInstallerOrSubscriberNeverLeavesItBusy()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo { InstallError = new InvalidOperationException() };
        io.Markers.Enqueue("M1");
        io.Markers.Enqueue("M2");
        var capture = new AntigravityAutoCapture(io.Io, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.Poll());
        Assert.False(capture.Busy);

        io.InstallError = null;
        await capture.Poll();
        Assert.Equal(2, io.Calls.Count(c => c == "auto"));

        capture.StateChanged += () => throw new InvalidOperationException();
        await capture.ManualCapture();
        Assert.False(capture.Busy);
        Assert.Equal(KeyA, capture.Current.Key);
    }

    [Fact]
    public async Task APreFetchThatChangesNothingRaisesNoStateChanged()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var capture = new AntigravityAutoCapture(new FakeIo().Io, store);
        await (await capture.PrepareForFetch())!; // attempts M1 once

        var raised = 0;
        capture.StateChanged += () => Interlocked.Increment(ref raised);
        await (await capture.PrepareForFetch())!;

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AStalledMarkerReadNeitherHoldsTheFetchNorCaptures()
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var gate = new ManualResetEventSlim(false);
        var io = new FakeIo { MarkerGate = gate };
        var capture = new AntigravityAutoCapture(io.Io, store, markerTimeout: TimeSpan.FromMilliseconds(200));

        var run = Task.Run(() => AntigravityFetch.Run(
            () => { io.Calls.Enqueue("fetch"); return Payload(Primary()); }, null, capture, NoBinding));

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))));
        Assert.Contains("fetch", io.Calls);
        Assert.DoesNotContain("auto", io.Calls);
        gate.Set();
    }

    [Theory]
    [InlineData("FFI returned NULL", AntigravityAutoCapture.UnexpectedError)]
    [InlineData("FFI envelope missing boolean 'ok'", AntigravityAutoCapture.UnexpectedError)]
    [InlineData("account_mismatch", "account_mismatch")]
    public async Task OnlyAKnownCoreCodeIsRecordedAsTheError(string message, string recorded)
    {
        var io = new FakeIo { CaptureResult = () => throw new TbCoreException(message) };
        var capture = new AntigravityAutoCapture(io.Io, TempStore());

        await capture.ManualCapture();

        Assert.Equal(recorded, capture.LastErrorCode);
    }

    /// <summary>WIN-P9 S2 G3: a failed automatic attempt logs only the fixed
    /// core code, never the exception's message or type. The messages carry
    /// a fixture secret; a known code is logged as itself, anything else as
    /// <see cref="AntigravityAutoCapture.UnexpectedError"/>.</summary>
    [Theory]
    [InlineData("account_mismatch", false, "account_mismatch")]
    [InlineData("refresh_rejected 1//SENTINEL-REFRESH GOCSPX-SENTINEL", false, AntigravityAutoCapture.UnexpectedError)]
    [InlineData("1//SENTINEL-REFRESH GOCSPX-SENTINEL", true, AntigravityAutoCapture.UnexpectedError)]
    public async Task AFailedAutoCaptureLogsOnlyTheCode(string message, bool otherType, string code)
    {
        var store = TempStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        var io = new FakeIo
        {
            AutoResult = () => throw (otherType ? new InvalidOperationException(message) : (Exception)new TbCoreException(message)),
        };
        var lines = new ConcurrentQueue<string>();
        var capture = new AntigravityAutoCapture(io.Io, store, lines.Enqueue);

        await capture.Poll();

        Assert.Contains("auto", io.Calls);
        Assert.Equal(
            new[] { $"antigravity auto-capture failed: {code}" },
            lines.Where(line => line.StartsWith("antigravity auto-capture failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.Contains("SENTINEL", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Exception", StringComparison.Ordinal));
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

    /// <summary>Slice W item 16b (security review S10): the core sees the
    /// list without the key before the credential delete reaches it, so its
    /// token write-back (check, write, re-check) cannot recreate the item.</summary>
    [Fact]
    public async Task RemoveInstallsTheListWithoutTheKeyBeforeDeletingItsCredential()
    {
        var store = TempStore();
        AntigravityAccounts.Save(store, [new(KeyA, "a@example.com"), new(KeyB, "b@example.com")]);
        var events = new ConcurrentQueue<string>();
        var io = new FakeIo().Io with
        {
            Install = () =>
            {
                events.Enqueue("install:" + string.Join(",", AntigravityAccounts.Load(store).Select(a => a.Key)));
                return true;
            },
            Remove = key => events.Enqueue("remove:" + key),
        };

        await new AntigravityAutoCapture(io, store).Remove(KeyA);

        Assert.Equal(["install:" + KeyB, "remove:" + KeyA], events);
        Assert.Equal([KeyB], AntigravityAccounts.Load(store).Select(a => a.Key));
    }

    /// <summary>Review F3: the core did not take the list without the key, so
    /// the key is still registered and an in-flight refresh could write the
    /// credential back. Nothing is deleted, the row stays where it was, and the
    /// account-list sentence (not the Credential Manager one) is shown.</summary>
    [Fact]
    public async Task ARemoveWhoseRegistryInstallFailsDeletesNothing()
    {
        var store = TempStore();
        AntigravityAccounts.Save(store, [new(KeyA, "a@example.com"), new(KeyB, "b@example.com")]);
        var io = new FakeIo { InstallFails = true };
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.Remove(KeyA);

        Assert.DoesNotContain("remove", io.Calls);
        // The drop install failed; the restore install answered true.
        Assert.Equal(2, io.Calls.Count(c => c == "install"));
        Assert.Equal([KeyA, KeyB], AntigravityAccounts.Load(store).Select(a => a.Key));
        Assert.Equal(AntigravityAutoCapture.AccountListUpdateFailed, capture.LastErrorCode);
    }

    /// <summary>Review F1: while this session's settings cannot be saved
    /// (here an in-memory store; the failed-move path is in
    /// AntigravityLocalSettingsTests), Capture and turning automatic capture
    /// on are refused before any native call or confirmation.</summary>
    [Fact]
    public async Task WhileTheSettingsCannotBeSavedCaptureAndTurningOnAreRefused()
    {
        var store = SettingsStore.InMemory();
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);
        Assert.Equal(AntigravityAutoCapture.SettingsUnavailable, capture.LastErrorCode);
        var confirms = 0;

        await capture.ManualCapture();
        Assert.False(await capture.TurnOn(() =>
        {
            confirms++;
            return Task.FromResult(true);
        }));
        await capture.SetEnabled(true);

        Assert.Empty(io.Calls);
        Assert.Equal(0, confirms);
        Assert.False(capture.IsEnabled);
        Assert.Empty(AntigravityAccounts.Load(store));
        Assert.Null(capture.Current.Key);
        Assert.Equal(AntigravityAutoCapture.SettingsUnavailable, capture.LastErrorCode);

        // Control: the same Capture on a store that saves reaches the core.
        var control = new FakeIo();
        await new AntigravityAutoCapture(control.Io, TempStore()).ManualCapture();
        Assert.Contains("capture", control.Calls);
    }

    /// <summary>A store in its own folder, and a way to make every later save
    /// fail: the folder is replaced by a file of the same name, so the next
    /// save cannot create it (review G4).</summary>
    private static (SettingsStore Store, Action BreakSaves) BreakableStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        return (store, () =>
        {
            Directory.Delete(dir, recursive: true);
            File.WriteAllText(dir, "a file where the folder was");
        });
    }

    /// <summary>Review G4/G1: Remove passed its entry check, then the drop's
    /// own save failed. The credential is not deleted and the row goes back
    /// (the two assertions G1-a fails; the settings code is derived from
    /// Persists, so it is not asserted here).</summary>
    [Fact]
    public async Task ARemoveWhoseDropSaveFailsDeletesNothing()
    {
        var (store, breakSaves) = BreakableStore();
        AntigravityAccounts.Save(store, [new(KeyA, "a@example.com"), new(KeyB, "b@example.com")]);
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);
        Assert.True(store.Persists, "fixture: the entry check passes");
        breakSaves();

        await capture.Remove(KeyA);

        Assert.False(store.Persists, "fixture: the drop's save failed");
        Assert.DoesNotContain("remove", io.Calls);
        Assert.Equal([KeyA, KeyB], AntigravityAccounts.Load(store).Select(a => a.Key));
    }

    /// <summary>Review G4/G1: an automatic attempt passed its commit point,
    /// then clearing the stored binding (its first store write) failed. No
    /// capture starts.</summary>
    [Fact]
    public async Task APollWhoseBindingWriteFailsStartsNoCapture()
    {
        var (store, breakSaves) = BreakableStore();
        store.SetBool(AntigravityAutoCapture.EnabledKey, true);
        store.SetString(AntigravityAutoCapture.CurrentKey, JsonSerializer.Serialize(
            new Dictionary<string, string> { ["key"] = KeyA, ["marker"] = "M0" }));
        var io = new FakeIo();
        var capture = new AntigravityAutoCapture(io.Io, store);
        Assert.True(capture.IsEnabled, "fixture: the commit point passes");
        breakSaves();

        await capture.Poll();

        Assert.False(store.Persists, "fixture: the binding write failed");
        Assert.Contains("marker", io.Calls);
        Assert.DoesNotContain("auto", io.Calls);
    }

    [Fact]
    public async Task AFailedRemovePutsTheRowBackWhereItWas()
    {
        var store = TempStore();
        AntigravityAccounts.Save(store, [new(KeyA, "a@example.com"), new(KeyB, "b@example.com")]);
        var io = new FakeIo { RemoveError = new TbCoreException("keychain_delete_failed") };
        var capture = new AntigravityAutoCapture(io.Io, store);

        await capture.Remove(KeyA);

        Assert.Equal([KeyA, KeyB], AntigravityAccounts.Load(store).Select(a => a.Key));
        Assert.Equal("keychain_delete_failed", capture.LastErrorCode);
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

    // ---- 10. the tray tooltip never names a captured account by email -------

    private const string EmailA = "a@example.com";
    private const string EmailB = "b@example.com";

    // The resolvers are process-wide statics, so every test here installs its
    // own (as the app does at launch) and restores the previous ones.
    private static void WithRegistry(Action body)
    {
        var label = AccountLabel.AntigravityLabel;
        var ordinal = AccountLabel.AntigravityOrdinal;
        Localization.Load("en", AppContext.BaseDirectory);
        var store = TempStore();
        AntigravityAccounts.Save(store, [new(KeyA, EmailA), new(KeyB, EmailB)]);
        AccountLabel.AntigravityLabel = key => AntigravityAccounts.Label(store, key);
        AccountLabel.AntigravityOrdinal = key => AntigravityAccounts.Ordinal(store, key);
        try
        {
            body();
        }
        finally
        {
            AccountLabel.AntigravityLabel = label;
            AccountLabel.AntigravityOrdinal = ordinal;
        }
    }

    private static string Tooltip(AgentUsageSnapshot agent, AgentUsagePayload payload) =>
        new QuotaPick(agent, agent.Windows[0]).TooltipLine(payload);

    [Fact]
    public void TheTooltipNamesCapturedAccountsByPositionNeverEmailOrKey()
    {
        WithRegistry(() =>
        {
            var a = Captured(KeyA);
            var b = Captured(KeyB) with { Identity = new AgentIdentity(EmailB) };
            var payload = Payload(Primary(), a, b);
            var (ta, tb) = (Tooltip(a, payload), Tooltip(b, payload));

            Assert.Contains("Antigravity account 1", ta);
            Assert.Contains("Antigravity account 2", tb);
            Assert.NotEqual(ta, tb);
            foreach (var t in new[] { ta, tb })
            {
                Assert.DoesNotContain(EmailA, t);
                Assert.DoesNotContain(EmailB, t);
                Assert.DoesNotContain(KeyA, t);
                Assert.DoesNotContain(KeyB, t);
            }
        });
    }

    [Fact]
    public void ThePrimaryTooltipCarriesNoEmailMergedOrNot()
    {
        WithRegistry(() =>
        {
            // Un-merged: the primary's own identity email.
            var plain = Payload(Primary(), Captured(KeyA));
            var unmerged = plain.Agents[0];
            Assert.Equal("primary@example.com", unmerged.Identity!.Email);
            Assert.DoesNotContain("primary@example.com", Tooltip(unmerged, plain));

            // Merged: Apply copies the captured account's email onto the primary.
            var merged = AntigravityDedup.Apply(plain, KeyA, "M1");
            var primary = merged.Agents[0];
            Assert.Equal(EmailA, primary.Identity!.Email);
            var line = Tooltip(primary, merged);
            Assert.DoesNotContain(EmailA, line);
            Assert.DoesNotContain("primary@example.com", line);
            Assert.DoesNotContain(KeyA, line);
        });
    }

    [Fact]
    public void TheTooltipNamesAnUnlistedCapturedAccountGenerically()
    {
        WithRegistry(() =>
        {
            var gone = Captured("c".PadRight(64, 'c'));
            Assert.Equal("Antigravity account Weekly (7d) 70% left", Tooltip(gone, Payload(gone)));
        });
    }

    [Theory]
    [InlineData("antigravity", null)]
    [InlineData("claude", AccountLabel.ClaudeDesktopKey)]
    [InlineData("claude", @"D:\Work\team-b")]
    public void TheTooltipNamesEveryOtherAccountKindAsTheLabelDoes(string clientId, string? key)
    {
        WithRegistry(() =>
        {
            var agent = Captured(KeyA) with { ClientId = clientId, AccountKey = key };
            var payload = Payload(agent);
            var id = AccountIdentity.Of(clientId, key);
            Assert.Equal(AccountLabel.Of(id, payload), AccountLabel.OfPublic(id, payload));
            Assert.StartsWith(AccountLabel.Of(id, payload), Tooltip(agent, payload));
        });
    }

    [Fact]
    public void MenusAndCardsStillNameACapturedAccountByEmail()
    {
        WithRegistry(() =>
            Assert.Equal($"Antigravity · {EmailA}", AccountLabel.Of(new AccountIdentity("antigravity", KeyA))));
    }
}
