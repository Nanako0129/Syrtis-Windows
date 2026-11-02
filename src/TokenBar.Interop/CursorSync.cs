using System.Text.Json;

namespace TokenBar.Interop;

// Cursor desktop sync (ctb.h tb_set_cursor_sync / tb_cursor_sync /
// tb_validate_cursor_profile / tb_cursor_present). Every Syrtis dir is chosen
// by the native side; this layer never sends one. An added Cursor profile
// crosses as (uppercase UUID, the --user-data-dir folder) on the way in only:
// no result echoes a folder, and no token or account id ever crosses this
// boundary (State and Reason are fixed codes).

/// <summary>One added Cursor profile as the request carries it.</summary>
public sealed record CursorProfile(string Id, string Root);

/// <summary>A profile the core registered: <c>default</c> or an added id.</summary>
public sealed record CursorProfileId(string Id);

/// <summary>An added profile's folder the core refused, by its index in the
/// request, with a fixed reason code. The other entries still register.</summary>
public sealed record CursorProfileRejection(int Index, string Reason);

/// <summary>Result of <c>tb_set_cursor_sync</c>. <see cref="Dir"/> is the
/// default profile's dir the native side resolved while enabled, null when
/// disabled; <see cref="ProfilesDir"/> the local dir added profiles live
/// under (null without any). <see cref="CleanupFailed"/> is an on push's
/// non-fatal cleanup of profiles no longer listed; an off push's failed
/// cleanup is the <c>cleanupFailed</c> error instead.</summary>
public sealed record CursorSyncConfig(
    bool Enabled,
    string? Dir,
    bool CliTakeoverConfirmed,
    int RemovedFiles,
    IReadOnlyList<CursorProfileId> Profiles,
    IReadOnlyList<CursorProfileRejection> Rejected,
    bool CleanupFailed,
    IReadOnlyList<string> CleanupFailedProfiles,
    string? ProfilesDir = null);

/// <summary>One profile's outcome in a sync round. <see cref="State"/> is one
/// of the round states plus <c>duplicate</c> (an earlier profile,
/// <see cref="DuplicateOf"/>, already synced the same account).</summary>
public sealed record CursorProfileStatus(
    string Id, string State, long Events, long? LastSuccessMs,
    string? Reason = null, string? DuplicateOf = null);

/// <summary>Result of <c>tb_cursor_sync</c>. <see cref="State"/> is the
/// round's headline, one of <c>ok</c>, <c>partial</c>, <c>expired</c>,
/// <c>notSignedIn</c>, <c>offline</c>, <c>error</c>, <c>disabled</c>,
/// <c>cliPresent</c>. <see cref="Changed"/>: some profile wrote its file this
/// round, whatever the headline says (the dashboard refreshes on it).</summary>
public sealed record CursorSyncStatus(
    string State, long Events, long? LastSuccessMs, bool Changed,
    IReadOnlyList<CursorProfileStatus> Profiles, string? Reason = null);

/// <summary>Result of <c>tb_validate_cursor_profile</c>: whether a folder can
/// be added, else a fixed reason code. Never a path.</summary>
public sealed record CursorProfileValidation(bool Accepted, string? Reason = null);

public sealed record CursorPresence(bool Present);

public static class CursorSyncRequest
{
    /// <summary>The exact <c>tb_set_cursor_sync</c> payload:
    /// <c>{"enabled","cliTakeoverConfirmed","profiles":[{"id","root"}]}</c>.
    /// The native side refuses any other key (a <c>dir</c> included).</summary>
    public static string Json(bool enabled, bool cliTakeoverConfirmed, IReadOnlyList<CursorProfile> profiles) =>
        JsonSerializer.Serialize(new
        {
            enabled,
            cliTakeoverConfirmed,
            profiles = profiles.Select(p => new { id = p.Id, root = p.Root }),
        });

    /// <summary>The <c>tb_validate_cursor_profile</c> payload.</summary>
    public static string ValidationJson(string root, IReadOnlyList<string> existing) =>
        JsonSerializer.Serialize(new { root, existing });
}
