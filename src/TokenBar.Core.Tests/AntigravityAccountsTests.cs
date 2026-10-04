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
            null));
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
            () => { io.Calls.Enqueue("fetch"); return Payload(Primary()); }, null, capture));

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
            Assert.Equal("Antigravity account Weekly 70% left", Tooltip(gone, Payload(gone)));
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
