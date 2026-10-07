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

    private static CursorSyncStatus Status(string state, long events = 0, long? last = 1) =>
        new(state, events, last);

    /// <summary>Fake core. Every call is logged in order; the optional gates
    /// hold a push or a sync until the test releases it.</summary>
    private sealed class FakeCore
    {
        public readonly ConcurrentQueue<string> Log = new();
        public readonly ConcurrentQueue<(bool Enabled, bool Takeover)> Pushes = new();
        public Func<bool, bool, Exception?> PushError = (_, _) => null;
        public Func<bool, ManualResetEventSlim?> PushGate = _ => null;
        public ManualResetEventSlim? SyncGate;
        public readonly SemaphoreSlim SyncStarted = new(0);
        public Func<CursorSyncStatus> Result = () => Status("ok", 1);
        public bool Present = true;
        public int PresenceProbes;
        public volatile bool CoreTakeover;

        public int Calls => Log.Count + PresenceProbes;

        public CursorSyncController.Io Io => new(
            SetConfig: (enabled, takeover) =>
            {
                PushGate(enabled)?.Wait(Wait);
                Pushes.Enqueue((enabled, takeover));
                CoreTakeover = takeover;
                Log.Enqueue(enabled ? "push:on" : "push:off");
                if (PushError(enabled, takeover) is { } error)
                {
                    throw error;
                }

                return new CursorSyncConfig(enabled, null, takeover, 0);
            },
            Sync: userInitiated =>
            {
                Log.Enqueue(userInitiated ? "sync:user" : "sync:auto");
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

    [Fact]
    public async Task ThePreferencesReachTheCoreAsTheTwoKeyConfig()
    {
        var store = TempStore();
        var core = new FakeCore();
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        string Last() => CursorSyncRequest.Json(core.Pushes.Last().Enabled, core.Pushes.Last().Takeover);

        Assert.True(CursorSync.Enabled(store)); // D3: default ON
        controller.Reconfigure(refresh: false);
        await Until(() => core.Pushes.Count == 1);
        // Default on, but the notice is unanswered: the core is told off.
        Assert.Equal("""{"enabled":false,"cliTakeoverConfirmed":false}""", Last());

        controller.AnswerNotice(continuing: true);
        await Until(() => core.Pushes.Count == 2);
        Assert.Equal("""{"enabled":true,"cliTakeoverConfirmed":false}""", Last());

        controller.SetTakeoverConfirmed(true);
        await Until(() => core.Pushes.Count == 3);
        Assert.Equal("""{"enabled":true,"cliTakeoverConfirmed":true}""", Last());

        controller.SetEnabled(false);
        await Until(() => core.Pushes.Count == 4);
        Assert.Equal("""{"enabled":false,"cliTakeoverConfirmed":true}""", Last());
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

    [Fact]
    public async Task OffThenOnRefreshesAgainForTheSameEventCount()
    {
        var store = TempStore();
        store.SetBool(CursorSync.NoticeKey, true);
        var core = new FakeCore { Result = () => Status("ok", 7) };
        var controller = new CursorSyncController(core.Io, store, User, interval: Never);
        var data = 0;
        controller.DataChanged += () => Interlocked.Increment(ref data);

        await controller.RunSync(userInitiated: true);
        Assert.Equal(1, data); // a changed count refreshes
        await controller.RunSync(userInitiated: true);
        Assert.Equal(1, data); // the same count does not

        store.SetBool(CursorSync.EnabledKey, false);
        controller.Reconfigure(refresh: false);
        store.SetBool(CursorSync.EnabledKey, true);
        await controller.RunSync(userInitiated: true);
        Assert.Equal(2, data); // off deleted the files: the same count refreshes again
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
        string[] problems = ["partial", "expired", "notSignedIn", "offline", "error", "cliPresent"];
        Assert.Equal(6, problems.Select(s => CursorSync.StatusLine(s, null)).Distinct().Count());
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
        ["Cursor sync is off, but some downloaded usage couldn't be deleted. Turn sync on and off again to retry."] =
            "Cursor 同步已關閉，但有部分已下載的用量無法刪除。重新開關一次即可再試。",
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
