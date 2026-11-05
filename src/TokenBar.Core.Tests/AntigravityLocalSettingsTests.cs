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
        Roaming(paths, KeyB);
        var roaming = new SettingsStore(paths.Roaming);
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

    /// <summary>The binding moves only with its own account, even when
    /// another account moved.</summary>
    [Fact]
    public void ABindingToAnAccountLeftBehindDoesNotMove()
    {
        var paths = new Paths();
        Roaming(paths, KeyB, KeyA);
        var roaming = new SettingsStore(paths.Roaming);
        roaming.SetString(AntigravityAutoCapture.CurrentKey, JsonSerializer.Serialize(
            new Dictionary<string, string> { ["key"] = KeyB, ["marker"] = "133000000000000000" }));
        roaming = new SettingsStore(paths.Roaming);

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);

        Assert.Equal([KeyA], AntigravityAccounts.Load(local).Select(a => a.Key));
        Assert.Null(local.GetString(AntigravityAutoCapture.CurrentKey));
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
        var log = new ConcurrentQueue<string>();

        var again = AntigravityLocalSettings.Open(paths.Local, new SettingsStore(paths.Roaming), key =>
        {
            Interlocked.Increment(ref probes);
            return OnlyAHere(key);
        }, log.Enqueue);

        Assert.Empty(AntigravityAccounts.Load(again));
        Assert.Equal(0, probes);
        Assert.Empty(log);
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
            Install: () => { calls.Enqueue("install"); return true; });
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

    /// <summary>Review F2: a local file without the done marker (a corrupt
    /// one SettingsStore sets aside, <c>null</c>, <c>{}</c>) has not been
    /// moved to: the move runs, gated by the probe as always. Review D1: that
    /// re-move leaves the toggle off and unanswered even though roaming has it
    /// on (the roaming value is stale; the user may have turned it off here).</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ALocalFileWithoutTheMarkerStillGetsTheMove(string contents)
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA, KeyB);
        var before = Bytes(paths.Roaming);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Local)!);
        File.WriteAllText(paths.Local, contents);

        var log = new ConcurrentQueue<string>();

        var local = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, log.Enqueue);

        Assert.Equal([KeyA], AntigravityAccounts.Load(local).Select(a => a.Key));
        Assert.Equal(BindingA, local.GetString(AntigravityAutoCapture.CurrentKey));
        Assert.Null(local.GetNullableBool(AntigravityAutoCapture.EnabledKey));
        Assert.Contains(log, line => line.Contains("no move marker", StringComparison.Ordinal));
        var reread = new SettingsStore(paths.Local);
        Assert.Null(reread.GetNullableBool(AntigravityAutoCapture.EnabledKey));
        Assert.True(reread.GetBool(AntigravityLocalSettings.MigratedKey, false));
        Assert.Equal([KeyA], AntigravityAccounts.Load(reread).Select(a => a.Key));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    /// <summary>Review F1 through the real path: after a failed move the
    /// session store does not save, so Capture makes no core call and nothing
    /// is written anywhere.</summary>
    [Fact]
    public async Task AfterAFailedMoveCaptureMakesNoCoreCallAndWritesNothing()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var before = Bytes(paths.Roaming);
        var session = AntigravityLocalSettings.Open(paths.Local, roaming, _ => "failed", NoLog);
        Assert.False(session.Persists);
        var calls = new ConcurrentQueue<string>();
        var io = new AntigravityAutoCapture.Io(
            Marker: () => { calls.Enqueue("marker"); return "133000000000000001"; },
            AutoCapture: _ => { calls.Enqueue("auto"); return new AntigravityAutoCaptureResult("captured", KeyA, "a@example.com"); },
            Capture: () => { calls.Enqueue("capture"); return new AntigravityAccount(KeyA, "a@example.com"); },
            Remove: _ => calls.Enqueue("remove"),
            Install: () => { calls.Enqueue("install"); return true; });
        var capture = new AntigravityAutoCapture(io, session);

        await capture.ManualCapture();

        Assert.Empty(calls);
        Assert.Empty(AntigravityAccounts.Load(session));
        Assert.Equal(AntigravityAutoCapture.SettingsUnavailable, capture.LastErrorCode);
        Assert.False(File.Exists(paths.Local));
        Assert.Equal(before, Bytes(paths.Roaming));
    }

    private static AntigravityAutoCapture.Io RecordingIo(ConcurrentQueue<string> calls) => new(
        Marker: () => { calls.Enqueue("marker"); return "133000000000000001"; },
        AutoCapture: _ => { calls.Enqueue("auto"); return new AntigravityAutoCaptureResult("captured", KeyA, "a@example.com"); },
        Capture: () => { calls.Enqueue("capture"); return new AntigravityAccount(KeyA, "a@example.com"); },
        Remove: _ => calls.Enqueue("remove"),
        Install: () => { calls.Enqueue("install"); return true; });

    /// <summary>Review D2: a save that fails at disk time (here the folder
    /// cannot be created: its name is taken by a file) stops the store from
    /// counting as persisting for the rest of the session.</summary>
    [Fact]
    public void ASaveThatFailsAtDiskTimeStopsPersistingForTheSession()
    {
        var paths = new Paths();
        Directory.CreateDirectory(paths.Dir);
        var blocker = Path.Combine(paths.Dir, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var store = new SettingsStore(Path.Combine(blocker, "antigravity.json"));
        Assert.True(store.Persists, "control: nothing has failed yet");

        store.SetBool("k", true);

        Assert.False(store.Persists);
        Assert.True(store.GetBool("k", false), "memory keeps the value");
    }

    /// <summary>Review D2 through the move: its one save fails, so the session
    /// store does not persist and Capture is refused with no core call.</summary>
    [Fact]
    public async Task AMoveWhoseSaveFailsLeavesCaptureRefused()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var blocker = Path.Combine(paths.Dir, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");

        var local = AntigravityLocalSettings.Open(Path.Combine(blocker, "antigravity.json"), roaming, OnlyAHere, NoLog);
        Assert.False(local.Persists);
        var calls = new ConcurrentQueue<string>();
        var capture = new AntigravityAutoCapture(RecordingIo(calls), local);

        await capture.ManualCapture();

        Assert.Empty(calls);
        Assert.Equal(AntigravityAutoCapture.SettingsUnavailable, capture.LastErrorCode);
    }

    /// <summary>Review D3, the failed-load half of F1: a local file another
    /// process holds with no sharing at launch cannot be loaded, so the store
    /// does not persist, Capture is refused with no core call, and the file is
    /// left as it was.</summary>
    [Fact]
    public async Task ALocalFileThatCannotBeLoadedAtLaunchRefusesCapture()
    {
        var paths = new Paths();
        var roaming = Roaming(paths, KeyA);
        var first = AntigravityLocalSettings.Open(paths.Local, roaming, OnlyAHere, NoLog);
        Assert.True(first.Persists);
        var moved = Bytes(paths.Local);
        var calls = new ConcurrentQueue<string>();
        SettingsStore local;
        AntigravityAutoCapture capture;
        using (new FileStream(paths.Local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            local = AntigravityLocalSettings.Open(paths.Local, new SettingsStore(paths.Roaming), OnlyAHere, NoLog);
            capture = new AntigravityAutoCapture(RecordingIo(calls), local);
        }

        Assert.True(local.LoadFailed, "fixture: the locked file did not load");
        Assert.False(local.Persists);

        await capture.ManualCapture();

        Assert.Empty(calls);
        Assert.Equal(AntigravityAutoCapture.SettingsUnavailable, capture.LastErrorCode);
        Assert.Equal(moved, Bytes(paths.Local));
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
