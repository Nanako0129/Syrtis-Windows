using TokenBar.App;
using TokenBar.Interop;
using Component = TokenBar.Core.DiscordPresence.Component;
using CostStyle = TokenBar.Core.DiscordPresence.CostStyle;
using Selection = TokenBar.Core.DiscordPresence.Selection;
using VisibilityChange = TokenBar.Core.DiscordIpc.VisibilityChange;

namespace TokenBar.Core.Tests;

// F4: the App-side wiring, linked from TokenBar.App/DiscordPresenceWiring.cs —
// the four classifiers and their combination, the controller's triggers
// (launch, accepted graph, value-gated settings write, quit), the test-mode
// gate, the one-time intro and the consent copy.
public sealed class DiscordWiringTests : IDisposable
{
    private const string Today = "2026-09-25";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-discord-wiring", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SettingsStore NewStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

    private static readonly Selection MostUsed = new Selection.MostUsed();

    // ---- classifiers ------------------------------------------------------

    [Fact]
    public void HidingAPublishedClientIsReducingAndUnhidingIsIncreasing()
    {
        Assert.Equal(VisibilityChange.Reducing, DiscordClassifiers.Visibility("", "claude", MostUsed, MostUsed));
        Assert.Equal(VisibilityChange.Increasing, DiscordClassifiers.Visibility("claude", "", MostUsed, MostUsed));
        // One write that hides one client and unhides another is reducing.
        Assert.Equal(VisibilityChange.Reducing, DiscordClassifiers.Visibility("codex", "claude", MostUsed, MostUsed));
    }

    [Fact]
    public void HidingAClientWithNoUsageTodayIsNotAReduction()
    {
        var contributors = new HashSet<string> { "codex" };
        Assert.Equal(VisibilityChange.None,
            DiscordClassifiers.Visibility("", "claude", MostUsed, MostUsed, contributors));
    }

    [Fact]
    public void SwitchingSelectionWhileHidingTheOldOneIsReducing()
    {
        // AppDelegate.swift :188-197: one coalesced write that switches from
        // claude to codex and hides claude must not read as no change.
        Assert.Equal(VisibilityChange.Reducing, DiscordClassifiers.Visibility(
            "", "claude", new Selection.Only("claude"), new Selection.Only("codex")));
    }

    [Fact]
    public void HidingTheCanonicalIdOfAnAliasOnlyContributorIsReducing()
    {
        // Today's only usage is under the live-tail alias `claude-code`, in two
        // entries dated today; hiding `claude` takes it off the profile.
        var graph = new UsagePayload(
            DiscordPresenceTests.Graph().Meta, DiscordPresenceTests.Graph().Summary, [],
            [
                new Contribution(Today, new ContributionTotals(0, 0, 0), 1, new TokenBreakdown(0, 0, 0, 0, 0),
                    [Stripe("codex", 0, 0)]),
                new Contribution(Today, new ContributionTotals(0, 0, 0), 1, new TokenBreakdown(0, 0, 0, 0, 0),
                    [Stripe("claude-code", 5_000, 1)]),
            ]);

        var contributors = DiscordPresenceController.TodayContributors(graph, Today);

        Assert.Contains("claude", contributors);
        Assert.Equal(VisibilityChange.Reducing,
            DiscordClassifiers.Visibility("", "claude", MostUsed, MostUsed, contributors));
    }

    [Fact]
    public void RepeatedTriggersWhileOffSendOneStop()
    {
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        DiscordIpcClient? client = null;
        var (controller, _) = Controller(store, ["app"], () => client = new DiscordIpcClient(
            (_, _) => throw new IOException("Discord is not running"), Fast(TimeSpan.Zero)));
        controller.Launch();

        store.SetBool(DiscordPresence.EnabledKey, false);
        controller.OnSettingChanged(DiscordPresence.EnabledKey);
        Assert.Equal(1, client!.StopRequestsForTesting);

        // While off: new graphs and other watched writes change nothing.
        controller.OnGraph(Graph(Stripe("claude", 5_000, 1)));
        controller.OnGraph(Graph(Stripe("claude", 6_000, 1)));
        store.SetString(ClientRegistry.TabHiddenKey, "codex");
        controller.OnSettingChanged(ClientRegistry.TabHiddenKey);
        Assert.Equal(1, client.StopRequestsForTesting);

        // On and off again: one more Stop, not one per trigger.
        store.SetBool(DiscordPresence.EnabledKey, true);
        controller.OnSettingChanged(DiscordPresence.EnabledKey);
        store.SetBool(DiscordPresence.EnabledKey, false);
        controller.OnSettingChanged(DiscordPresence.EnabledKey);
        controller.OnGraph(Graph(Stripe("claude", 7_000, 1)));
        Assert.Equal(2, client.StopRequestsForTesting);
    }

