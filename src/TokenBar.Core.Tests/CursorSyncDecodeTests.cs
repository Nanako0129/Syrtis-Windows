using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The Cursor sync DTOs against the exact shapes tb_set_cursor_sync,
/// tb_cursor_sync and tb_cursor_present emit (ctb.h), and the request the
/// wrapper sends. Pure decoding: nothing here calls the native library, so
/// no sync dir, Cursor database or network is touched.
/// </summary>
public class CursorSyncDecodeTests
{
    [Fact]
    public void ConfigDecodesWithAndWithoutADir()
    {
        var on = TbCore.DecodeEnvelope<CursorSyncConfig>(
            """{"ok":true,"data":{"enabled":true,"dir":"C:\\Users\\x\\AppData\\Roaming\\com.nyanako.tokenbar\\cursor-cache","cliTakeoverConfirmed":false,"removedFiles":0}}""");
        Assert.Equal(new CursorSyncConfig(true, @"C:\Users\x\AppData\Roaming\com.nyanako.tokenbar\cursor-cache", false, 0), on);

        var off = TbCore.DecodeEnvelope<CursorSyncConfig>(
            """{"ok":true,"data":{"enabled":false,"dir":null,"cliTakeoverConfirmed":true,"removedFiles":2}}""");
        Assert.Equal(new CursorSyncConfig(false, null, true, 2), off);
    }

    [Fact]
    public void CleanupFailureIsAnErrorNotZeroRemoved()
    {
        var ex = Assert.Throws<TbCoreException>(() => TbCore.DecodeEnvelope<CursorSyncConfig>(
            """{"ok":false,"err":"cleanupFailed"}"""));
        Assert.Equal("cleanupFailed", ex.Message);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("partial")]
    [InlineData("expired")]
    [InlineData("notSignedIn")]
    [InlineData("offline")]
    [InlineData("error")]
    [InlineData("disabled")]
    [InlineData("cliPresent")]
    public void StatusDecodesEveryState(string state)
    {
        var status = TbCore.DecodeEnvelope<CursorSyncStatus>(
            $$$"""{"ok":true,"data":{"state":"{{{state}}}","events":0,"lastSuccessMs":null}}""");
        Assert.Equal(new CursorSyncStatus(state, 0, null), status);
    }

    [Fact]
    public void StatusCarriesEventsLastSuccessAndReason()
    {
        var ok = TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"state":"ok","events":3542,"lastSuccessMs":1788256800000}}""");
        Assert.Equal(new CursorSyncStatus("ok", 3542, 1788256800000), ok);

        var partial = TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"state":"partial","events":0,"lastSuccessMs":1788256800000,"reason":"timeout"}}""");
        Assert.Equal("timeout", partial.Reason);
    }

    [Fact]
    public void StatusMissingARequiredFieldFailsLoudly()
    {
        Assert.ThrowsAny<Exception>(() => TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"events":0,"lastSuccessMs":null}}"""));
    }

    [Fact]
    public void PresenceDecodes()
    {
        Assert.True(TbCore.DecodeEnvelope<CursorPresence>("""{"ok":true,"data":{"present":true}}""").Present);
        Assert.False(TbCore.DecodeEnvelope<CursorPresence>("""{"ok":true,"data":{"present":false}}""").Present);
    }

    /// <summary>W1: the request carries exactly two keys, never a path.</summary>
    [Fact]
    public void RequestCarriesOnlyTheTwoSwitches()
    {
        using var doc = JsonDocument.Parse(CursorSyncRequest.Json(true, false));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["enabled", "cliTakeoverConfirmed"], keys);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("cliTakeoverConfirmed").GetBoolean());
    }
}
