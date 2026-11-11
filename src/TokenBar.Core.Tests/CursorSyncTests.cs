using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>The status-copy assertions read the process-wide translation table,
/// which other test classes swap while they run; this collection runs alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CursorSyncCollection
{
    public const string Name = "cursor sync (English copy)";
}

/// <summary>
/// The C# half of Cursor desktop sync (Plan B2): preferences → the config the
/// core is given, the notice and switch rules, the test-mode gate, the
/// ordering/generation rules ported from macOS #488, the cleanupFailed line,
/// and the approved copy in all three catalogs. Every native call is a fake:
/// nothing here reads Cursor's database or touches the network.
/// </summary>
[Collection(CursorSyncCollection.Name)]
public class CursorSyncTests
{
    private static readonly string[] User = ["Syrtis.exe"];
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static SettingsStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "settings.json"));

    private static CursorSyncStatus Status(string state, long events = 0, long? last = 1, bool? changed = null) =>
        new(state, events, last, changed ?? state == "ok", []);

    /// <summary>Fake core. Every call is logged in order; the optional gates
    /// hold a push or a sync until the test releases it.</summary>
    private sealed class FakeCore
    {
        public readonly ConcurrentQueue<string> Log = new();
        public readonly ConcurrentQueue<(bool Enabled, bool Takeover)> Pushes = new();
        /// <summary>Every push's exact JSON, in order.</summary>
        public readonly ConcurrentQueue<string> Json = new();
        public Func<bool, bool, Exception?> PushError = (_, _) => null;
        public Func<bool, ManualResetEventSlim?> PushGate = _ => null;
        public ManualResetEventSlim? SyncGate;
        public Action<bool>? OnSync;
        public readonly SemaphoreSlim SyncStarted = new(0);
        public Func<CursorSyncStatus> Result = () => Status("ok", 1);
        public bool Present = true;
        /// <summary>An accepted on push's <c>cleanupFailed</c>.</summary>
        public volatile bool PushCleanupFailed;
        public int PresenceProbes;
        public volatile bool CoreTakeover;

        public int Calls => Log.Count + PresenceProbes;

        public CursorSyncController.Io Io => new(
            SetConfig: json =>
            {
                using var doc = JsonDocument.Parse(json);
                var enabled = doc.RootElement.GetProperty("enabled").GetBoolean();
                var takeover = doc.RootElement.GetProperty("cliTakeoverConfirmed").GetBoolean();
                PushGate(enabled)?.Wait(Wait);
                Json.Enqueue(json);
                Pushes.Enqueue((enabled, takeover));
                CoreTakeover = takeover;
                Log.Enqueue(enabled ? "push:on" : "push:off");
                if (PushError(enabled, takeover) is { } error)
                {
                    throw error;
                }

                return new CursorSyncConfig(
                    enabled, null, takeover, 0, [], [], enabled && PushCleanupFailed, []);
            },
            Sync: userInitiated =>
            {
                Log.Enqueue(userInitiated ? "sync:user" : "sync:auto");
                OnSync?.Invoke(userInitiated);
                SyncStarted.Release();
                SyncGate?.Wait(Wait);
                return Result();
            },
            CursorPresent: () =>
            {
                Interlocked.Increment(ref PresenceProbes);
                return Present;
            });

        public int UserSyncs => Log.Count(e => e == "sync:user");
    }

    // The confirmation for a notice that is already answered: TurnOn must not open it.
    private static Task<bool> NeverAsked() => throw new InvalidOperationException("asked again");

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    // ---- preferences → config ---------------------------------------------------

    /// <summary>The exact JSON the core is given (CMP-W shape). W1 pushes no
    /// added profile (WB11: W2 reads the store behind its disclosure).</summary>
    [Fact]
    public async Task ThePreferencesReachTheCoreAsTheConfigJson()
    {
        var store = TempStore();
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        string Last() => core.Json.Last();

        Assert.True(CursorSync.Enabled(store)); // D3: default ON
        controller.Reconfigure(refresh: false);
        await Until(() => core.Pushes.Count == 1);
        // Default on, but the notice is unanswered: the core is told off.
        Assert.Equal("""{"enabled":false,"cliTakeoverConfirmed":false,"profiles":[]}""", Last());

        controller.AnswerNotice(continuing: true);
        await Until(() => core.Pushes.Count == 2);
        Assert.Equal("""{"enabled":true,"cliTakeoverConfirmed":false,"profiles":[]}""", Last());

        controller.SetTakeoverConfirmed(true);
        await Until(() => core.Pushes.Count == 3);
        Assert.Equal("""{"enabled":true,"cliTakeoverConfirmed":true,"profiles":[]}""", Last());

        controller.TurnOff();
        await Until(() => core.Pushes.Count == 4);
        Assert.Equal("""{"enabled":false,"cliTakeoverConfirmed":true,"profiles":[]}""", Last());
    }

    [Fact]
    public void TurnOffOnTheNoticeAnswersItAndStoresOff()
    {
        var store = TempStore();
        var controller = new CursorSyncController(new FakeCore().Io, store, User, interval: Never);
        controller.AnswerNotice(continuing: false);
        Assert.True(CursorSync.NoticeAcknowledged(store));
        Assert.False(CursorSync.Enabled(store));
        Assert.False(CursorSync.ShouldSync(store));
    }

    // The Settings switch's real sequence (TurnOn with the confirmation's
    // answer): Continue enables with one config push and one sync.
    [Fact]
    public async Task TurningTheSwitchOnAndContinuingPushesOnceAndSyncsOnce()
    {
        var store = TempStore();
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var asked = 0;
        var turnedOn = await controller.TurnOn(() =>
        {
            // While the confirmation is open nothing is written or sent, so a
            // state change then still reads off (the switch must not follow it).
            asked++;
            Assert.False(CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store)));
            Assert.Null(store.GetNullableBool(CursorSync.NoticeKey));
            Assert.Equal(0, core.Calls);
            return Task.FromResult(true);
        });
        Assert.True(turnedOn);
        Assert.Equal(1, asked);
        await Until(() => core.Log.Any(e => e.StartsWith("sync", StringComparison.Ordinal)));
        await Task.Delay(200);
        Assert.True(CursorSync.NoticeAcknowledged(store));
        Assert.True(CursorSync.Enabled(store));
        Assert.Equal(new[] { (true, false) }, core.Pushes.ToArray());
        Assert.Equal(1, core.Log.Count(e => e.StartsWith("sync", StringComparison.Ordinal)));

        // Once answered, Windows turns on without asking (macOS asks every
        // time; a decided difference, see TurnOn).
        controller.TurnOff();
        Assert.True(await controller.TurnOn(NeverAsked));
        Assert.True(CursorSync.ShouldSync(store));
    }

    [Fact]
    public async Task CancellingTheConfirmationWritesNothingAndCallsNoCore()
    {
        var store = TempStore();
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var changes = 0;
        controller.StateChanged += () => changes++;
        Assert.False(await controller.TurnOn(() => Task.FromResult(false)));
        await Task.Delay(200);
        Assert.Null(store.GetNullableBool(CursorSync.EnabledKey));
        Assert.Null(store.GetNullableBool(CursorSync.NoticeKey));
        Assert.Equal(0, core.Calls);
        Assert.Equal(0, changes);
    }

    // Consent lives in the controller: only TurnOn (its confirmation) and the
    // Overview card's AnswerNotice may turn sync on. Every other public
    // controller call (instance or static), with every bool argument, leaves
    // it off, both before the notice is answered (default-on preference) and
    // after it was answered and turned off: in the store, and in what reaches
    // the core (no enabled config push, no sync).
    [Fact]
    public async Task NoPublicCallButTheConsentPathsTurnsSyncOn()
    {
        string[] consent = [nameof(CursorSyncController.TurnOn), nameof(CursorSyncController.AnswerNotice)];
        var calls = typeof(CursorSyncController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !consent.Contains(m.Name))
            .ToArray();
        // Control: the scan sees the controller's actions.
        Assert.Contains(calls, m => m.Name == nameof(CursorSyncController.TurnOff));
        Assert.Contains(calls, m => m.Name == nameof(CursorSyncController.SetTakeoverConfirmed));
        foreach (var answered in new[] { false, true })
        {
            foreach (var call in calls)
            {
                var parameters = call.GetParameters();
                // A new public call with other parameters must be classified here.
                Assert.True(parameters.All(p => p.ParameterType == typeof(bool)), call.Name);
                foreach (var flag in new[] { false, true })
                {
                    var store = TempStore();
                    if (answered)
                    {
                        store.SetBool(CursorSync.NoticeKey, true);
                        store.SetBool(CursorSync.EnabledKey, false);
                    }

                    var core = new FakeCore();
                    var controller = new CursorSyncController(core.Io, store, User, interval: Never);
                    var args = parameters.Select(_ => (object)flag).ToArray();
                    if (call.Invoke(call.IsStatic ? null : controller, args) is Task task)
                    {
                        await task;
                    }

                    // Pushes and syncs run on the thread pool; let them land.
                    await Task.Delay(50);
                    var what = $"{call.Name}({flag}) answered={answered}";
                    Assert.False(CursorSync.ShouldSync(store), what);
                    Assert.Equal(answered, CursorSync.NoticeAcknowledged(store));
                    Assert.False(core.Pushes.LastOrDefault().Enabled, what);
                    Assert.DoesNotContain(core.Log, e => e.StartsWith("sync", StringComparison.Ordinal));
                }
            }
        }

        // Controls: the consent paths do turn it on, and the core sees it.
        var answeredStore = TempStore();
        new CursorSyncController(new FakeCore().Io, answeredStore, User, interval: Never)
            .AnswerNotice(continuing: true);
        Assert.True(CursorSync.ShouldSync(answeredStore));

        var confirmedStore = TempStore();
        var confirmedCore = new FakeCore();
        Assert.True(await new CursorSyncController(confirmedCore.Io, confirmedStore, User, interval: Never)
            .TurnOn(() => Task.FromResult(true)));
        Assert.True(CursorSync.ShouldSync(confirmedStore));
        await Until(() => confirmedCore.Log.Any(e => e.StartsWith("sync", StringComparison.Ordinal)));
        Assert.True(confirmedCore.Pushes.Last().Enabled);
    }

    // Turn Off on the notice card: no reader may see "answered" while the
    // preference is still the default on.
    [Fact]
    public void TurnOffOnTheNoticeNeverPassesThroughSyncAllowed()
    {
        var store = TempStore();
        var seen = new List<(string Key, bool ShouldSync)>();
        store.Changed += key => seen.Add((key, CursorSync.ShouldSync(store)));
        Assert.True(CursorSync.Enabled(store)); // default on
        new CursorSyncController(new FakeCore().Io, store, User, interval: Never)
            .AnswerNotice(continuing: false);
        // Control: both writes were observed.
        Assert.Equal(new[] { CursorSync.EnabledKey, CursorSync.NoticeKey }.Order(), seen.Select(s => s.Key).Order());
        Assert.DoesNotContain(seen, s => s.ShouldSync);
    }

    // ---- notice and switch rules ------------------------------------------------

    [Fact]
    public async Task NoSyncRunsBeforeTheNoticeIsAnswered()
    {
        var store = TempStore();
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        await controller.RunSync(userInitiated: true);
        controller.Reconfigure(refresh: false);
        await Until(() => core.Pushes.Count == 1);
        await Task.Delay(100);
        Assert.Equal(0, core.Log.Count(e => e.StartsWith("sync", StringComparison.Ordinal)));

        // Control: answered, the same calls sync.
        store.SetBool(CursorSync.NoticeKey, true);
        await controller.RunSync(userInitiated: true);
        Assert.Equal(1, core.UserSyncs);
    }

    [Fact]
    public void TheNoticeShowsOnlyWithCursorPresentAndNeverAgainOnceAnswered()
    {
        var store = TempStore();
        Assert.False(CursorSync.NoticeVisible(store, User, () => false)); // Cursor absent → no card
        Assert.True(CursorSync.NoticeVisible(store, User, () => true));

        var controller = new CursorSyncController(new FakeCore().Io, store, User, interval: Never);
        controller.AnswerNotice(continuing: true);
        Assert.False(CursorSync.NoticeVisible(store, User, () => true));

        var declined = TempStore();
        new CursorSyncController(new FakeCore().Io, declined, User, interval: Never).AnswerNotice(continuing: false);
        Assert.False(CursorSync.NoticeVisible(declined, User, () => true));

        // Turned off in Settings before answering: no card either.
        var off = TempStore();
        off.SetBool(CursorSync.EnabledKey, false);
        Assert.False(CursorSync.NoticeVisible(off, User, () => true));
    }

    [Fact]
    public void TheNoticeNeverShowsOrProbesInATestRun()
    {
        var store = TempStore();
        var probes = 0;
        foreach (var flag in DiscordPresence.TestArguments)
        {
            Assert.False(CursorSync.NoticeVisible(store, ["Syrtis.exe", flag], () => { probes++; return true; }));
        }

        Assert.Equal(0, probes);
    }

    [Fact]
    public void TheSwitchReadsOnOnlyWhenEnabledAndAnswered()
    {
        Assert.False(CursorSync.ToggleShowsOn(enabled: true, acknowledged: false));
        Assert.True(CursorSync.ToggleShowsOn(enabled: true, acknowledged: true));
        Assert.False(CursorSync.ToggleShowsOn(enabled: false, acknowledged: true));
        Assert.False(CursorSync.ToggleShowsOn(enabled: false, acknowledged: false));
    }

    // ---- test-mode gate -----------------------------------------------------------

    [Fact]
    public async Task ATestRunNeverCallsTheCoreWhateverTheSettingsSay()
    {
        foreach (var flag in DiscordPresence.TestArguments)
        {
            var store = TempStore();
            store.SetBool(CursorSync.NoticeKey, true);
            var core = new FakeCore();
            var controller = new CursorSyncController(core.Io, store, ["Syrtis.exe", flag], interval: Never);
            controller.Reconfigure(refresh: true);
            Assert.True(await controller.TurnOn(NeverAsked));
            controller.SetTakeoverConfirmed(true);
            controller.AnswerNotice(continuing: true);
            await controller.RunSync(userInitiated: true);
            await controller.ProbeCursorPresent();
            controller.TurnOff(); // off would delete files
            await Task.Delay(100);
            Assert.True(core.Calls == 0, $"{flag}: {string.Join(",", core.Log)}");
        }

        // Control: the same calls in a user session reach the core.
        var userStore = TempStore();
        userStore.SetBool(CursorSync.NoticeKey, true);
        var userCore = new FakeCore();
        var user = new CursorSyncController(userCore.Io, userStore, User, interval: Never);
        user.Reconfigure(refresh: false);
        await user.RunSync(userInitiated: true);
        await user.ProbeCursorPresent();
        // push + a sync (the loop's or this one, single-flight) + the probe.
        await Until(() => userCore.Calls >= 3);
        Assert.True(user.CursorPresent);
    }

    [Fact]
    public void TheTestArgumentsIncludeTheStartupProbeAndTheIconDump()
    {
        Assert.Contains("--startup-smoke", DiscordPresence.TestArguments);
        Assert.Contains("--dump-tray-icons", DiscordPresence.TestArguments);
        Assert.True(CursorSync.IsUserRuntime(["Syrtis.exe", "--settings", "--open-flyout"]));
    }

    // ---- ordering and generation (macOS #488) -------------------------------------

    [Fact]
    public async Task RapidOnThenOffReachesTheCoreInOrder()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var slowOn = new ManualResetEventSlim(false);
        core.PushGate = enabled => enabled ? slowOn : null;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);
        controller.TurnOff();
        await Task.Delay(100);
        slowOn.Set();
        await Until(() => core.Pushes.Count == 2);
        Assert.Equal([true, false], core.Pushes.Select(p => p.Enabled));
    }

    [Fact]
    public async Task SyncNowWaitsForTheNewestConfigPush()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var slowPush = new ManualResetEventSlim(false);
        core.PushGate = _ => slowPush;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);
        var syncNow = controller.RunSync(userInitiated: true);
        await Task.Delay(150);
        slowPush.Set();
        await syncNow;
        Assert.Equal("push:on", core.Log.First());
    }

    [Fact]
    public async Task AResultReturningAfterSyncWasTurnedOffIsDiscarded()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        core.Result = () => Status("ok", 3);
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var data = 0;
        controller.DataChanged += () => Interlocked.Increment(ref data);
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        store.SetBool(CursorSync.EnabledKey, false);
        controller.Reconfigure(refresh: false);
        gate.Set();
        await running;
        Assert.Null(controller.State);
        Assert.Null(controller.LastSuccessMs);
        Assert.Equal(0, data);
        Assert.Equal(1, core.UserSyncs); // and no rerun while off
    }

    [Fact]
    public async Task AConfigChangeDuringASyncDiscardsItsResultAndReruns()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        core.Result = () => Status("ok", 2);
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        controller.SetTakeoverConfirmed(true);
        await Until(() => core.Pushes.Count == 1);
        gate.Set();
        await running;
        // The background loop's own syncs are "auto"; only the rerun can
        // make this two.
        Assert.Equal(2, core.UserSyncs);
        Assert.Equal("ok", controller.State);
    }

    // A background sync gets a short budget; a press during it must not be lost.
    [Fact]
    public async Task APressDuringABackgroundSyncRunsOneUserSyncAfterIt()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        CursorSyncController controller = null!;
        // Syncing as seen when the user pass starts: it must not drop between
        // the two passes.
        bool? syncingAtUserPass = null;
        var userStarted = false;
        var seen = new ConcurrentQueue<bool>();
        core.OnSync = user =>
        {
            if (user)
            {
                userStarted = true;
                syncingAtUserPass = controller.Syncing;
            }
        };
        controller = new CursorSyncController(core.Io, store, User, interval: Never);
        // Every Syncing reading from the auto pass until the user pass starts.
        controller.StateChanged += () =>
        {
            if (!userStarted)
            {
                seen.Enqueue(controller.Syncing);
            }
        };
        var running = controller.RunSync(userInitiated: false);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true);
        await controller.RunSync(userInitiated: true);
        gate.Set();
        await running;
        Assert.Equal(new[] { "sync:auto", "sync:user" }, core.Log.ToArray());
        Assert.True(syncingAtUserPass);
        Assert.NotEmpty(seen);
        Assert.DoesNotContain(false, seen);
        Assert.False(controller.Syncing);
    }

    // Held Enter or Space: presses while a user sync runs start nothing more.
    [Fact]
    public async Task PressesDuringAUserSyncStartNoMoreSyncs()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true);
        await controller.RunSync(userInitiated: true);
        await controller.RunSync(userInitiated: true);
        gate.Set();
        await running;
        Assert.Equal(new[] { "sync:user" }, core.Log.ToArray());
    }

    [Fact]
    public async Task ARememberedPressIsDroppedWhenSyncIsTurnedOffBeforeThePassEnds()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var running = controller.RunSync(userInitiated: false);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true);
        controller.TurnOff();
        gate.Set();
        await running;
        Assert.Equal(new[] { "sync:auto" }, core.Log.Where(e => e.StartsWith("sync")).ToArray());
        Assert.False(controller.Syncing);
    }

    // The off clears the remembered press; turning sync back on before the pass
    // ends (so it reruns under the new config) must not bring the press back.
    [Fact]
    public async Task ARememberedPressDoesNotSurviveAnOffOnCycle()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var running = controller.RunSync(userInitiated: false);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true);
        controller.TurnOff();
        Assert.True(await controller.TurnOn(NeverAsked));
        gate.Set();
        await running;
        Assert.Equal(0, core.UserSyncs);
        // The rerun after the cycle is the second auto pass. At least that: sync turned
        // back on also starts the schedule loop, whose own auto sync may or
        // may not get in before the rerun finishes, so the count is not exact.
        Assert.True(core.Log.Count(e => e == "sync:auto") >= 2);
    }

    [Fact]
    public async Task AConfigChangeWhileASyncWaitsForAPushWaitsForTheNewestOne()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var first = new ManualResetEventSlim(false);
        using var second = new ManualResetEventSlim(false);
        var pushes = 0;
        core.PushGate = _ => Interlocked.Increment(ref pushes) == 1 ? first : second;
        // The status records which config the sync ran under.
        core.Result = () => Status("ok", 1, core.CoreTakeover ? 2 : 1);
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);
        var running = controller.RunSync(userInitiated: true);
        await Task.Delay(100);
        controller.SetTakeoverConfirmed(true);
        await Task.Delay(50);
        first.Set();
        await Task.Delay(150);
        second.Set();
        await running;
        // No walk under the superseded config: the pass waited for the
        // newest push and synced once, under it.
        Assert.Equal(1, core.UserSyncs);
        Assert.Equal(2, controller.LastSuccessMs);
    }

    // Consent: Sync Now pressed while the "on" push is still pending, then the
    // switch turned off before that push lands. Nothing may be sent.
    [Fact]
    public async Task SyncNowWaitingOnAnOnPushDoesNotSyncAfterTurningOff()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        using var slowOn = new ManualResetEventSlim(false);
        core.PushGate = enabled => enabled ? slowOn : null;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);                // on push, held
        var syncNow = controller.RunSync(userInitiated: true); // waits on that push
        controller.TurnOff();                          // the user turns it off
        slowOn.Set();
        await syncNow;
        await Until(() => core.Pushes.Count == 2);
        Assert.Equal(0, core.UserSyncs);
        Assert.DoesNotContain("sync:auto", core.Log);
    }

    // A reconfigure whose push lands just as a sync ends (here: from the
    // DataChanged that sync raises) starts a loop whose first sync must not be
    // refused as "already running": that pass has decided it is the last, so
    // no rerun is owed and the new config would wait a whole interval.
    [Fact]
    public async Task ASyncStartingAsTheLastPassEndsIsNotRefused()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore { Result = () => Status("ok", 5) };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var reconfigured = 0;
        controller.DataChanged += () =>
        {
            if (Interlocked.Exchange(ref reconfigured, 1) != 0)
            {
                return;
            }

            // The new loop's sync runs while this pass is still unwinding.
            store.SetBool(CursorSync.TakeoverKey, true);
            controller.Reconfigure(refresh: false);
            SpinWait.SpinUntil(() => core.Log.Contains("sync:auto"), TimeSpan.FromMilliseconds(500));
        };
        await controller.RunSync(userInitiated: true);
        await Until(() => core.Log.Contains("sync:auto"));
    }

    /// <summary>The dashboard refreshes on <c>changed</c> (some profile's
    /// file content changed), not on the headline or the event count: a round
    /// whose headline is an error can still have written a file, another
    /// profile can write the same count, and an ok walk whose bytes equal the
    /// file's changes nothing.</summary>
    [Fact]
    public async Task EveryRoundThatWroteAFileRefreshesAndNoOther()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var next = Status("ok", 7);
        var core = new FakeCore { Result = () => next };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var data = 0;
        controller.DataChanged += () => Interlocked.Increment(ref data);

        await controller.RunSync(userInitiated: true);
        Assert.Equal(1, data);
        await controller.RunSync(userInitiated: true);
        Assert.Equal(2, data); // the same count, new content: refreshes

        next = Status("error", 7, changed: true); // one profile failed, another wrote
        await controller.RunSync(userInitiated: true);
        Assert.Equal(3, data);

        // e.g. every profile a duplicate, or a walk whose bytes equal the
        // file's (core: cursor_sync an_identical_walk_changes_nothing)
        next = Status("ok", 7, changed: false);
        await controller.RunSync(userInitiated: true);
        Assert.Equal(3, data);
        next = Status("partial", 0, changed: false);
        await controller.RunSync(userInitiated: true);
        Assert.Equal(3, data);
    }

    /// <summary>CMP-W W1 review round 3 (C): a round whose status lists
    /// <c>cleanupFailedProfiles</c>, or an accepted on push whose cleanup
    /// failed, raises <see cref="CursorSyncController.ProfileCleanupFailed"/>;
    /// a clean round or push clears its own part, and an off push clears
    /// both.</summary>
    [Fact]
    public async Task ProfileCleanupFailuresAreRaisedWhileOn()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var next = Status("ok", 1) with { CleanupFailedProfiles = ["0A1B2C3D-0000-4000-8000-00000000000A"] };
        var core = new FakeCore { Result = () => next };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);

        await controller.RunSync(userInitiated: true);
        Assert.True(controller.ProfileCleanupFailed, "the round's cleanupFailedProfiles was dropped");
        next = Status("ok", 1) with { CleanupFailedProfiles = [] };
        await controller.RunSync(userInitiated: true);
        Assert.False(controller.ProfileCleanupFailed);

        core.PushCleanupFailed = true;
        controller.Reconfigure(refresh: false);
        await Until(() => controller.ProfileCleanupFailed);
        core.PushCleanupFailed = false;
        controller.Reconfigure(refresh: false);
        await Until(() => !controller.ProfileCleanupFailed);

        core.PushCleanupFailed = true;
        controller.Reconfigure(refresh: false);
        await Until(() => controller.ProfileCleanupFailed);
        controller.TurnOff();
        await Until(() => !controller.ProfileCleanupFailed);
    }

    // ---- configRejected (CMP-W WB3) -----------------------------------------------

    /// <summary>A refused on push stops sync: the fake core's Sync count stays
    /// 0 for the schedule and for a user's Sync Now, until a push is accepted.
    /// The core kept only profiles that push listed; anything else could walk
    /// a profile the user removed.</summary>
    [Fact]
    public async Task ARejectedOnPushStopsEverySyncUntilAPushIsAccepted()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        var refuse = true;
        core.PushError = (enabled, _) => enabled && refuse ? new TbCoreException("invalidProfileId") : null;
        var interval = TimeSpan.FromMilliseconds(20);
        var controller = new CursorSyncController(core.Io, store, User, interval: interval);
        int Syncs() => core.Log.Count(e => e.StartsWith("sync", StringComparison.Ordinal));

        controller.Reconfigure(refresh: false); // the schedule's push, refused
        await Until(() => controller.State == CursorSync.ConfigRejectedState);
        await controller.RunSync(userInitiated: true); // Sync Now
        await controller.RunSync(userInitiated: false);
        await Task.Delay(200); // several intervals of the schedule
        Assert.Equal(0, Syncs());
        Assert.Equal(CursorSync.Copy.ConfigRejected, CursorSync.StatusLine(controller.State, null));
        Assert.True(CursorSync.StatusIsWarning(controller.State));

        // An accepted push clears it and sync runs again.
        refuse = false;
        controller.Reconfigure(refresh: false);
        await Until(() => Syncs() > 0);
        await Until(() => controller.State == "ok");
    }

    /// <summary>W1 and the Accounts page's consent flow (#267) together, over
    /// both consent paths: the on push made by
    /// <see cref="CursorSyncController.TurnOn"/>'s Continue, or by the
    /// Overview card's <see cref="CursorSyncController.AnswerNotice"/>
    /// Continue, is refused, so consent is recorded but nothing syncs,
    /// scheduled or pressed; turning off and on again (no question now: the
    /// notice is answered) with an accepted push syncs.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARejectedPushAfterTurnOnStopsSyncUntilAnAcceptedPush(bool viaTurnOn)
    {
        var store = TempStore(); // notice unanswered, preference default on
        var core = new FakeCore();
        var refuse = true;
        core.PushError = (enabled, _) => enabled && refuse ? new TbCoreException("invalidProfileId") : null;
        var controller = new CursorSyncController(core.Io, store, User, interval: TimeSpan.FromMilliseconds(20));
        int Syncs() => core.Log.Count(e => e.StartsWith("sync", StringComparison.Ordinal));

        if (viaTurnOn)
        {
            Assert.True(await controller.TurnOn(() => Task.FromResult(true)));
        }
        else
        {
            controller.AnswerNotice(continuing: true);
        }

        Assert.True(CursorSync.NoticeAcknowledged(store));
        Assert.True(CursorSync.ShouldSync(store));
        await Until(() => controller.State == CursorSync.ConfigRejectedState);
        await controller.RunSync(userInitiated: true); // Sync Now
        await Task.Delay(150); // several intervals of the schedule
        Assert.Equal(0, Syncs());

        refuse = false;
        controller.TurnOff();
        Assert.True(await controller.TurnOn(NeverAsked));
        await Until(() => Syncs() > 0);
        await Until(() => controller.State == "ok");
    }

    /// <summary>Merge-delta 1: an on push refused only after a later
    /// Reconfigure turned sync off belongs to a superseded configuration, so
    /// it must not leave configRejected behind: not while off, and not as a
    /// stale warning when sync is turned on again (seen before that push
    /// lands).</summary>
    [Fact]
    public async Task ARefusalThatLandsAfterTurningOffLeavesNoStaleWarning()
    {
        var store = TempStore(); // notice unanswered
        var core = new FakeCore();
        var refuse = true;
        core.PushError = (enabled, _) => enabled && refuse ? new TbCoreException("invalidProfileId") : null;
        var held = new ManualResetEventSlim(false);
        ManualResetEventSlim? gate = held;
        core.PushGate = enabled => enabled ? gate : null;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);

        Assert.True(await controller.TurnOn(() => Task.FromResult(true))); // on push, held
        controller.TurnOff();
        held.Set(); // the on push is refused now, after the off
        await Until(() => core.Pushes.Count == 2);
        await Task.Delay(50);
        Assert.NotEqual(CursorSync.ConfigRejectedState, controller.State);

        // Turning on again: before its push lands, no stale warning.
        refuse = false;
        using var next = new ManualResetEventSlim(false);
        gate = next;
        Assert.True(await controller.TurnOn(NeverAsked));
        Assert.NotEqual(CursorSync.ConfigRejectedState, controller.State);
        next.Set();
        await Until(() => core.Pushes.Count == 3);
        held.Dispose();
    }

    /// <summary>Turning sync off after a refusal clears the line (sync is off),
    /// and an off push is never treated as a refusal.</summary>
    [Fact]
    public async Task AnOffPushNeverReadsAsRejected()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore { PushError = (_, _) => new TbCoreException("tb_set_cursor_sync panicked: x") };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);
        await Until(() => controller.State == CursorSync.ConfigRejectedState);
        controller.TurnOff();
        await Until(() => core.Pushes.Count == 2);
        await Task.Delay(50);
        Assert.Null(controller.State);
    }

    /// <summary>WB9, C# side: what the controller logs is a type name or a
    /// fixed code, never a native message (a panic payload could carry
    /// anything).</summary>
    [Fact]
    public async Task TheLogCarriesNoNativeMessage()
    {
        const string Canary = "TOKENCANARY-eyJ.SIGCANARY.user_CANARY01234567890";
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var lines = new ConcurrentQueue<string>();
        var core = new FakeCore
        {
            PushError = (_, _) => new TbCoreException($"tb_set_cursor_sync panicked: {Canary}"),
            Result = () => throw new TbCoreException($"tb_cursor_sync panicked: {Canary}"),
            Present = true,
        };
        var controller = new CursorSyncController(core.Io, store, User, lines.Enqueue, Never);
        controller.Reconfigure(refresh: false);
        await Until(() => controller.State == CursorSync.ConfigRejectedState);
        core.PushError = (_, _) => null;
        controller.Reconfigure(refresh: false);
        await Until(() => core.Pushes.Count == 2);
        await controller.RunSync(userInitiated: true);
        controller.TurnOff();
        await Until(() => core.Pushes.Count == 3);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.DoesNotContain("CANARY", line, StringComparison.Ordinal));
    }

    // ---- added profiles: store and consent (W1 plumbing, no UI) -------------------

    /// <summary>WB5 + WB11: W1 never pushes an added profile, whatever the
    /// local store or the roaming settings hold (another PC's folders could
    /// arrive through the roaming file).</summary>
    [Fact]
    public async Task NoAddedProfileIsPushedInW1()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        store.SetString("tokenbar.cursorSync.profiles", """[{"id":"0A1B2C3D-0000-4000-8000-00000000000A","root":"D:\other-pc"}]""");
        var local = new CursorProfileStore(Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "cursor-profiles.json"));
        local.Save([new CursorProfile(CursorProfileStore.NewId(), @"C:\CursorProfileTest")]);
        Assert.Single(local.Load()); // control: the store does hold one
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        controller.Reconfigure(refresh: false);
        await Until(() => core.Json.Count == 1);
        using var doc = JsonDocument.Parse(core.Json.Single());
        Assert.Equal(0, doc.RootElement.GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public void TheProfileStoreIsLocalAndKeepsOnlyUppercaseGuidIds()
    {
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CursorProfileStore.DefaultPath, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + Path.DirectorySeparatorChar,
            CursorProfileStore.DefaultPath, StringComparison.Ordinal);
        var id = CursorProfileStore.NewId();
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", id);

        var path = Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "cursor-profiles.json");
        var store = new CursorProfileStore(path);
        Assert.Empty(store.Load()); // absent file
        store.Save([new CursorProfile(id, @"C:"), new CursorProfile(id.ToLowerInvariant(), @"C:"), new CursorProfile("default", @"C:\c")]);
        Assert.Equal([new CursorProfile(id, @"C:")], store.Load());
        File.WriteAllText(path, "{not json");
        Assert.Empty(store.Load());
        // Null entries are dropped, not dereferenced (`[null]` is the empty list).
        File.WriteAllText(path, "[null]");
        Assert.Empty(store.Load());
        File.WriteAllText(path, $$"""[null,{"Id":"{{id}}","Root":"C:/d"}]""");
        Assert.Equal([new CursorProfile(id, "C:/d")], store.Load());
    }

    [Fact]
    public void ConsentParagraphsFollowTheList()
    {
        Assert.Equal([CursorSync.Copy.Privacy], CursorSync.ConsentParagraphs(0));
        Assert.Equal([CursorSync.Copy.Privacy, CursorSync.Copy.ProfilesPrivacy], CursorSync.ConsentParagraphs(1));
        Assert.Equal(2, CursorSync.ConsentParagraphs(32).Count);
    }

    // ---- cleanupFailed ----------------------------------------------------------

    [Fact]
    public async Task CleanupFailedShowsWhileOffUntilALaterCleanupSucceeds()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        var failCleanup = true;
        core.PushError = (enabled, _) =>
            !enabled && failCleanup ? new TbCoreException(CursorSync.CleanupFailedCode) : null;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        bool Shown() => CursorSync.CleanupFailedVisible(
            CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store)),
            controller.CleanupFailed);

        controller.TurnOff();
        await Until(() => core.Pushes.Count == 1);
        await Until(() => controller.CleanupFailed);
        Assert.True(Shown()); // sync off, line shown

        // Turning sync on hides the line ("sync is off" would be false) but
        // does not clear it: only a successful cleanup does.
        failCleanup = false;
        Assert.True(await controller.TurnOn(NeverAsked));
        await Until(() => core.Pushes.Count == 2);
        await Task.Delay(50);
        Assert.True(controller.CleanupFailed);
        Assert.False(Shown());

        controller.TurnOff();
        await Until(() => core.Pushes.Count == 3);
        await Until(() => !controller.CleanupFailed);
        Assert.False(Shown()); // gone after a successful cleanup
    }

    // "Restart Syrtis to retry": a new process over a store where sync was
    // turned off pushes off at launch, which is the cleanup retry, and the
    // line follows that launch push's outcome.
    [Fact]
    public async Task ALaunchWithSyncOffRetriesTheCleanupAndShowsItsOutcome()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        store.SetBool(CursorSync.EnabledKey, false);
        bool Shown(CursorSyncController c) => CursorSync.CleanupFailedVisible(
            CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store)),
            c.CleanupFailed);

        var failing = new FakeCore
        {
            PushError = (enabled, _) => enabled ? null : new TbCoreException(CursorSync.CleanupFailedCode),
        };
        var first = new CursorSyncController(failing.Io, store, User, interval: Never);
        first.Reconfigure(refresh: false); // what App.StartCursorSync does
        await Until(() => first.CleanupFailed);
        Assert.Equal([false], failing.Pushes.Select(p => p.Enabled));
        Assert.True(Shown(first));

        // Next launch: a new process starts with the flag clear, so only its
        // own launch push can bring the line back. A retry that fails again
        // must show it again. (A successful retry hiding it is covered in
        // TheCleanupFailedLineIsHiddenWhileANewOffPushRuns; across processes
        // "hidden" holds trivially, so it is not asserted here.)
        var stillFailing = new FakeCore
        {
            PushError = (enabled, _) => enabled ? null : new TbCoreException(CursorSync.CleanupFailedCode),
        };
        var second = new CursorSyncController(stillFailing.Io, store, User, interval: Never);
        Assert.False(second.CleanupFailed); // control: nothing carried over in memory
        second.Reconfigure(refresh: false);
        await Until(() => second.CleanupFailed);
        Assert.Equal([false], stillFailing.Pushes.Select(p => p.Enabled));
        Assert.True(Shown(second));
    }

    // A retry never shows the previous failure: while a new off push is in
    // flight the line is hidden, and that push alone decides it.
    [Fact]
    public async Task TheCleanupFailedLineIsHiddenWhileANewOffPushRuns()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        var fail = true;
        ManualResetEventSlim? hold = null;
        core.PushError = (enabled, _) =>
            !enabled && fail ? new TbCoreException(CursorSync.CleanupFailedCode) : null;
        core.PushGate = enabled => enabled ? null : hold;
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        bool Shown() => CursorSync.CleanupFailedVisible(
            CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store)),
            controller.CleanupFailed);

        controller.TurnOff();
        await Until(() => Shown()); // the previous failure

        using (var failAgain = new ManualResetEventSlim(false))
        {
            hold = failAgain;
            controller.TurnOff(); // retry, held
            await Task.Delay(100);
            Assert.False(Shown());
            failAgain.Set();
            await Until(() => core.Pushes.Count == 2);
            await Until(() => Shown()); // it failed again: shown
        }

        using (var succeed = new ManualResetEventSlim(false))
        {
            hold = succeed;
            fail = false;
            controller.TurnOff();
            await Task.Delay(100);
            Assert.False(Shown());
            succeed.Set();
            await Until(() => core.Pushes.Count == 3);
            await Task.Delay(50);
            Assert.False(Shown()); // it succeeded: hidden
        }
    }

    [Fact]
    public void TheCleanupFailedLineIsOnlyForTheOffSwitch()
    {
        Assert.True(CursorSync.CleanupFailedVisible(toggleShowsOn: false, cleanupFailed: true));
        Assert.False(CursorSync.CleanupFailedVisible(toggleShowsOn: true, cleanupFailed: true));
        Assert.False(CursorSync.CleanupFailedVisible(toggleShowsOn: false, cleanupFailed: false));
    }

    // ---- status → copy ------------------------------------------------------------

    [Theory]
    [InlineData("partial", CursorSync.Copy.Partial)]
    [InlineData("expired", CursorSync.Copy.Expired)]
    [InlineData("notSignedIn", CursorSync.Copy.NotSignedIn)]
    [InlineData("offline", CursorSync.Copy.Offline)]
    [InlineData("error", CursorSync.Copy.Error)]
    [InlineData("cliPresent", CursorSync.Copy.CliPresent)]
    public void EachProblemStateMapsToItsApprovedLineInOrange(string state, string copy)
    {
        Assert.Equal(copy, CursorSync.StatusLine(state, null));
        Assert.True(CursorSync.StatusIsWarning(state));
    }

    [Fact]
    public void OkShowsTheRelativeTimeAndNothingElseShowsALine()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var hourAgo = (now.ToUnixTimeSeconds() - 3600) * 1000;
        Assert.Equal("Last synced 1h ago", CursorSync.StatusLine("ok", hourAgo, now));
        Assert.False(CursorSync.StatusIsWarning("ok"));
        Assert.Null(CursorSync.StatusLine("disabled", hourAgo, now));
        Assert.Null(CursorSync.StatusLine(null, null, now));
        Assert.Null(CursorSync.StatusLine("ok", null, now));
        Assert.Null(CursorSync.StatusLine("somethingNew", hourAgo, now));
        string[] problems = ["partial", "expired", "notSignedIn", "offline", "error", "cliPresent", CursorSync.ConfigRejectedState];
        Assert.Equal(7, problems.Select(s => CursorSync.StatusLine(s, null)).Distinct().Count());
    }

    [Fact]
    public async Task ANativeFailureReadsAsError()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore { Result = () => throw new TbCoreException("tb_cursor_sync panicked") };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        await controller.RunSync(userInitiated: true);
        Assert.Equal("error", controller.State);
    }

    /// <summary>#547 (macOS <c>statusLine</c>): an error line names the core's
    /// reason code when it has the core's shape, <c>^[a-z_]{1,40}$</c>; any
    /// other reason falls back to the generic line, so no free text reaches
    /// it. The partial line follows the same rule (#520); other lines carry no reason.</summary>
    [Fact]
    public void AnErrorLineShowsTheReasonCodeOnlyWhenItHasTheCoreShape()
    {
        Assert.Equal(
            "Cursor usage couldn't be synced (token_unreadable). Syrtis will try again later.",
            CursorSync.StatusLine("error", null, reason: "token_unreadable"));
        var forty = new string('a', 40);
        Assert.Equal(
            $"Cursor usage couldn't be synced ({forty}). Syrtis will try again later.",
            CursorSync.StatusLine("error", null, reason: forty));
        string?[] invalid =
        [
            null, "", "Token_unreadable", "token unreadable", "token-unreadable",
            "token_unreadable\n", "tökén", new string('a', 41),
        ];
        foreach (var reason in invalid)
        {
            Assert.Equal(CursorSync.Copy.Error, CursorSync.StatusLine("error", null, reason: reason));
            Assert.Equal(CursorSync.Copy.Partial, CursorSync.StatusLine("partial", null, reason: reason));
        }

        Assert.Equal(CursorSync.Copy.Offline, CursorSync.StatusLine("offline", null, reason: "token_unreadable"));
    }

    /// <summary>#520 (macOS <c>partialWithReason</c>): a partial line names the
    /// core's reason code (<c>timeout</c>, <c>budget_exhausted</c>), so a
    /// screenshot tells the two apart.</summary>
    [Fact]
    public void APartialLineShowsTheReasonCode()
    {
        Assert.Equal(
            "Only part of your usage was downloaded (timeout). Syrtis will try again.",
            CursorSync.StatusLine("partial", null, reason: "timeout"));
        Assert.Equal(
            "Only part of your usage was downloaded (budget_exhausted). Syrtis will try again.",
            CursorSync.StatusLine("partial", null, reason: "budget_exhausted"));
    }

    /// <summary>S3b: a sealed sign-in with no "cursor" grant
    /// (<c>keychain_consent</c>) shows the consent line, not the generic
    /// error-with-reason line (the Allow control sits beside it), even though
    /// the code has the lowercase shape the reason-code branch would match.
    /// Mutation: drop the <c>keychain_consent</c> branch in
    /// <see cref="CursorSync.StatusLine"/> -> the line reads
    /// "Cursor usage couldn't be synced (keychain_consent)…".</summary>
    [Fact]
    public void AKeychainConsentReasonShowsTheConsentLine()
    {
        Assert.Equal(
            CursorSync.Copy.KeychainConsentLine,
            CursorSync.StatusLine("error", null, reason: "keychain_consent"));
        Assert.True(CursorSync.NeedsKeychainAllow("error", "keychain_consent"));
        // Not any other error, and not another state.
        Assert.False(CursorSync.NeedsKeychainAllow("error", "token_sealed_unreadable"));
        Assert.False(CursorSync.NeedsKeychainAllow("error", null));
        Assert.False(CursorSync.NeedsKeychainAllow("ok", "keychain_consent"));
    }

    /// <summary>CURSOR-BV2-W (macOS #554 design 7): each Billing V2 reason has
    /// its own line, which still carries the code and is not the generic
    /// coded line. Mutation: drop the <c>BillingV2Copy</c> branch in
    /// <see cref="CursorSync.StatusLine"/> -> each line reads "Cursor usage
    /// couldn't be synced (billing_v2_…)…".</summary>
    [Theory]
    [InlineData("billing_v2_team_required", CursorSync.Copy.BillingV2TeamRequired)]
    [InlineData("billing_v2_unavailable", CursorSync.Copy.BillingV2Unavailable)]
    [InlineData("billing_v2_no_items", CursorSync.Copy.BillingV2NoItems)]
    [InlineData("billing_v2_principal_mismatch", CursorSync.Copy.BillingV2PrincipalMismatch)]
    public void EachBillingV2ReasonHasItsOwnLineCarryingItsCode(string code, string copy)
    {
        var line = CursorSync.StatusLine("error", null, reason: code);
        Assert.Equal(string.Format(copy, code), line);
        Assert.Contains(code, line);
        Assert.NotEqual(CursorSync.Copy.ErrorWithReason.Localized(code), line);
        Assert.True(CursorSync.StatusIsWarning("error"));
        // Not on another state, and not the consent prompt.
        Assert.Equal(CursorSync.Copy.Offline, CursorSync.StatusLine("offline", null, reason: code));
        Assert.False(CursorSync.NeedsKeychainAllow("error", code));
    }

    private static CursorSyncStatus NeedsConsent(bool changed = false) =>
        new("error", 0, null, changed, [], Reason: "keychain_consent");

    /// <summary>A controller whose first sync blocks on the returned gate and
    /// answers <paramref name="results"/> in order (the last one repeats).</summary>
    private static (FakeCore Core, CursorSyncController Controller, ManualResetEventSlim Gate) Gated(
        params CursorSyncStatus[] results)
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore();
        var gate = new ManualResetEventSlim(false);
        core.SyncGate = gate;
        var next = -1;
        core.Result = () => results[Math.Min(Interlocked.Increment(ref next), results.Length - 1)];
        return (core, new CursorSyncController(core.Io, store, User, interval: Never), gate);
    }

    /// <summary>S3b: an Allow pressed while a user round runs, when that round
    /// ends refused for want of consent (<c>keychain_consent</c>), is served by
    /// one user round right after it; with no round running,
    /// <see cref="CursorSyncController.ConsentChanged"/> starts one.
    /// Mutation: CursorSync.cs <c>RunSync</c>
    /// <c>                    if (consentRerun)\n                    {\n                        _rerunPending = true;\n                    }</c>
    /// -> (delete) -> one round; the keychain_consent line stays.</summary>
    [Fact]
    public async Task AnAllowDuringARoundRefusedForConsentRerunsAsAUserRound()
    {
        var (core, controller, gate) = Gated(NeedsConsent(), Status("ok", 1));
        using var gateLease = gate;
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        Assert.True(controller.ConsentChanged(granted: true).IsCompleted, "queued behind the running round");
        gate.Set();
        await running;
        Assert.Equal(2, core.UserSyncs);
        Assert.Equal("ok", controller.State);
        Assert.Null(controller.Reason);

        await controller.ConsentChanged(granted: true);
        Assert.Equal(3, core.UserSyncs);
    }

    /// <summary>Round-3 item 1: the round an Allow reruns is still applied, so
    /// what it wrote reaches the dashboard: <c>DataChanged</c> fires on its
    /// <c>changed</c>.
    /// Mutation: CursorSync.cs <c>RunSync</c>
    /// <c>if (started != _generation || !CursorSync.ShouldSync(_store))</c>
    /// -> <c>if (started != _generation || consentRerun || !CursorSync.ShouldSync(_store))</c>
    /// (discard it) -> no DataChanged.</summary>
    [Fact]
    public async Task ARoundRerunForConsentStillRaisesDataChanged()
    {
        var (core, controller, gate) = Gated(NeedsConsent(changed: true), Status("ok", 1, changed: false));
        using var gateLease = gate;
        var data = 0;
        controller.DataChanged += () => Interlocked.Increment(ref data);
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        _ = controller.ConsentChanged(granted: true);
        gate.Set();
        await running;
        Assert.Equal(2, core.UserSyncs);
        Assert.Equal(1, data);
    }

    /// <summary>Round-3 item 2: a SCHEDULED round interrupted by an Allow is
    /// rerun as a user round (<c>io.Sync(true)</c>).
    /// Mutation: CursorSync.cs <c>RunSync</c> delete the block
    /// <c>if (again &amp;&amp; consentRerun) { user = true; _runningUser = true; _userPending = false; }</c>
    /// -> the rerun is <c>sync:auto</c>.</summary>
    [Fact]
    public async Task AnAllowDuringAScheduledRoundRerunsAsAUserRound()
    {
        var (core, controller, gate) = Gated(NeedsConsent(), Status("ok", 1));
        using var gateLease = gate;
        var running = controller.RunSync(userInitiated: false);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        _ = controller.ConsentChanged(granted: true);
        gate.Set();
        await running;
        Assert.Equal(["sync:auto", "sync:user"], core.Log.ToArray());
    }

    /// <summary>Round-3 item 3: scheduled round + Sync Now + Allow -> exactly
    /// one extra round, a user one: the promoted round serves the remembered
    /// press.
    /// Mutation: CursorSync.cs <c>RunSync</c>, in the promotion block,
    /// <c>                        _userPending = false;</c> -> (delete) -> two user rounds.</summary>
    [Fact]
    public async Task APromotedConsentRoundServesARememberedSyncNow()
    {
        var (core, controller, gate) = Gated(NeedsConsent(), Status("ok", 1));
        using var gateLease = gate;
        var running = controller.RunSync(userInitiated: false);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true); // remembered, returns at once
        _ = controller.ConsentChanged(granted: true);
        gate.Set();
        await running;
        Assert.Equal(["sync:auto", "sync:user"], core.Log.ToArray());
    }

    /// <summary>Round-4 F4: an Allow pressed while a round runs whose
    /// <c>io.Sync</c> throws (no result, so nothing says the round did not
    /// depend on the old answer) is served by one user round after it, which
    /// replaces the generic error. A Withdraw during a throwing round adds no
    /// round (assumption 6).
    /// Mutations: CursorSync.cs
    /// <c>                        &amp;&amp; (result is null || AskedForConsent(result));</c>
    /// -> <c>                        &amp;&amp; AskedForConsent(result);</c> -> one round, the generic error stays (first half fails);
    /// <c>consentRerun = startedConsent != _consentEpoch &amp;&amp; _consentGranted</c>
    /// -> <c>consentRerun = startedConsent != _consentEpoch</c> -> the Withdraw reruns (second half fails).</summary>
    [Fact]
    public async Task AnAllowDuringARoundWhoseSyncThrowsRerunsAsAUserRound()
    {
        static Func<CursorSyncStatus> ThrowsFirst()
        {
            var calls = 0;
            return () => Interlocked.Increment(ref calls) == 1
                ? throw new TbCoreException("tb_cursor_sync panicked")
                : Status("ok", 1);
        }

        var (core, controller, gate) = Gated(Status("ok", 1));
        core.Result = ThrowsFirst();
        using (gate)
        {
            var running = controller.RunSync(userInitiated: false);
            Assert.True(await core.SyncStarted.WaitAsync(Wait));
            _ = controller.ConsentChanged(granted: true);
            gate.Set();
            await running;
            Assert.Equal(["sync:auto", "sync:user"], core.Log.ToArray());
            Assert.Equal("ok", controller.State);
        }

        var (withdrawn, withdrawnController, withdrawnGate) = Gated(Status("ok", 1));
        withdrawn.Result = ThrowsFirst();
        using (withdrawnGate)
        {
            var running = withdrawnController.RunSync(userInitiated: false);
            Assert.True(await withdrawn.SyncStarted.WaitAsync(Wait));
            _ = withdrawnController.ConsentChanged(granted: false);
            withdrawnGate.Set();
            await running;
            Assert.Equal(["sync:auto"], withdrawn.Log.ToArray());
            Assert.Equal("error", withdrawnController.State);
            Assert.Null(withdrawnController.Reason);
        }
    }

    /// <summary>Round-3 item 4: a consent press reruns only a round that
    /// depended on the old answer. An Allow during a round that synced (a
    /// plaintext sign-in never reads consent; still one round after the
    /// round-4 change that reruns a thrown round), and a Withdraw during a
    /// round refused for consent, both leave one round.
    /// Mutations: CursorSync.cs
    /// <c>consentRerun = startedConsent != _consentEpoch &amp;&amp; _consentGranted\n                        &amp;&amp; (result is null || AskedForConsent(result));</c>
    /// -> <c>consentRerun = startedConsent != _consentEpoch &amp;&amp; _consentGranted;</c> (first half fails), or
    /// -> <c>consentRerun = startedConsent != _consentEpoch\n                        &amp;&amp; (result is null || AskedForConsent(result));</c> (second half fails).</summary>
    [Fact]
    public async Task AConsentPressDuringAnIndependentRoundDoesNotRerun()
    {
        var (core, controller, gate) = Gated(Status("ok", 1));
        using (gate)
        {
            var running = controller.RunSync(userInitiated: true);
            Assert.True(await core.SyncStarted.WaitAsync(Wait));
            _ = controller.ConsentChanged(granted: true);
            gate.Set();
            await running;
            Assert.Equal(1, core.UserSyncs);
        }

        var (refused, refusedController, refusedGate) = Gated(NeedsConsent());
        using (refusedGate)
        {
            var running = refusedController.RunSync(userInitiated: true);
            Assert.True(await refused.SyncStarted.WaitAsync(Wait));
            _ = refusedController.ConsentChanged(granted: false);
            refusedGate.Set();
            await running;
            Assert.Equal(1, refused.UserSyncs);
            Assert.Equal("keychain_consent", refusedController.Reason);
        }
    }

    /// <summary>Control: a plain Sync Now pressed during a user round is
    /// dropped (why the consent controls use <c>ConsentChanged</c>).</summary>
    [Fact]
    public async Task APlainSyncNowDuringAUserRoundIsDropped()
    {
        var (core, controller, gate) = Gated(Status("ok", 1));
        using var gateLease = gate;
        var running = controller.RunSync(userInitiated: true);
        Assert.True(await core.SyncStarted.WaitAsync(Wait));
        await controller.RunSync(userInitiated: true);
        gate.Set();
        await running;
        Assert.Equal(1, core.UserSyncs);
    }

    /// <summary>#547: the controller keeps the round's reason for the status
    /// line, and a round that threw has none.</summary>
    [Fact]
    public async Task TheControllerKeepsTheRoundsReason()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        Func<CursorSyncStatus> next = () => Status("error") with { Reason = "token_unreadable" };
        var core = new FakeCore { Result = () => next() };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        await controller.RunSync(userInitiated: true);
        Assert.Equal("error", controller.State);
        Assert.Equal("token_unreadable", controller.Reason);

        next = () => throw new TbCoreException("tb_cursor_sync panicked");
        await controller.RunSync(userInitiated: true);
        Assert.Equal("error", controller.State);
        Assert.Null(controller.Reason);
    }

    // ---- copy in the catalogs -----------------------------------------------------

    /// <summary>Shared spec §3 with the approved Windows wording (this PC /
    /// 這台電腦) and .NET's {0} for macOS's %@; plus the approved cleanupFailed
    /// line (§2).</summary>
    private static readonly Dictionary<string, string> Approved = new()
    {
        ["Cursor usage sync"] = "Cursor 用量同步",
        ["Sync Cursor usage from the Cursor app"] = "從 Cursor App 同步用量",
        ["To show your Cursor usage, Syrtis reads the login of the Cursor app on this PC and sends it only to Cursor's usage service (cursor.com) to download your usage. Syrtis stores the date, model, token counts, cost and conversation ID of each request on this PC; the login itself is never saved or logged. Turning this off deletes the downloaded usage."] =
            "要顯示 Cursor 用量，Syrtis 會讀取這台電腦上 Cursor App 的登入資訊，只送到 Cursor 的用量服務（cursor.com）下載你的用量。Syrtis 會在這台電腦上存下每次請求的日期、模型、token 數、費用和對話 ID；登入資訊本身不會儲存或記錄。關閉這個選項會刪除已下載的用量。",
        ["Continue"] = "繼續",
        ["Cancel"] = "取消",
        ["Turn Off"] = "關閉",
        ["Last synced {0}"] = "上次同步：{0}",
        ["Only part of your usage was downloaded. Syrtis will try again."] = "只下載到部分用量，Syrtis 會再試一次。",
        // #520, verbatim from macOS zh-Hant (%@ -> {0}).
        ["Only part of your usage was downloaded ({0}). Syrtis will try again."] = "只下載到部分用量（{0}），Syrtis 會再試一次。",
        ["Your Cursor login has expired. Open Cursor to refresh it."] = "Cursor 的登入已過期，打開 Cursor 就會更新。",
        ["Sign in to the Cursor app to sync your usage."] = "在 Cursor App 登入後就能同步用量。",
        ["Can't reach Cursor. Syrtis will try again."] = "連不上 Cursor，Syrtis 會再試一次。",
        ["Cursor usage couldn't be synced. Syrtis will try again later."] = "無法同步 Cursor 用量，Syrtis 稍後會再試。",
        // #547, verbatim from macOS zh-Hant (%@ -> {0}).
        ["Cursor usage couldn't be synced ({0}). Syrtis will try again later."] = "無法同步 Cursor 用量（{0}），Syrtis 稍後會再試。",
        ["Showing Cursor usage from tokscale CLI."] = "目前顯示的是 tokscale CLI 的 Cursor 用量。",
        ["Sync Now"] = "立即同步",
        ["Syncing…"] = "同步中…",
        ["You also have Cursor usage from tokscale CLI on this PC. Use Syrtis's own sync instead? Choose this only if it's the same Cursor account, or that account's usage will no longer be shown."] =
            "這台電腦上也有 tokscale CLI 的 Cursor 用量。要改用 Syrtis 自己的同步嗎？只有在是同一個 Cursor 帳號時才選，否則那個帳號的用量就不會再顯示。",
        ["Use Syrtis Sync"] = "改用 Syrtis 同步",
        ["Keep tokscale CLI Data"] = "保留 tokscale CLI 的資料",
        ["Cursor sync is off, but some downloaded usage couldn't be deleted. Restart Syrtis to retry."] =
            "Cursor 同步已關閉，但有部分已下載的用量無法刪除。重新啟動 Syrtis 即可再試。",
        // CMP-W, verbatim from macOS #532 / #535 zh-Hant.
        ["Cursor sync couldn't be set up, so nothing is being synced."] = "無法設定 Cursor 同步，目前不會同步任何用量。",
        ["Add a folder you start Cursor with using --user-data-dir to sync it too (Syrtis keeps the folder path in its settings); each profile's sign-in is handled as described above. All profiles add up to one Cursor total, and an account signed in to several profiles counts once. Removing a profile deletes its downloaded usage the next time Syrtis can."] =
            "加入用 --user-data-dir 啟動 Cursor 的資料夾（資料夾路徑會儲存在 Syrtis 設定中），就能一起同步；每個設定檔的登入資訊都照上一段的方式處理。所有設定檔合計成一個 Cursor 總量，同一個帳號只算一次。移除設定檔後，Syrtis 會在下次能處理時刪除它已下載的用量。",
        // S3b: DRAFT encrypted-sign-in consent copy (C4), pending maintainer
        // approval before merge.
        ["Cursor stores its sign-in encrypted. Syrtis needs your permission to read it."] =
            "Cursor 把登入資訊加密儲存，Syrtis 需要你的授權才能讀取。",
        ["Allow reading Cursor's encrypted sign-in…"] = "允許讀取 Cursor 的加密登入…",
        ["Cursor 3.27 and later can store its sign-in encrypted. To read it, Syrtis unlocks the key Cursor keeps in its \"Local State\" file on this PC. Windows does not ask, so this confirmation is the only check. That key can decrypt everything Cursor stores encrypted, but Syrtis uses it only to read your Cursor sign-in, keeps it in memory for that read alone, and never saves or logs it. The sign-in is sent only to Cursor's usage service (cursor.com). Turning on Cursor sync does not grant this. Withdraw stops Syrtis reading Cursor's encrypted sign-in; if Cursor stores its sign-in unencrypted, Syrtis still reads that and Withdraw does not change it."] =
            "Cursor 3.27 以後的版本可能會把登入資訊加密儲存。要讀取它，Syrtis 會解開 Cursor 存在這台電腦「Local State」檔案裡的金鑰。Windows 不會詢問，所以這個確認是唯一的把關。那把金鑰能解開 Cursor 所有加密儲存的資料，但 Syrtis 只用它來讀取你的 Cursor 登入，而且只在那次讀取時留在記憶體中，不會儲存或記錄。登入資訊只會送到 Cursor 的用量服務（cursor.com）。開啟 Cursor 同步並不代表授權這件事。撤回後，Syrtis 就不再讀取 Cursor 的加密登入；如果 Cursor 以未加密的方式儲存登入，Syrtis 仍會讀取，撤回不會改變這一點。",
        ["Withdraw"] = "撤回",
        ["Allow access"] = "允許存取",
        // CURSOR-BV2-W: Billing V2 reasons, verbatim from macOS #554 zh-Hant
        // (%@ -> {0}).
        ["Cursor's new billing asks for a team for this account, which Syrtis can't read yet ({0})."] =
            "Cursor 新版計費要求這個帳號指定團隊，Syrtis 目前還無法讀取（{0}）。",
        ["Cursor's new billing didn't provide this account's usage ({0}). Syrtis will try again later."] =
            "Cursor 新版計費沒有提供這個帳號的用量（{0}），Syrtis 稍後會再試。",
        ["Cursor's new billing has no usage for this account yet ({0}). Syrtis will try again later."] =
            "Cursor 新版計費目前還沒有這個帳號的用量（{0}），Syrtis 稍後會再試。",
        ["Cursor returned usage of another account, so Syrtis saved nothing ({0})."] =
            "Cursor 回傳的是其他帳號的用量，因此 Syrtis 沒有儲存任何資料（{0}）。",
    };

    private static Dictionary<string, string> Catalog(string tag) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"strings-{tag}.json")))!;

    [Fact]
    public void TheEnglishCopyIsTheApprovedText() =>
        Assert.Equal(Approved.Keys.OrderBy(k => k, StringComparer.Ordinal),
            CursorSync.Copy.All.OrderBy(k => k, StringComparer.Ordinal));

    [Fact]
    public void ZhHantIsTheApprovedTextVerbatim()
    {
        var hant = Catalog("zh-Hant");
        foreach (var (en, zh) in Approved)
        {
            Assert.True(hant.TryGetValue(en, out var value), en);
            Assert.Equal(zh, value);
        }
    }

    [Fact]
    public void ZhHansHasEveryKeyTranslated()
    {
        var hans = Catalog("zh-Hans");
        foreach (var en in CursorSync.Copy.All)
        {
            Assert.True(hans.TryGetValue(en, out var value), en);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(en, value);
            Assert.Equal(en.Contains("{0}"), value.Contains("{0}"));
            Assert.DoesNotContain("Mac", value, StringComparison.Ordinal);
        }
    }
}
