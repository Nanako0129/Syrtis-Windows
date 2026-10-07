using System.Runtime.InteropServices;

namespace TokenBar.Interop;

// P/Invoke surface for crates/tb_core_ffi (contract: include/ctb.h).
// "tb_core_ffi" resolves to tb_core_ffi.dll on Windows and
// libtb_core_ffi.dylib on macOS (the macOS-side test loop) via default
// runtime probing; the native binary is copied next to the assembly by
// src/Directory.Build.targets.
//
// Every entry point returns a heap NUL-terminated JSON string that must be
// released with tb_free exactly once — TbCore.TakeString is the single legal
// consumer. `year` may be null (all time) or a 4-digit year string.
internal static partial class NativeMethods
{
    private const string Lib = "tb_core_ffi";

    [LibraryImport(Lib)]
    internal static partial nint tb_probe();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_graph(string? year);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_graph_local_first(string? year);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_refresh_graph(string? year);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_model_report(string? year);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_hourly_report(string? year, string? clients);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_agents_report(string? year, string? clients);

    [LibraryImport(Lib)]
    internal static partial nint tb_source_context_id();

    [LibraryImport(Lib)]
    internal static partial nint tb_filter_parity_probe();

    [LibraryImport(Lib)]
    internal static partial nint tb_usage_trace(long windowSecs);

    [LibraryImport(Lib)]
    internal static partial nint tb_tokens_per_min();

    [LibraryImport(Lib)]
    internal static partial nint tb_agent_usage();

    [LibraryImport(Lib)]
    internal static partial nint tb_quota_history();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_window_usage(string? accountKey, long fromMs, long untilMs);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_set_claude_config_dirs(string json);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_set_extra_scan_paths(string json);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_validate_claude_config_dir(string json);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_set_keychain_consent(string json);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_set_antigravity_accounts(string json);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_set_antigravity_binding(string json);

    [LibraryImport(Lib)]
    internal static partial nint tb_antigravity_capture();

    [LibraryImport(Lib)]
    internal static partial nint tb_antigravity_login_marker();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_antigravity_auto_capture(string removedKeysJson);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint tb_antigravity_remove(string key);

    [LibraryImport(Lib)]
    internal static partial void tb_free(nint ptr);
}