    [Fact]
    public void SelectionComponentsAndCostStyleClassify()
    {
        var none = new HashSet<string>();
        Assert.Equal(VisibilityChange.Retiring,
            DiscordClassifiers.SelectionChange(MostUsed, new Selection.Only("codex"), none));
        // Two selections that are both hidden publish the same (nothing).
        Assert.Equal(VisibilityChange.None, DiscordClassifiers.SelectionChange(
            new Selection.Only("claude"), new Selection.Only("codex"), new HashSet<string> { "claude", "codex" }));

        var all = DiscordPresence.DefaultComponents;
        var tokensOnly = new HashSet<Component> { Component.Tokens };
        Assert.Equal(VisibilityChange.Reducing, DiscordClassifiers.ComponentsChange(all, tokensOnly));
        Assert.Equal(VisibilityChange.Increasing, DiscordClassifiers.ComponentsChange(tokensOnly, all));

        Assert.Equal(VisibilityChange.Reducing,
            DiscordClassifiers.CostStyleChange(CostStyle.WholeDollars, CostStyle.Banded));
        Assert.Equal(VisibilityChange.Increasing,
            DiscordClassifiers.CostStyleChange(CostStyle.Banded, CostStyle.WholeDollars));
        Assert.Equal(VisibilityChange.None,
            DiscordClassifiers.CostStyleChange(CostStyle.WholeDollars, CostStyle.Banded, publishedInBoth: false));
    }

    // ---- controller -------------------------------------------------------

