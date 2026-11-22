using System.Text.Json;
using TokenBar.App;
using TokenBar.Interop;
using C = TokenBar.App.DevicesCopy;

namespace TokenBar.Core.Tests;

/// <summary>Settings → Devices copy against the shipped tables, the error
/// class mapping (SelfTest.swift :22828-22887, Syrtis f8a1a0ae), the listen
/// menu (:23248-23277) and the agent lifecycle (RemoteAgentLaunch :105-108,
/// SelfTest :22684-22687). macOS compares X.localized(args) with itself; the
/// literal rows here also catch a placeholder that is never filled.</summary>
public class DevicesCopyTests : IDisposable
{
    public DevicesCopyTests() => Load("en");

    public void Dispose() => Load("en");

    private static void Load(string tag) => Localization.Load(tag, AppContext.BaseDirectory);

    private static Dictionary<string, string> Table(string tag) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"strings-{tag}.json")))!;

    [Fact] // :22828-22835
    public void EveryDevicesKeyHasATranslationInBothTables()
    {
        foreach (var tag in new[] { "zh-Hant", "zh-Hans" })
        {
            var table = Table(tag);
            var missing = C.All.Where(k => !table.TryGetValue(k, out var v) || v.Length == 0 || v == k).ToList();
            Assert.True(missing.Count == 0, $"{tag}: {string.Join(" | ", missing)}");
        }
    }

    [Fact] // brief: no value says "Mac" or carries a Cocoa placeholder
    public void NoKeyOrValueSaysMacOrCarriesACocoaPlaceholder()
    {
        var hant = Table("zh-Hant");
        var hans = Table("zh-Hans");
        foreach (var text in C.All.Concat(C.All.Select(k => hant[k])).Concat(C.All.Select(k => hans[k])))
        {
            Assert.DoesNotContain("Mac", text);
            Assert.DoesNotMatch(@"%(\d+\$)?(@|lld|ld|d|s)", text);
        }

        Assert.Equal(C.All.Count, C.All.Distinct().Count());
    }

    private sealed record Lit(string Key, string Arg, string En, string Hant, string Hans);

    // One row for every key with a placeholder, the expected text typed out.
    private static readonly Lit[] Literals =
    [
        new(C.ExpiresIn, "9:41", "Expires in 9:41", "剩餘 9:41", "剩余 9:41"),
        new(C.RevokeTitle, "0123456789abcdef", "Revoke access for 0123456789abcdef?", "要撤銷 0123456789abcdef 的存取權嗎？", "要撤销 0123456789abcdef 的访问权限吗？"),
        new(C.EnvRoots, "CODEX_HOME", "Some clients' folders are set by environment variables (CODEX_HOME). This PC's pages follow them; sharing doesn't.", "部分用戶端的資料夾由環境變數指定（CODEX_HOME）。這台電腦的頁面會採用它們，分享不會。", "部分客户端的文件夹由环境变量指定（CODEX_HOME）。这台电脑的页面会采用它们，共享不会。"),
        new(C.RemoveTitle, "10.0.0.2:47000", "Remove 10.0.0.2:47000?", "要移除 10.0.0.2:47000 嗎？", "要移除 10.0.0.2:47000 吗？"),
        new(C.YourCode, "5555-6666", "Code: 5555-6666", "代碼：5555-6666", "代码：5555-6666"),
    ];

    [Fact]
    public void EveryPlaceholderKeyFormatsLiterally()
    {
        Assert.Equal(Literals.Length, C.All.Count(k => k.Contains("{0}")));
        foreach (var (tag, pick) in new (string, Func<Lit, string>)[]
            { ("en", l => l.En), ("zh-Hant", l => l.Hant), ("zh-Hans", l => l.Hans) })
        {
            Load(tag);
            foreach (var lit in Literals)
            {
                Assert.Equal(pick(lit), lit.Key.Localized(lit.Arg));
            }
        }
    }

    [Fact] // the approved Windows sentences, word for word (plan §5-Q4, §6, 2026-11-22 ledger)
    public void TheApprovedWindowsSentencesAreExact()
    {
        Load("zh-Hant");
        Assert.Equal("核准後，該裝置可以讀取這台電腦在已核准掃描範圍內的用量彙總，直到你撤銷為止。該裝置可能會把它與自己的用量合併顯示，包括顯示在它的 Discord 狀態上。", C.ApproveEffect.Localized());
        Assert.Equal("第一次套用時 Windows 可能詢問是否允許 Syrtis 通過防火牆，請選擇允許，否則其他裝置連不到這台電腦。", C.FirewallNote.Localized());
        Assert.Equal("能寫入你 AppData 資料夾的程式或帳號（例如 OpenAI Codex 的 sandbox），可以在 Syrtis 沒有執行時更動分享服務的資料夾。", C.FolderWritable.Localized());
        Assert.Equal("被這樣替換的資料夾，可能讓這台電腦分享給你沒有核准的裝置。", C.FolderReplaced.Localized());
        Assert.Equal("放回較舊的副本會還原你之後做的變更，讓你已撤銷的裝置又能讀取你的用量。", C.FolderRestored.Localized());
        Assert.Equal("Approving lets that device read this PC's usage totals in the approved scan scope until you revoke it. That device may show them combined with its own, including on its Discord status.", C.ApproveEffect);
        Assert.Equal("The first time you apply, Windows may ask whether to allow Syrtis through the firewall. Choose Allow, or other devices can't reach this PC.", C.FirewallNote);
        // The three W4 lines sit in the "Share this PC" info flyout, after macOS's two.
        Assert.Equal([C.ShareOffNote, C.ApproveEffect, C.FolderWritable, C.FolderReplaced, C.FolderRestored],
            C.ShareInfoRows().Select(r => r.Text));
    }

    [Fact] // the "this Mac" substitutions, two of the thirteen typed out
    public void ThisMacReadsThisPc()
    {
        Load("zh-Hant");
        Assert.Equal("分享這台電腦", C.ShareSection.Localized());
        Assert.Equal("僅限這台電腦（127.0.0.1）", C.LocalOnlyLabel.Localized());
        Load("zh-Hans");
        Assert.Equal("共享这台电脑", C.ShareSection.Localized());
        Assert.Equal("这台电脑将停止读取该设备的用量。要再读取，请重新连接。", C.RemoveMessage.Localized());
    }

    [Fact] // :22873-22887
    public void EachErrorClassMapsToItsFixedLine()
    {
        Assert.Equal(C.SharingAlreadyOn, C.SharingFailure("invalid"));
        Assert.Equal(C.SharingBusy, C.SharingFailure("busy"));
        Assert.Equal(C.GenericFailure, C.SharingFailure("x"));
        Assert.Equal(C.StartUnsafe, C.StartFailure("unsafeState"));
        Assert.Equal(C.StartUnsafe, C.StartFailure("unsafeSocket"));
        Assert.Equal(C.StartUnsafe, C.StartFailure("pathTooLong"));
        Assert.Equal(C.StartBusy, C.StartFailure("busy"));
        Assert.Equal(C.StartFailed, C.StartFailure("homeRequired"));
        Assert.Equal(C.OfferNeedsSharing, C.OfferFailure("unauthorized"));
        Assert.Equal(C.OfferNeedsAddress, C.OfferFailure("invalid"));
        Assert.Equal(C.GenericFailure, C.OfferFailure("panic"));
        Assert.Equal(C.InvitationExpired, C.LinkFailure("expired"));
        Assert.Equal(C.Declined, C.LinkFailure("rejected"));
        Assert.Equal(C.UnreachableDevice, C.LinkFailure("transport"));
        Assert.Equal(C.IdentityChanged, C.LinkFailure("identityChanged"));
        Assert.Equal(C.InvalidInvitation, C.LinkFailure("invalidOffer"));
        Assert.Equal(C.OwnInvitation, C.LinkFailure("selfPairing"));
        Assert.Equal(C.UnusableInvitation, C.LinkFailure("offerUnusable"));
        Assert.Equal(C.UnusableInvitation, C.LinkFailure("unauthorized"));
        Assert.Equal(C.UnusableInvitation, C.LinkFailure("revoked"));
        Assert.Equal(C.LinkFailed, C.LinkFailure("panic"));
        Assert.Equal(C.StateActive, C.State("active"));
        Assert.Equal(C.StateRevoked, C.State("revoked"));
        Assert.Equal(C.StateExpired, C.State("expired"));
        Assert.Equal("superseded", C.State("superseded"));
    }

    [Fact] // :22836-22841
    public void TheEnvironmentNoticeNamesEachVariableOnceInOrder()
    {
        RemoteEnvRoot[] roots = [new("amp", "TOKSCALE_CONFIG_DIR"), new("codex", "CODEX_HOME"), new("pi", "TOKSCALE_CONFIG_DIR")];
        Assert.Equal("TOKSCALE_CONFIG_DIR, CODEX_HOME", DevicesModel.VariableNames(roots));
    }

    [Fact] // DSV :460-462
    public void AddedFoldersReadClientColonPathClientsSorted()
    {
        var paths = new Dictionary<string, IReadOnlyList<string>>
        {
            ["codex"] = [@"D:\b"],
            ["claude"] = [@"D:\a", @"D:\c"],
        };
        Assert.Equal([@"claude: D:\a", @"claude: D:\c", @"codex: D:\b"], DevicesModel.Flatten(paths));
    }

    // ---- Listen menu (:23248-23277) ---------------------------------------

    [Fact] // :23255-23260
    public void TheConfiguredAddressAndSelectionStayListed()
    {
        Assert.Equal(["127.0.0.1", "192.168.1.20", "2001:db8::9"],
            ListenAddress.Choices(["192.168.1.20", "2001:db8::5"], ["2001:db8::9", "192.168.1.20"]));
        Assert.Equal(["127.0.0.1", "192.168.1.20", "2001:db8::9", "10.0.0.7"],
            ListenAddress.Choices(["192.168.1.20"], ["2001:db8::9", "10.0.0.7"]));
    }

    [Fact] // :23261-23264
    public void NothingKeptIsUnspecifiedLoopbackZonedOrNotAnAddress()
    {
        Assert.Equal(["127.0.0.1"],
            ListenAddress.Choices([], ["", "0.0.0.0", "::", "::1", "localhost", "fe80::1%en0", "127.0.0.1", "1", "1.2", "10.0.0.256"]));
    }

    [Fact] // :23249-23271
    public void TheMenuIsThisPcPlusIpv4AndUniqueLocalIpv6()
    {
        var choices = ListenAddress.Choices([
            "0.0.0.0", "::", "192.168.1.20", "fe80::1%12", "fe80::2", "127.0.0.1", "::1",
            "10.0.0.2", "192.168.1.20", "2001:db8::5", "fd7a:115c:a1e0::1f3b:e46f", "100.76.1.2",
            "fd96:4fe:ccd9::7",
        ]);
        Assert.Equal([
            "127.0.0.1", "192.168.1.20", "10.0.0.2", "fd7a:115c:a1e0::1f3b:e46f", "100.76.1.2", "fd96:4fe:ccd9::7",
        ], choices);
        var local = ListenAddress.Choices(ListenAddress.InterfaceAddresses());
        Assert.Equal(ListenAddress.LocalOnly, local[0]);
        Assert.DoesNotContain("0.0.0.0", local);
        Assert.DoesNotContain("::", local);
    }

    [Fact] // :23272-23277
    public void APublicAddressGetsTheInternetNotePrivateOnesDoNot()
    {
        Assert.True(ListenAddress.MayBePublic("8.8.8.8"));
        Assert.True(ListenAddress.MayBePublic("2001:db8::5"));
        foreach (var local in new[] { "192.168.1.20", "10.0.0.2", "172.16.0.1", "100.100.1.1", "fd00::1", "127.0.0.1", "169.254.1.1", "", "localhost" })
        {
            Assert.False(ListenAddress.MayBePublic(local), local);
        }
    }

    // ---- Agent lifecycle (RemoteAgentLaunch) ------------------------------

    [Fact] // SelfTest :22684-22687
    public void OnlyABareWordIsLoggedAsAClass()
    {
        Assert.True(RemoteAgentLifecycle.IsClassString("unsafeState"));
        Assert.False(RemoteAgentLifecycle.IsClassString(@"C:\x"));
        Assert.False(RemoteAgentLifecycle.IsClassString("tb_remote_start panicked: x"));
        Assert.False(RemoteAgentLifecycle.IsClassString(""));
        Assert.False(RemoteAgentLifecycle.IsClassString(new string('a', 33)));
        Assert.Equal("busy", RemoteAgentLifecycle.Describe(new TbCoreException("busy")));
        Assert.Equal("(unclassified)", RemoteAgentLifecycle.Describe(new TbCoreException("syrtis://pair?x")));
        Assert.Equal("(unclassified)", RemoteAgentLifecycle.Describe(new InvalidOperationException("busy")));
    }

    private sealed class Agent
    {
        public int Starts;
        public int Stops;
        public readonly List<string> Log = [];
        public ManualResetEventSlim StopGate { get; } = new(true);
        public string? StartError;

        public RemoteAgentLifecycle Make() => new(
            () =>
            {
                Interlocked.Increment(ref Starts);
                if (StartError is { } e) { throw new TbCoreException(e); }
                return new RemoteAgentStatus(true);
            },
            () =>
            {
                Interlocked.Increment(ref Stops);
                StopGate.Wait();
                return new RemoteAgentStatus(false);
            },
            line => { lock (Log) { Log.Add(line); } });
    }

    private static bool Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !condition()) { Thread.Sleep(5); }
        return condition();
    }

    [Fact] // :68-89: launch starts once after its before-step; inputs restart only after launch, never after stop
    public void LaunchInputsChangedAndStop()
    {
        var agent = new Agent();
        var lifecycle = agent.Make();
        lifecycle.InputsChanged();
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref agent.Starts));

        var waitedFirst = false;
        lifecycle.Launch(() => { Thread.Sleep(50); waitedFirst = Volatile.Read(ref agent.Starts) == 0; });
        Assert.True(Eventually(() => Volatile.Read(ref agent.Starts) == 1));
        Assert.True(waitedFirst);
        lifecycle.Launch();
        lifecycle.InputsChanged();
        Assert.True(Eventually(() => Volatile.Read(ref agent.Starts) == 2));
        Thread.Sleep(100);
        Assert.Equal(2, Volatile.Read(ref agent.Starts));

        Assert.True(lifecycle.Stop(TimeSpan.FromSeconds(5)));
        lifecycle.InputsChanged();
        Thread.Sleep(100);
        Assert.Equal(2, Volatile.Read(ref agent.Starts));
        Assert.Equal(1, agent.Stops);
        Assert.True(lifecycle.Stop(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, agent.Stops);
    }

    [Fact] // brief: exit is not delayed past the bound by a slow stop (up to 15 s in the core)
    public void StopReturnsAtTheBoundWhenTheCoreIsSlow()
    {
        var agent = new Agent();
        agent.StopGate.Reset();
        var lifecycle = agent.Make();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(lifecycle.Stop(TimeSpan.FromMilliseconds(200)));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 3000);
        agent.StopGate.Set();
    }

    [Fact] // a start failure logs its class string only
    public void AStartFailureLogsTheClassOnly()
    {
        var agent = new Agent { StartError = "unsafeState" };
        agent.Make().Launch();
        Assert.True(Eventually(() => { lock (agent.Log) { return agent.Log.Count == 1; } }));
        Assert.Equal("remote-agent: start failed unsafeState", agent.Log[0]);
        var leaky = new Agent { StartError = @"C:\Users\x\AppData" };
        leaky.Make().Launch();
        Assert.True(Eventually(() => { lock (leaky.Log) { return leaky.Log.Count == 1; } }));
        Assert.Equal("remote-agent: start failed (unclassified)", leaky.Log[0]);
    }
}
