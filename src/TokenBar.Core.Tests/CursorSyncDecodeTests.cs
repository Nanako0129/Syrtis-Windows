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
            """{"ok":true,"data":{"enabled":true,"dir":"C:\\Users\\x\\AppData\\Roaming\\com.nyanako.tokenbar\\cursor-cache","profilesDir":"C:\\Users\\x\\AppData\\Local\\com.nyanako.tokenbar\\cursor-profiles","profiles":[{"id":"default"},{"id":"0A1B2C3D-0000-4000-8000-00000000000A"}],"rejected":[{"index":1,"reason":"unsupportedPath"}],"cliTakeoverConfirmed":false,"removedFiles":1,"cleanupFailed":true,"cleanupFailedProfiles":["0A1B2C3D-0000-4000-8000-00000000000B"]}}""");
        Assert.True(on.Enabled);
        Assert.Equal(@"C:\Users\x\AppData\Roaming\com.nyanako.tokenbar\cursor-cache", on.Dir);
        Assert.Equal(@"C:\Users\x\AppData\Local\com.nyanako.tokenbar\cursor-profiles", on.ProfilesDir);
        Assert.Equal(["default", "0A1B2C3D-0000-4000-8000-00000000000A"], on.Profiles.Select(p => p.Id));
        Assert.Equal(new CursorProfileRejection(1, "unsupportedPath"), Assert.Single(on.Rejected));
        Assert.True(on.CleanupFailed);
        Assert.Equal(["0A1B2C3D-0000-4000-8000-00000000000B"], on.CleanupFailedProfiles);
        Assert.Equal(1, on.RemovedFiles);

        var off = TbCore.DecodeEnvelope<CursorSyncConfig>(
            """{"ok":true,"data":{"enabled":false,"dir":null,"profilesDir":null,"profiles":[],"rejected":[],"cliTakeoverConfirmed":true,"removedFiles":2,"cleanupFailed":false,"cleanupFailedProfiles":[]}}""");
        Assert.False(off.Enabled);
        Assert.Null(off.Dir);
        Assert.Empty(off.Profiles);
        Assert.Equal(2, off.RemovedFiles);
    }

    /// <summary>The old two-key result (no profiles) is a decode failure, not
    /// a silent empty list.</summary>
    [Fact]
    public void ConfigWithoutProfilesFailsLoudly()
    {
        Assert.ThrowsAny<Exception>(() => TbCore.DecodeEnvelope<CursorSyncConfig>(
            """{"ok":true,"data":{"enabled":true,"dir":null,"cliTakeoverConfirmed":false,"removedFiles":0}}"""));
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
            $$$"""{"ok":true,"data":{"state":"{{{state}}}","events":0,"lastSuccessMs":null,"changed":false,"profiles":[]}}""");
        Assert.Equal(state, status.State);
        Assert.False(status.Changed);
        Assert.Empty(status.Profiles);
    }

    [Fact]
    public void StatusCarriesEventsLastSuccessAndReason()
    {
        var ok = TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"state":"ok","events":3542,"lastSuccessMs":1788256800000,"changed":true,"profiles":[{"id":"default","state":"ok","events":3542,"lastSuccessMs":1788256800000},{"id":"0A1B2C3D-0000-4000-8000-00000000000A","state":"duplicate","events":0,"lastSuccessMs":null,"duplicateOf":"default"},{"id":"0A1B2C3D-0000-4000-8000-00000000000B","state":"error","events":0,"lastSuccessMs":null,"reason":"db_foreign_owner"}],"cleanupFailedProfiles":["0A1B2C3D-0000-4000-8000-00000000000A"]}}""");
        Assert.Equal(("ok", 3542L, (long?)1788256800000, true), (ok.State, ok.Events, ok.LastSuccessMs, ok.Changed));
        Assert.Equal(["0A1B2C3D-0000-4000-8000-00000000000A"], ok.CleanupFailedProfiles);
        Assert.Equal(
            [
                new CursorProfileStatus("default", "ok", 3542, 1788256800000),
                new CursorProfileStatus("0A1B2C3D-0000-4000-8000-00000000000A", "duplicate", 0, null, DuplicateOf: "default"),
                new CursorProfileStatus("0A1B2C3D-0000-4000-8000-00000000000B", "error", 0, null, Reason: "db_foreign_owner"),
            ],
            ok.Profiles);

        var partial = TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"state":"partial","events":0,"lastSuccessMs":1788256800000,"changed":false,"profiles":[],"reason":"timeout"}}""");
        Assert.Equal("timeout", partial.Reason);
        Assert.Null(partial.CleanupFailedProfiles); // an older core sends none
    }

    [Fact]
    public void StatusMissingARequiredFieldFailsLoudly()
    {
        Assert.ThrowsAny<Exception>(() => TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"events":0,"lastSuccessMs":null,"changed":false,"profiles":[]}}"""));
        // `changed` is required: a status without it must not read as "nothing changed".
        Assert.ThrowsAny<Exception>(() => TbCore.DecodeEnvelope<CursorSyncStatus>(
            """{"ok":true,"data":{"state":"ok","events":0,"lastSuccessMs":null,"profiles":[]}}"""));
    }

    [Fact]
    public void ValidationDecodes()
    {
        Assert.Equal(new CursorProfileValidation(true),
            TbCore.DecodeEnvelope<CursorProfileValidation>("""{"ok":true,"data":{"accepted":true,"reason":null}}"""));
        Assert.Equal(new CursorProfileValidation(false, "notFixedDrive"),
            TbCore.DecodeEnvelope<CursorProfileValidation>("""{"ok":true,"data":{"accepted":false,"reason":"notFixedDrive"}}"""));
        using var doc = JsonDocument.Parse(CursorSyncRequest.ValidationJson(@"D:\p", [@"C:\q"]));
        Assert.Equal(["root", "existing"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void PresenceDecodes()
    {
        Assert.True(TbCore.DecodeEnvelope<CursorPresence>("""{"ok":true,"data":{"present":true}}""").Present);
        Assert.False(TbCore.DecodeEnvelope<CursorPresence>("""{"ok":true,"data":{"present":false}}""").Present);
    }

    /// <summary>W1 + CMP-W: the request carries the two switches and the
    /// added profiles as (id, root), never a Syrtis path.</summary>
    [Fact]
    public void RequestCarriesTheSwitchesAndTheProfiles()
    {
        using var doc = JsonDocument.Parse(CursorSyncRequest.Json(
            true, false, [new CursorProfile("0A1B2C3D-0000-4000-8000-00000000000A", @"D:\Cursor B")]));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["enabled", "cliTakeoverConfirmed", "profiles"], keys);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("cliTakeoverConfirmed").GetBoolean());
        var profile = Assert.Single(doc.RootElement.GetProperty("profiles").EnumerateArray().ToArray());
        Assert.Equal(["id", "root"], profile.EnumerateObject().Select(p => p.Name));
        Assert.Equal(@"D:\Cursor B", profile.GetProperty("root").GetString());
    }
}
