using System.Text.Json;

namespace TokenBar.Interop;

// Cursor desktop sync (ctb.h tb_set_cursor_sync / tb_cursor_sync /
// tb_cursor_present). The sync dir is chosen by the native side; this layer
// never sends a path. No token or account id ever crosses this boundary:
// State and Reason are fixed codes.

/// <summary>Result of <c>tb_set_cursor_sync</c>. <see cref="Dir"/> is the
/// directory the native side resolved while enabled, null when disabled.</summary>
public sealed record CursorSyncConfig(bool Enabled, string? Dir, bool CliTakeoverConfirmed, int RemovedFiles);

/// <summary>Result of <c>tb_cursor_sync</c>. <see cref="State"/> is one of
/// <c>ok</c>, <c>partial</c>, <c>expired</c>, <c>notSignedIn</c>,
/// <c>offline</c>, <c>error</c>, <c>disabled</c>, <c>cliPresent</c>.</summary>
public sealed record CursorSyncStatus(string State, long Events, long? LastSuccessMs, string? Reason = null);

public sealed record CursorPresence(bool Present);

public static class CursorSyncRequest
{
    /// <summary>The exact <c>tb_set_cursor_sync</c> payload. Two keys only:
    /// the native side refuses any other (a <c>dir</c> included).</summary>
    public static string Json(bool enabled, bool cliTakeoverConfirmed) =>
        JsonSerializer.Serialize(new { enabled, cliTakeoverConfirmed });
}
