using System.Collections.Concurrent;
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

                return new CursorSyncConfig(enabled, null, takeover, 0, [], [], false, []);
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

        controller.SetEnabled(false);
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

    [Fact]
    public void TheSettingsSwitchAnswersTheNoticeWhenTurnedOn()
    {
        var store = TempStore();
        var controller = new CursorSyncController(new FakeCore().Io, store, User, interval: Never);
        controller.SetEnabled(true);
        Assert.True(CursorSync.NoticeAcknowledged(store));
        Assert.True(CursorSync.ShouldSync(store));
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
            controller.SetEnabled(true);
            controller.SetTakeoverConfirmed(true);
            controller.AnswerNotice(continuing: true);
            await controller.RunSync(userInitiated: true);
            await controller.ProbeCursorPresent();
            controller.SetEnabled(false); // off would delete files
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
        controller.SetEnabled(false);
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
        controller.SetEnabled(false);
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
        controller.SetEnabled(false);
        controller.SetEnabled(true);
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
        controller.SetEnabled(false);                          // the user turns it off
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
        controller.SetEnabled(false);
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
        controller.SetEnabled(false);
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

        controller.SetEnabled(false);
        await Until(() => core.Pushes.Count == 1);
        await Until(() => controller.CleanupFailed);
        Assert.True(Shown()); // sync off, line shown

        // Turning sync on hides the line ("sync is off" would be false) but
        // does not clear it: only a successful cleanup does.
        failCleanup = false;
        controller.SetEnabled(true);
        await Until(() => core.Pushes.Count == 2);
        await Task.Delay(50);
        Assert.True(controller.CleanupFailed);
        Assert.False(Shown());

        controller.SetEnabled(false);
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

        controller.SetEnabled(false);
        await Until(() => Shown()); // the previous failure

        using (var failAgain = new ManualResetEventSlim(false))
        {
            hold = failAgain;
            controller.SetEnabled(false); // retry, held
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
            controller.SetEnabled(false);
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
        ["Turn Off"] = "關閉",
        ["Last synced {0}"] = "上次同步：{0}",
        ["Only part of your usage was downloaded. Syrtis will try again."] = "只下載到部分用量，Syrtis 會再試一次。",
        ["Your Cursor login has expired. Open Cursor to refresh it."] = "Cursor 的登入已過期，打開 Cursor 就會更新。",
        ["Sign in to the Cursor app to sync your usage."] = "在 Cursor App 登入後就能同步用量。",
        ["Can't reach Cursor. Syrtis will try again."] = "連不上 Cursor，Syrtis 會再試一次。",
        ["Cursor usage couldn't be synced. Syrtis will try again later."] = "無法同步 Cursor 用量，Syrtis 稍後會再試。",
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
