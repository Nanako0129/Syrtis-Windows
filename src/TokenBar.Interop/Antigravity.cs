namespace TokenBar.Interop;

// Captured Antigravity accounts (ctb.h tb_antigravity_*). Key = 64 lowercase
// hex derived from the Google account id, never shown; Label = the account's
// email or a fallback. No secret ever crosses this boundary.

public sealed record AntigravityAccount(string Key, string Label);

/// <summary><see cref="Status"/> is <c>captured</c>, <c>unchanged</c> (both
/// with key and label) or <c>skipped_removed</c>.</summary>
public sealed record AntigravityAutoCaptureResult(string Status, string? Key = null, string? Label = null);

public sealed record AntigravityMarker(string Marker);

public sealed record AntigravityRemoved(bool Removed);
