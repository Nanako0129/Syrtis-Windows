namespace TokenBar.Interop;

// Result of tb_set_claude_config_dirs / tb_set_extra_scan_paths. Indexes
// refer to the submitted list; reasons are fixed codes (ctb.h). No path ever
// crosses back, so this is safe to log or show as is.

public sealed record RootsResult(
    int RegisteredCount,
    IReadOnlyList<RootRejection> Rejected);

public sealed record RootRejection(int Index, string Reason);
