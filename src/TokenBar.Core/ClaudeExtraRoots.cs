using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// The extra Claude config directories (<c>CLAUDE_CONFIG_DIR</c> accounts) the
/// user added in Settings: persistence, the UI-only path rules, and the push
/// into the native registries. Port of macOS <c>ClaudeExtraRoots.swift</c>.
/// <para>
/// Rust's <c>claude_config_dirs::normalize</c> is the authoritative path rule
/// (drive paths only, the primary's own <c>.claude</c> refused); this side adds
/// macOS's UI rules only, so the picker can refuse the obvious mistakes before
/// anything is saved.
/// </para>
/// </summary>
public static class ClaudeExtraRoots
{
    /// <summary>Same key and shape as macOS: a string holding a JSON array of
    /// paths. Paths only, never a token.</summary>
    public const string Key = "tokenbar.claude.extraConfigDirs";

    public static IReadOnlyList<string> Load(SettingsStore store)
    {
        var raw = store.GetString(Key);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw)?
                .Where(dir => !string.IsNullOrEmpty(dir)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(SettingsStore store, IReadOnlyList<string> dirs) =>
        store.SetString(Key, JsonSerializer.Serialize(dirs));

    /// <summary>Windows paths are case-insensitive and take either separator;
    /// the same fold as Rust's <c>duplicate_key</c>.</summary>
    public static string Fold(string path) =>
        path.TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

    /// <summary>Why the picker refuses <paramref name="path"/> before saving,
    /// or null. The profile folder itself would scan the whole profile; its
    /// <c>.claude</c> is the primary account (security review R2), which Rust
    /// refuses too. Both compared on the folded form.</summary>
    public static string? UiRejection(string path, IReadOnlyList<string> existing, string? userProfile)
    {
        var key = Fold(path);
        if (!string.IsNullOrEmpty(userProfile))
        {
            var home = Fold(userProfile);
            if (key == home)
            {
                return "homeDirectory";
            }

            if (key == home + "\\.claude")
            {
                return "defaultConfigDir";
            }
        }

        return existing.Any(dir => Fold(dir) == key) ? "duplicate" : null;
    }

    /// <summary>The transcript roots of each directory, as macOS
    /// <c>ClaudeExtraRoots.expand</c>: <c>projects</c> and <c>transcripts</c>.
    /// Backslash-joined: the registry takes Windows drive paths only.</summary>
    public static IReadOnlyList<string> ScanRoots(IEnumerable<string> dirs) =>
        [.. dirs.SelectMany(dir =>
        {
            var root = dir.TrimEnd('\\', '/');
            return new[] { root + "\\projects", root + "\\transcripts" };
        })];

    public const string ClientId = "claude";

    /// <summary>The extra Claude accounts whose local usage may be attributed:
    /// the config-directory cards in <paramref name="quota"/>, by the key the
    /// native registry put on them. Trust boundary: a key passed to
    /// <c>tb_window_usage</c> comes only from here, never from settings or
    /// user input, so the native side is only ever asked about a directory it
    /// registered itself (an unregistered key is an error there, and the card
    /// stays unattributed). Claude Desktop has no local scan.</summary>
    public static IReadOnlyList<string> AttributableAccountKeys(AgentUsagePayload? quota) =>
        [.. (quota?.Agents ?? [])
            .Where(card => card.ClientId == ClientId)
            .Select(card => card.Account.AccountKey)
            .OfType<string>()
            .Where(key => key != AccountLabel.ClaudeDesktopKey)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The live pusher for this process; set once at launch.</summary>
    public static ClaudeRootsPusher? Shared { get; set; }

    /// <summary>The launch push, or a completed task when nothing was saved
    /// (a fresh process's registries are already empty, so it pushes nothing
    /// and its scans are exactly what they were before this feature).</summary>
    public static Task LaunchPush { get; set; } = Task.CompletedTask;

    /// <summary>How long a background reader waits for the launch push.
    /// ponytail: a fixed guess, not measured; the push is two setters and one
    /// context capture. A slower push only means that read uses the context
    /// that was current, and the push's own refresh corrects it.</summary>
    public static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(10);

    /// <summary>Block a background reader (graph, snapshot, quota) until the
    /// launch push has landed, so the first scan, the first quota fetch and the
    /// snapshot's source id (security review R4) all see the saved roots. On a
    /// timeout or a failed push it returns and the reader carries on with the
    /// current context. Never call on the UI thread.</summary>
    public static void AwaitLaunch(Task launch, TimeSpan timeout, Action<string> log)
    {
        try
        {
            if (!launch.Wait(timeout))
            {
                log("claudeRoots launch push timed out; using the current context");
            }
        }
        catch (AggregateException ex)
        {
            log($"claudeRoots launch push failed; using the current context: {ex.InnerException?.GetType().Name}");
        }
    }

    public static void AwaitLaunch() => AwaitLaunch(LaunchPush, LaunchWait, Log);

    /// <summary>Where this feature logs; the app points it at DevLog. Only
    /// fixed text and exception type names are ever written (review R6).</summary>
    public static Action<string> Log { get; set; } = _ => { };
}

/// <summary>Outcome of one push: the directory list it read, and the reason
/// code for each directory the registries refused (by list index).</summary>
public sealed record ClaudeRootsPush(
    IReadOnlyList<string> Dirs,
    IReadOnlyDictionary<int, string> Rejected);

/// <summary>
/// One serialized worker for both full-replace setters (security review R7):
/// requests coalesce, at most one push runs at a time, and each push reads the
/// persisted list when it RUNS, not when it was requested — so the last saved
/// list always wins, however the setters' completions would have interleaved.
/// Order inside a push: config directories, then the scan roots of the
/// directories that registry accepted.
/// </summary>
public sealed class ClaudeRootsPusher(
    Func<IReadOnlyList<string>> load,
    Func<IReadOnlyList<string>, RootsResult> setConfigDirs,
    Func<IReadOnlyList<string>, RootsResult> setScanRoots,
    Action<string> log)
{
    private readonly object _gate = new();
    private bool _running;
    private TaskCompletionSource? _next;

    /// <summary>Raised after every push that reached both setters.</summary>
    public event Action<ClaudeRootsPush>? Pushed;

    public ClaudeRootsPush? Last { get; private set; }

    /// <summary>Queue a push. The returned task completes when a push that
    /// started after this call finishes, and is faulted if that push threw.
    /// Requests made while a push runs share the one push after it.</summary>
    public Task Request()
    {
        lock (_gate)
        {
            _next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _next.Task;
            if (!_running)
            {
                _running = true;
                _ = Task.Run(Loop);
            }

            return task;
        }
    }

    private void Loop()
    {
        while (true)
        {
            TaskCompletionSource waiter;
            lock (_gate)
            {
                if (_next is null)
                {
                    _running = false;
                    return;
                }

                waiter = _next;
                _next = null;
            }

            try
            {
                PushOnce();
                waiter.SetResult();
            }
            catch (Exception ex)
            {
                // Type only: a native or IO message can name the directory.
                log($"claudeRoots push failed: {ex.GetType().Name}");
                waiter.SetException(ex);
            }
        }
    }

    private void PushOnce()
    {
        var dirs = load();
        var rejected = new Dictionary<int, string>();
        var config = setConfigDirs(dirs);
        foreach (var rejection in config.Rejected)
        {
            rejected[rejection.Index] = rejection.Reason;
        }

        var accepted = Enumerable.Range(0, dirs.Count).Where(i => !rejected.ContainsKey(i)).ToList();
        var scan = setScanRoots(ClaudeExtraRoots.ScanRoots(accepted.Select(i => dirs[i])));
        foreach (var rejection in scan.Rejected)
        {
            // Two roots per accepted directory, in order.
            var dir = accepted[rejection.Index / 2];
            rejected.TryAdd(dir, rejection.Reason);
        }

        var push = new ClaudeRootsPush(dirs, rejected);
        Last = push;
        Pushed?.Invoke(push);
    }
}