    private static DiscordIpcClient.Timing Fast(TimeSpan publishInterval) => new(
        publishInterval, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));

    private static UsagePayload Graph(params ContributionClient[] today) => DiscordPresenceTests.Graph(today);

    private static ContributionClient Stripe(string id, long tokens, double cost) =>
        DiscordPresenceTests.Stripe(id, tokens, cost);

    private (DiscordPresenceController Controller, Func<int> Made) Controller(
        SettingsStore store, IReadOnlyList<string> arguments, Func<DiscordIpcClient> make)
    {
        var made = 0;
        var controller = new DiscordPresenceController(
            store, arguments,
            () =>
            {
                made++;
                return make();
            },
            () => Today);
        return (controller, () => made);
    }

    [Fact]
    public void TestModesAndTheOffSwitchNeverCreateAClient()
    {
        // Switched on under a test flag; switched off in a normal run.
        foreach (var (arguments, enabled) in new (string[], bool)[]
                 {
                     (["app", "--startup-smoke", "x"], true),
                     (["app", "--soak3d"], true),
                     (["app"], false),
                 })
        {
            var store = NewStore();
            store.SetBool(DiscordPresence.EnabledKey, enabled);
            var (controller, made) = Controller(
                store, arguments, () => throw new InvalidOperationException("must not connect"));

            controller.Launch();
            controller.OnGraph(Graph(Stripe("claude", 5_000, 1)));
            store.SetString(ClientRegistry.TabHiddenKey, "claude");
            controller.OnSettingChanged(ClientRegistry.TabHiddenKey);

            Assert.Equal(0, made());
        }
    }

    [Fact]
    public async Task NothingPublishesBeforeTheFirstGraphThenEveryGraphPublishes()
    {
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        var (controller, made) = Controller(
            store, ["app"], () => new DiscordIpcClient(server.Connector(), Fast(TimeSpan.Zero)));

        controller.Launch(); // enabled at launch: the worker starts and connects
        var connection = await server.HandshakeAsync();
        // A pre-graph publish (even a clear) would be flushed by READY and be
        // the first activity frame; none may exist.
        Assert.Null(await TryActivityAsync(connection, 300));

        controller.OnGraph(Graph(Stripe("claude", 1_234_567, 12)));
        Assert.Contains("1.2M tokens today", await FakeDiscord.NextActivityAsync(connection));

        controller.OnGraph(Graph(Stripe("claude", 2_345_678, 12)));
        Assert.Contains("2.3M tokens today", await FakeDiscord.NextActivityAsync(connection));
        Assert.Equal(1, made());

        controller.Quit(TimeSpan.FromMilliseconds(300));
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
    }

    [Fact]
    public async Task OnlyANewGraphRestartsAClientThatSpentItsBudget()
    {
        var attempts = 0;
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        DiscordIpcClient? client = null;
        var (controller, _) = Controller(store, ["app"], () => client = new DiscordIpcClient(
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("Discord is not running");
            },
            Fast(TimeSpan.Zero)));
        var graph = Graph(Stripe("claude", 5_000, 1));
        var budget = 1 + DiscordIpcClient.MaxReconnectAttempts;

        controller.Launch();
        await WaitAbandonedAsync(() => client!);
        Assert.Equal(budget, Volatile.Read(ref attempts));

        controller.OnGraph(graph); // a new graph: try again, once more bounded
        await Task.Delay(50);
        await WaitAbandonedAsync(() => client!);
        Assert.Equal(2 * budget, Volatile.Read(ref attempts));

        // The same instance again (a trace or quota tick): no restart.
        controller.OnGraph(graph);
        await Task.Delay(200);
        Assert.Equal(2 * budget, Volatile.Read(ref attempts));
    }

    private static async Task WaitAbandonedAsync(Func<DiscordIpcClient> client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await client().IsAbandonedForTestingAsync())
        {
            Assert.True(DateTime.UtcNow < deadline, "client never gave up");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task HidingTheShownClientRepublishesWithoutItAndTurningOffClears()
    {
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        var (controller, _) = Controller(
            store, ["app"],
            () => new DiscordIpcClient(server.Connector(), Fast(TimeSpan.FromMilliseconds(200))));
        controller.Launch();
        var connection = await server.HandshakeAsync();
        controller.OnGraph(Graph(Stripe("claude", 5_000_000, 40), Stripe("codex", 2_000, 1)));
        Assert.Contains("Claude Code", await FakeDiscord.NextActivityAsync(connection));

        // An unrelated or unchanged write publishes nothing new.
        store.SetString("tokenbar.popover.height", "500");
        controller.OnSettingChanged("tokenbar.popover.height");
        controller.OnSettingChanged(ClientRegistry.TabHiddenKey);

        store.SetString(ClientRegistry.TabHiddenKey, "claude");
        controller.OnSettingChanged(ClientRegistry.TabHiddenKey);
        var next = await FakeDiscord.NextActivityAsync(connection);
        Assert.DoesNotContain("Claude", next);
        Assert.Contains("Codex", next);

        store.SetBool(DiscordPresence.EnabledKey, false);
        controller.OnSettingChanged(DiscordPresence.EnabledKey);
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));

        // Graphs arriving while off never reconnect.
        controller.OnGraph(Graph(Stripe("codex", 9_000, 1)));
        await Task.Delay(200);
        Assert.Equal(1, server.AcceptCount);
    }

    [Fact]
    public async Task HideClassificationRetiresAGraphPublishQueuedBeforeIt()
    {
        // Floor 0, so a stale payload that ran would be written at once. The
        // graph republish is queued BEFORE the hide; only the hide being
        // classified reducing (epoch bump) keeps its Claude payload off the
        // wire.
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        DiscordIpcClient? client = null;
        var (controller, _) = Controller(store, ["app"],
            () => client = new DiscordIpcClient(server.Connector(), Fast(TimeSpan.Zero)));
        controller.Launch();
        var connection = await server.HandshakeAsync();
        controller.OnGraph(Graph(Stripe("claude", 5_000_000, 40), Stripe("codex", 2_000, 1)));
        Assert.Contains("Claude Code", await FakeDiscord.NextActivityAsync(connection));
        var gate = new TaskCompletionSource();

        client!.HoldForTesting(gate.Task);
        controller.OnGraph(Graph(Stripe("claude", 6_000_000, 50), Stripe("codex", 3_000, 1)));
        store.SetString(ClientRegistry.TabHiddenKey, "claude");
        controller.OnSettingChanged(ClientRegistry.TabHiddenKey);
        gate.SetResult();

        var next = await FakeDiscord.NextActivityAsync(connection);
        Assert.DoesNotContain("Claude", next);
        Assert.Contains("Codex", next);
        controller.Quit(TimeSpan.FromMilliseconds(300));
        while (await TryActivityAsync(connection, 300) is { } later)
        {
            Assert.DoesNotContain("Claude", later);
        }
    }

    [Fact]
    public async Task QuitWaitIsBoundedEvenWhenTheWorkerIsStuck()
    {
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        DiscordIpcClient? client = null;
        var (controller, _) = Controller(store, ["app"], () => client = new DiscordIpcClient(
            (_, _) => throw new IOException("Discord is not running"), Fast(TimeSpan.Zero)));
        controller.Launch();
        var gate = new TaskCompletionSource();
        client!.HoldForTesting(gate.Task); // e.g. a write sitting on its timeout
        try
        {
            var started = DateTime.UtcNow;
            var quit = Task.Run(() => controller.Quit(TimeSpan.FromMilliseconds(300)));

            // Generous bound; an unbounded wait would never finish while held.
            Assert.Same(quit, await Task.WhenAny(quit, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.SetResult();
        }
    }

    /// <summary>What a LocalFirst publication looks like: pricing bypassed
    /// (LocalOnly), every cost folded in at zero.</summary>
    private static UsagePayload LocalFirst(long claudeTokens)
    {
        var priced = Graph(Stripe("claude", claudeTokens, 0));
        return priced with
        {
            Meta = priced.Meta with { PricingMode = PricingMode.LocalOnly, CostCoverage = CostCoverage.None },
        };
    }

    [Fact]
    public async Task UnpricedGraphsNeverPublishAndSettingsUseTheLastAuthoritativeGraph()
    {
        // Floor 0: anything the controller hands the client goes out at once,
        // so "nothing on the wire" is a real observation, not the floor.
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        var (controller, _) = Controller(store, ["app"],
            () => new DiscordIpcClient(server.Connector(), Fast(TimeSpan.Zero)));
        controller.Launch();
        var connection = await server.HandshakeAsync();

        void Feed(UsagePayload graph) => controller.OnGraph(graph);

        // Startup: a LocalFirst graph lands first. Nothing publishes.
        var local1 = LocalFirst(9_000_000);
        Assert.False(CostSurfaceProjection.IsAuthoritative(local1));
        Feed(local1);
        Assert.Null(await TryActivityAsync(connection, 300));

        // The authoritative graph publishes as usual.
        Feed(Graph(Stripe("claude", 1_234_567, 12)));
        var first = await FakeDiscord.NextActivityAsync(connection);
        Assert.Contains("1.2M tokens today", first);
        Assert.Contains("$10-50", first);

        // A later refresh cycle's LocalFirst graph: nothing new on the wire —
        // no "<$10", no "$0", no LocalFirst token figure.
        Feed(LocalFirst(9_000_000));
        Assert.Null(await TryActivityAsync(connection, 300));

        // A settings republish in between uses the last AUTHORITATIVE graph.
        store.SetBool(DiscordPresence.WholeDollarsKey, true);
        controller.OnSettingChanged(DiscordPresence.WholeDollarsKey);
        var republished = await FakeDiscord.NextActivityAsync(connection);
        Assert.Contains("1.2M tokens today", republished);
        Assert.Contains("$12", republished);
        Assert.DoesNotContain("$0", republished);
        Assert.DoesNotContain("<$10", republished);

        // The Richer graph of that cycle publishes.
        Feed(Graph(Stripe("claude", 2_345_678, 30)));
        var richer = await FakeDiscord.NextActivityAsync(connection);
        Assert.Contains("2.3M tokens today", richer);
        Assert.Contains("$30", richer);
        controller.Quit(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task UnpricedGraphIsIgnoredEvenWhenTheTrayFlagWouldSayAuthoritative()
    {
        // The race this closes: TrayFeed.CostAuthoritative is a separate read
        // that a concurrent Richer accept can set to true while _feed.Graph
        // still returns the LocalFirst instance. The controller no longer takes
        // that flag at all — it judges the instance it was handed — so an
        // unpriced graph never publishes, whatever the tray's flag says.
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        var (controller, _) = Controller(store, ["app"],
            () => new DiscordIpcClient(server.Connector(), Fast(TimeSpan.Zero)));
        controller.Launch();
        var connection = await server.HandshakeAsync();
        controller.OnGraph(Graph(Stripe("claude", 1_234_567, 12)));
        Assert.Contains("$10-50", await FakeDiscord.NextActivityAsync(connection));

        // Handed the LocalFirst instance at the moment TrayFeed's flag already
        // reads true: the instance itself is what decides.
        var local = LocalFirst(9_000_000);
        Assert.False(CostSurfaceProjection.IsAuthoritative(local));

        controller.OnGraph(local);

        Assert.Null(await TryActivityAsync(connection, 300));
        controller.Quit(TimeSpan.FromMilliseconds(300));
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
    }

    [Fact]
    public async Task UntickingEveryComponentClearsTheProfile()
    {
        await using var server = new FakeDiscord();
        var store = NewStore();
        store.SetBool(DiscordPresence.EnabledKey, true);
        var (controller, _) = Controller(
            store, ["app"], () => new DiscordIpcClient(server.Connector(), Fast(TimeSpan.FromHours(1))));
        controller.Launch();
        var connection = await server.HandshakeAsync();
        controller.OnGraph(Graph(Stripe("claude", 5_000, 1)));
        Assert.Contains("details", await FakeDiscord.NextActivityAsync(connection));

        store.SetString(DiscordPresence.ComponentsKey, "");
        controller.OnSettingChanged(DiscordPresence.ComponentsKey);

        // Immediately, not after the one-hour floor: a clear adds nothing.
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection, 2_000));
        controller.Quit(TimeSpan.FromMilliseconds(300));
    }

    private static async Task<string?> TryActivityAsync(Stream connection, int timeoutMs)
    {
        try
        {
            var frame = await FakeDiscord.TryReadFrameAsync(connection, timeoutMs);
            return frame is { } f ? FakeDiscord.Activity(f.Body) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ---- intro --------------------------------------------------------------

    [Fact]
    public void IntroShowsOnceAndNeverToSomeoneAlreadyUsingIt()
    {
        var fresh = NewStore();
        Assert.True(DiscordIntro.Consume(fresh));
        Assert.False(DiscordIntro.Consume(fresh));
        Assert.False(DiscordPresence.Enabled(fresh)); // presenting enables nothing

        var existing = NewStore();
        existing.SetBool(DiscordPresence.EnabledKey, true);
        Assert.False(DiscordIntro.Consume(existing));
        existing.SetBool(DiscordPresence.EnabledKey, false);
        Assert.False(DiscordIntro.Consume(existing)); // consumed, not deferred
    }

    // ---- consent copy -------------------------------------------------------

    // The macOS copy VERBATIM — SettingsPanel.swift :944-1006 and
    // DiscordIntro.swift :104-108 in English, Localizable.strings :267-281 in
    // zh-Hant — and, separately, the exact product-name occurrences this port
    // renames TokenBar → Syrtis (user decision 2026-09-26). Any other drift
    // from macOS, in either language, fails here, and so does a rename that
    // stops matching exactly one occurrence. The zh values are read through the
    // SHIPPED table, so an English key that drifted by one character falls
    // back to English and fails too.
    private static readonly (string English, string Zh)[] MacOsCopy =
    [
        ("Show today's usage on Discord", "在 Discord 顯示今日用量"),
        ("Open Settings", "開啟設定"),
        ("Not now", "暫時不要"),
        ("1.2M tokens today", "今日 1.2M token"),
        ("Your Discord profile can show what you have been building today. Pick exactly what appears — or nothing at all — in Settings.",
            "你的 Discord 個人檔案可以顯示你今天在做什麼。要顯示哪些內容——或什麼都不顯示——都在設定裡挑。"),
        ("Off by default. Publishes what you pick below — today's tokens, a client name, a cost range or rounded figure — for whichever client you choose, and a link to TokenBar's source to your Discord profile. It updates while you work, so your active hours show too. Anyone who can see your profile can read and keep every update; switching this off stops new ones but cannot unshare what already went out. Hidden clients are never included, and a change here reaches your profile within about 15 seconds.",
            "預設關閉。會把你在下方勾選的內容——今日 token 數、用戶端名稱、花費級距或取整金額——依你指定的用戶端，連同一個 TokenBar 原始碼連結，發布到你的 Discord 個人檔案。工作時會持續更新，因此活動時段也會曝光。任何看得到你檔案的人都能讀取並保存每一次更新；關閉只會停止後續更新，無法收回已經送出的內容。已隱藏的用戶端不會送出；在這裡做的變更約 15 秒內反映到你的個人檔案。"),
        ("Include today's tokens", "包含今日 token 數"),
        ("Include the client name", "包含用戶端名稱"),
        ("Include cost", "包含花費"),
        ("Untick everything and nothing is published at all.", "全部取消勾選就完全不發布任何內容。"),
        ("Show cost as a figure instead of a range", "以金額而非級距顯示花費"),
        ("A range keeps you among everyone else in that band. A figure is rounded to the dollar, never cents, but still says more about you — every day. With one client named above, it becomes that tool's daily spend.",
            "級距讓你和同一區間裡的其他人混在一起。金額會取整到整數美元、不含分位，但仍然每天多透露一些。若上方指定了單一用戶端，它就等於那個工具的每日花費。"),
        ("Whichever client you used most", "用量最高的用戶端"),
        ("Naming one client publishes only its usage, so the totals can differ from the menu bar, which counts every client including ones TokenBar does not recognise. The cost becomes that one tool's daily spend rather than the whole day's.",
            "指定單一用戶端後只會發布它的用量，因此數字可能與選單列不同——選單列會計入所有用戶端，包含 TokenBar 未收錄的。花費也會變成那一個工具的當日金額，而非整天的總額。"),
    ];

    /// <summary>Every product-name occurrence renamed, as (macOS, Windows)
    /// fragments. Each must occur exactly once across the copy.</summary>
    private static readonly (string MacOs, string Windows)[] ProductRenames =
    [
        ("a link to TokenBar's source", "a link to Syrtis's source"),
        ("including ones TokenBar does not recognise", "including ones Syrtis does not recognise"),
        ("連同一個 TokenBar 原始碼連結", "連同一個 Syrtis 原始碼連結"),
        ("包含 TokenBar 未收錄的", "包含 Syrtis 未收錄的"),
    ];

    private static string Renamed(string macOs) =>
        ProductRenames.Aggregate(macOs, (text, rename) => text.Replace(rename.MacOs, rename.Windows));

    [Fact]
    public void ConsentCopyIsMacOsWithTheProductRenamed()
    {
        foreach (var (from, _) in ProductRenames)
        {
            Assert.Equal(1, MacOsCopy.Sum(copy =>
                Occurrences(copy.English, from) + Occurrences(copy.Zh, from)));
        }

        string[] windowsEnglish =
        [
            DiscordCopy.Toggle, DiscordCopy.OpenSettings, DiscordCopy.NotNow, DiscordCopy.PreviewDetails,
            DiscordCopy.IntroBody, DiscordCopy.Consent, DiscordCopy.IncludeTokens, DiscordCopy.IncludeClient,
            DiscordCopy.IncludeCost, DiscordCopy.UntickHint, DiscordCopy.WholeDollars,
            DiscordCopy.WholeDollarsHint, DiscordCopy.MostUsed, DiscordCopy.NamingHint,
        ];
        Assert.Equal(MacOsCopy.Length, windowsEnglish.Length);
        for (var i = 0; i < MacOsCopy.Length; i++)
        {
            Assert.Equal(Renamed(MacOsCopy[i].English), windowsEnglish[i]);
            Assert.DoesNotContain("TokenBar", windowsEnglish[i]);
        }

        // The Discord activity title is the portal app's name (renamed to
        // Syrtis on the portal, 2026-09-26).
        Assert.Equal("Syrtis", DiscordCopy.PreviewTitle);

        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < MacOsCopy.Length; i++)
            {
                Assert.Equal(Renamed(MacOsCopy[i].Zh), windowsEnglish[i].Localized());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    private static int Occurrences(string text, string fragment)
    {
        var count = 0;
        for (var at = text.IndexOf(fragment, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(fragment, at + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
