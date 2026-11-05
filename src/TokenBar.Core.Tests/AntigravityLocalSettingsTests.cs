using System.Collections.Concurrent;
using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// Slice W item 13 (MA-19, security review S15/S16/S18): the one-time move of
/// Antigravity's machine-bound settings from the roaming settings to the local
/// store. Every path is under a fresh temp folder and the Credential Manager
/// probe is a fake; nothing here reads %APPDATA%, %LOCALAPPDATA% or a
/// credential. Cases (1)–(6) are the plan's Acceptance W migration tests.
/// </summary>
public class AntigravityLocalSettingsTests
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string KeyR = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly string BindingA = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["key"] = KeyA, ["marker"] = "133000000000000000" });

    private sealed class Paths
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));
        public string Roaming => Path.Combine(Dir, "roaming", "settings.json");
        public string Local => Path.Combine(Dir, "local", "antigravity.json");
    }

    /// <summary>A roaming settings file listing <paramref name="accounts"/>,
    /// with the toggle on, A's binding and one removed key.</summary>
    private static SettingsStore Roaming(Paths paths, params string[] accounts)
    {
        var roaming = new SettingsStore(paths.Roaming);
        AntigravityAccounts.Save(roaming, [.. accounts.Select(k => new AntigravityAccount(k, k[..1] + "@example.com"))]);
        AntigravityAccounts.SaveRemovedKeys(roaming, [KeyR]);
        roaming.SetBool(AntigravityAutoCapture.EnabledKey, true);
        roaming.SetString(AntigravityAutoCapture.CurrentKey, BindingA);
        return new SettingsStore(paths.Roaming);
    }

    /// <summary>The probe: A's credential is on this PC, nothing else is.</summary>
    private static string OnlyAHere(string key) => key == KeyA ? AntigravityLocalSettings.Present : AntigravityLocalSettings.Absent;

    private static readonly Action<string> NoLog = _ => { };

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    // (1) this-PC case
    [Fact]
    public void AnAccountWhoseCredentialIsHereMovesWithItsBindingAndTheToggle()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var before = Bytes(paths.Roaming);

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);

        Assert.Equal([KeyA], AntigravityAccounts.Load(local).Select(a => a.Key));
        Assert.Equal("a@example.com", AntigravityAccounts.Label(local, KeyA));
        Assert.True(local.GetBool(AntigravityAutoCapture.EnabledKey, false));
        Assert.Equal(BindingA, local.GetString(AntigravityAutoCapture.CurrentKey));
        Assert.Equal([KeyR], AntigravityAccounts.RemovedKeys(local));
        Assert.True(local.GetBool(AntigravityLocalSettings.MigratedKey, false));
        // Written to disk, in the one file, marker included.
        var reread = new SettingsStore(paths.Local);
        Assert.Equal([KeyA], AntigravityAccounts.Load(reread).Select(a => a.Key));
        Assert.True(reread.GetBool(AntigravityLocalSettings.MigratedKey, false));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    // (2) roamed-in case
    [Fact]
    public void AnAccountWithoutACredentialHereStaysBehindAndTheToggleIsUnanswered()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyB);
        roaming = new SettingsStore(paths.Roaming);
        roaming.SetString(AntigravityAutoCapture.CurrentKey, JsonSerializer.Serialize(
            new Dictionary<string, string> { ["key"] = KeyB, ["marker"] = "133000000000000000" }));
        roaming = new SettingsStore(paths.Roaming);
        var before = Bytes(paths.Roaming);

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);

        Assert.Empty(AntigravityAccounts.Load(local));
        Assert.Null(local.GetNullableBool(AntigravityAutoCapture.EnabledKey));
        Assert.Null(local.GetString(AntigravityAutoCapture.CurrentKey));
        // Removed keys only suppress: they move as they are.
        Assert.Equal([KeyR], AntigravityAccounts.RemovedKeys(local));
        Assert.True(new SettingsStore(paths.Local).GetBool(AntigravityLocalSettings.MigratedKey, false));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    // (3) mixed
    [Fact]
    public void OfTwoAccountsOnlyTheOneBackedHereMoves()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyB, KeyA);
        var before = Bytes(paths.Roaming);

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);

        Assert.Equal([KeyA], AntigravityAccounts.Load(local).Select(a => a.Key));
        Assert.True(local.GetBool(AntigravityAutoCapture.EnabledKey, false));
        Assert.Equal(BindingA, local.GetString(AntigravityAutoCapture.CurrentKey));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    // (4) already moved
    [Fact]
    public void AStoreThatAlreadyMovedIsNeitherOverwrittenNorMergedWithRoaming()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var before = Bytes(paths.Roaming);
        var first = AntigravityLocalSettings.Open(paths.Local, roaming, _ => AntigravityLocalSettings.Absent, NoLog);
        Assert.Empty(AntigravityAccounts.Load(first));
        var moved = Bytes(paths.Local);
        var probes = 0;

        var again = AntigravityLocalSettings.Open(paths.Local, new SettingsStore(paths.Roaming), key =>
        {
            Interlocked.Increment(ref probes);
            return OnlyAHere(key);
        }, NoLog);

        Assert.Empty(AntigravityAccounts.Load(again));
        Assert.Equal(0, probes);
        Assert.Equal(moved, Bytes(paths.Local));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    // (5) probe failure
    [Theory]
    [InlineData("failed")]
    [InlineData("throws")]
    [InlineData("hangs")]
    public void AProbeFailureWritesNothingUsesNoRoamingValueAndRetriesNextLaunch(string failure)
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var before = Bytes(paths.Roaming);
        var hang = new ManualResetEventSlim(false);
        Func<string, string> broken = failure switch
        {
            "failed" => _ => "failed",
            "throws" => _ => throw new TbCoreException("unexpected"),
            _ => _ => { hang.Wait(TimeSpan.FromSeconds(10)); return AntigravityLocalSettings.Present; },
        };

        var session = AntigravityLocalSettings.Open(
            paths.Local, roaming, broken, NoLog, probeTimeout: TimeSpan.FromMilliseconds(200));
        hang.Set();

        Assert.False(File.Exists(paths.Local), "a failed move wrote the local store");
        Assert.Empty(AntigravityAccounts.Load(session));
        Assert.False(session.GetBool(AntigravityAutoCapture.EnabledKey, false));
        Assert.Null(session.GetString(AntigravityAutoCapture.CurrentKey));
        Assert.Empty(AntigravityAccounts.RemovedKeys(session));
        // This session's store never reaches a file, whatever it is given.
        session.SetBool(AntigravityAutoCapture.EnabledKey, true);
        Assert.False(File.Exists(paths.Local));
        Assert.Equal(before, Bytes(paths.Roaming));

        // Next launch, the probe works: the move happens then.
        var next = AntigravityLocalSettings.Open(paths.Local, new SettingsStore(paths.Roaming), OnlyAHere, NoLog);
        Assert.Equal([KeyA], AntigravityAccounts.Load(next).Select(a => a.Key));
        Assert.True(next.GetBool(AntigravityAutoCapture.EnabledKey, false));
    }

    // (6) first poll after launch
    [Fact]
    public async Task ARoamedInToggleStartsNoCaptureOnTheFirstPoll()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyB);
        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);
        var calls = new ConcurrentQueue<string>();
        var io = new AntigravityAutoCapture.Io(
            Marker: () => { calls.Enqueue("marker"); return "133000000000000001"; },
            AutoCapture: _ => { calls.Enqueue("auto"); return new AntigravityAutoCaptureResult("captured", KeyB, "b@example.com"); },
            Capture: () => { calls.Enqueue("capture"); return new AntigravityAccount(KeyB, "b@example.com"); },
            Remove: _ => calls.Enqueue("remove"),
            Install: () => calls.Enqueue("install"));
        var capture = new AntigravityAutoCapture(io, local);

        AntigravityFetch.Run(
            () => new AgentUsagePayload("2026-11-05T00:00:00Z", []), null, capture, _ => { });
        // Anything the fetch started has had time to reach the fake.
        await Task.Delay(200);

        Assert.False(capture.IsEnabled);
        Assert.DoesNotContain("auto", calls);
        Assert.DoesNotContain("marker", calls);
        Assert.Null(capture.Current.Key);
    }

    /// <summary>A local file that exists without the marker (SettingsStore
    /// set a corrupt one aside) never falls back to the roaming values.</summary>
    [Fact]
    public void AnUnreadableLocalStoreStartsEmptyInsteadOfRereadingRoaming()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Local)!);
        File.WriteAllText(paths.Local, "{ not json");

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);

        Assert.Empty(AntigravityAccounts.Load(local));
        Assert.False(local.GetBool(AntigravityAutoCapture.EnabledKey, false));
        Assert.True(new SettingsStore(paths.Local).GetBool(AntigravityLocalSettings.MigratedKey, false));
    }

    /// <summary>The move's single save: every key lands in one file write
    /// (one temp file, one replacing move), never key by key.</summary>
    [Fact]
    public void SetManyWritesEveryKeyInOneSave()
    {
        var paths = new Paths();
        var store = new SettingsStore(paths.Local);
        var changed = new List<string>();
        store.Changed += changed.Add;

        store.SetMany(new Dictionary<string, JsonElement>
        {
            ["a"] = JsonSerializer.SerializeToElement(true),
            ["b"] = JsonSerializer.SerializeToElement("x"),
        });

        var reread = new SettingsStore(paths.Local);
        Assert.True(reread.GetBool("a", false));
        Assert.Equal("x", reread.GetString("b"));
        Assert.Equal(["a", "b"], changed);
        Assert.False(File.Exists(paths.Local + ".tmp"));
    }
}
