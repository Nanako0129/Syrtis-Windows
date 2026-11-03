using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The C#↔Rust seam for Cursor sync (CMP-W W1, macOS 13b): the JSON the
/// production C# code builds goes through the core's own
/// <c>tb_set_cursor_sync</c> rule and must be accepted. The native side is the
/// test-support build (<c>tb_core_ffi_test_support.dll</c>, see the test
/// project's BuildTbNativeTestSupport target), whose
/// <c>tb_test_set_cursor_sync</c> takes the roaming and local data parents as
/// arguments, so neither the real <c>%APPDATA%</c> nor <c>%LOCALAPPDATA%</c> is
/// touched. No test here (or anywhere) pushes through the shipping DLL. Only
/// on Windows: elsewhere the core has no secure storage and these return.
/// </summary>
public class CursorSyncSeamTests
{
    private static class TestSupport
    {
        private const string Lib = "tb_core_ffi_test_support";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint tb_test_set_cursor_sync(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string json,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string roaming,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string local);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void tb_free(nint p);

        public static CursorSyncConfig SetCursorSync(string json, string roaming, string local)
        {
            var p = tb_test_set_cursor_sync(json, roaming, local);
            try
            {
                return TbCore.DecodeEnvelope<CursorSyncConfig>(Marshal.PtrToStringUTF8(p)!);
            }
            finally
            {
                tb_free(p);
            }
        }
    }

    private sealed class Bases : IDisposable
    {
        public Bases()
        {
            Root = Path.Combine(Path.GetTempPath(), "tbseam-" + Guid.NewGuid().ToString("N")[..8]);
            Roaming = Directory.CreateDirectory(Path.Combine(Root, "Roaming")).FullName;
            Local = Directory.CreateDirectory(Path.Combine(Root, "Local")).FullName;
        }

        public string Root { get; }

        public string Roaming { get; }

        public string Local { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The production request builder with an added profile: the
    /// core registers its id, under the overridden local base (WB5), and an
    /// off push is accepted and cleans up. Mutations this catches:
    /// <see cref="CursorSyncRequest.Json"/> without <c>profiles</c>, or the old
    /// two-key shape (the id never comes back); a test-support override that
    /// ignores the local base (the profiles dir is not under it).</summary>
    [Fact]
    public void TheProductionRequestIsAcceptedAndTheAddedIdComesBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var bases = new Bases();
        var id = CursorProfileStore.NewId();
        // An absent folder is accepted: it reports its own state at sync time.
        var root = Path.Combine(bases.Root, "CursorProfileTest");
        var on = TestSupport.SetCursorSync(
            CursorSyncRequest.Json(true, false, [new CursorProfile(id, root)]), bases.Roaming, bases.Local);
        Assert.True(on.Enabled);
        Assert.Equal(["default", id], on.Profiles.Select(p => p.Id));
        Assert.Empty(on.Rejected);
        Assert.StartsWith(bases.Roaming, on.Dir, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(on.ProfilesDir);
        Assert.StartsWith(bases.Local, on.ProfilesDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(on.ProfilesDir), "the local profiles dir was not created");
        Assert.DoesNotContain(root, JsonSerializer.Serialize(on), StringComparison.OrdinalIgnoreCase);

        var off = TestSupport.SetCursorSync(CursorSyncRequest.Json(false, false, []), bases.Roaming, bases.Local);
        Assert.False(off.Enabled);
        Assert.Empty(off.Profiles);
    }

    /// <summary>The exact JSON the controller pushes (captured through its
    /// I/O seam) is accepted by the core, on and off.</summary>
    [Fact]
    public async Task TheControllersPushesAreAcceptedByTheCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pushed = new ConcurrentQueue<string>();
        var io = new CursorSyncController.Io(
            json =>
            {
                pushed.Enqueue(json);
                return new CursorSyncConfig(false, null, false, 0, [], [], false, []);
            },
            _ => throw new InvalidOperationException("no sync in this test"),
            () => false);
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "settings.json"));
        var controller = new CursorSyncController(io, store, ["Syrtis.exe"], interval: TimeSpan.FromHours(1));
        Assert.True(await controller.TurnOn(() => Task.FromResult(true)));
        controller.TurnOff();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (pushed.Count < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }

        using var bases = new Bases();
        var results = pushed.Select(json => TestSupport.SetCursorSync(json, bases.Roaming, bases.Local)).ToArray();
        Assert.Equal([true, false], results.Select(r => r.Enabled));
        Assert.Equal(["default"], results[0].Profiles.Select(p => p.Id));
    }

    /// <summary>A refused root comes back by index with a fixed code; the push
    /// is still accepted and the folder is never echoed (WB3, WB9).</summary>
    [Fact]
    public void ARefusedRootComesBackByIndexOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var bases = new Bases();
        var good = CursorProfileStore.NewId();
        var json = CursorSyncRequest.Json(true, false,
        [
            new CursorProfile(good, Path.Combine(bases.Root, "ok")),
            new CursorProfile(CursorProfileStore.NewId(), @"\\ROOTCANARY\share\x"),
        ]);
        var on = TestSupport.SetCursorSync(json, bases.Roaming, bases.Local);
        Assert.Equal(new CursorProfileRejection(1, "unsupportedPath"), Assert.Single(on.Rejected));
        Assert.Equal(["default", good], on.Profiles.Select(p => p.Id));
        Assert.DoesNotContain("ROOTCANARY", JsonSerializer.Serialize(on), StringComparison.Ordinal);
        TestSupport.SetCursorSync(CursorSyncRequest.Json(false, false, []), bases.Roaming, bases.Local);
    }
}
