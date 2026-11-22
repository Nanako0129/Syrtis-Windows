namespace TokenBar.Interop;

// Device-aware pages (tb_*_devices, ctb.h; macOS port: RemoteAgent.swift).

/// <summary>Which sources a page folds: this PC and paired producers
/// (<c>ProducerStatic</c> of <see cref="RemotePeer"/>, 64 lowercase hex).</summary>
public sealed record DeviceSelection(bool Local, IReadOnlyList<string> Peers);

/// <summary><c>Cached</c>: no connection. <c>Refresh</c>: each selected
/// producer is pulled.</summary>
public enum DeviceFetchMode
{
    Cached,
    Refresh,
}

/// <summary>One <c>devices</c> row. Producer strings (Label, client names)
/// are the owner's free text: show as plain text only. Defaults as macOS: a
/// row without <c>costIncomplete</c> is false, without <c>everAnswered</c>
/// true (the local row carries neither).</summary>
public sealed record RemoteDevice(
    string Id,
    string State,
    string? Label = null,
    long? LastSuccessAt = null,
    string? Reason = null,
    IReadOnlyList<DeviceClientTotal>? Totals = null,
    string? Address = null,
    bool CostIncomplete = false,
    bool EverAnswered = true);

public sealed record DeviceClientTotal(string Client, long Messages, long Tokens, double CostUsd);

/// <summary>A page payload with devices folded in, plus the device rows.
/// Deliberately not the page's own type, so a mix-up fails to compile: it
/// must never reach the dashboard snapshot or the tray.</summary>
public sealed record DeviceAwarePayload<T>(
    T Payload,
    IReadOnlyList<RemoteDevice> Devices,
    bool DuplicateWarning,
    bool Saturated,
    bool RemoteRangeLimited);

/// <summary>The device keys of the same <c>data</c> object the page body is
/// decoded from.</summary>
internal sealed record DeviceOverlay(
    IReadOnlyList<RemoteDevice> Devices,
    bool DuplicateWarning,
    bool Saturated,
    bool RemoteRangeLimited);

public static class DeviceVocabulary
{
    public static readonly IReadOnlyList<string> States =
        ["local", "fresh", "lastGood", "stale", "unavailable", "overLimit", "refused", "invalidReply", "incompatible"];

    public static readonly IReadOnlyList<string> Reasons =
        ["unreachable", "timeout", "busy", "stopped", "error"];
}
