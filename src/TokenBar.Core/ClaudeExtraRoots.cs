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

    /// <summary>Most directories Rust's registries take (the same cap).</summary>
    public const int MaxDirs = 8;

    /// <summary>Why the picker refuses <paramref name="path"/> before saving,
    /// or null, so a path the registries would refuse anyway never reaches the
    /// list. Mirrors the native rules: anything but an absolute drive path
    /// (<c>X:\</c> or <c>X:/</c>; UNC, WSL, rooted and drive-relative paths); a
    /// drive root; the profile folder itself; the primary's <c>.claude</c>,
    /// anything under it or any folder above it (security review R2); a
    /// duplicate; a ninth folder. Rust stays authoritative for everything else
    /// (components, reserved names).</summary>
    public static string? UiRejection(string path, IReadOnlyList<string> existing, string? userProfile)
    {
        if (!IsDrivePath(path))
        {
            return "unsupportedPath";
        }

        var key = Fold(path);
        if (key.Length == 2)
        {
            return "rootDirectory";
        }

        if (!string.IsNullOrEmpty(userProfile))
        {
            var home = Fold(userProfile);
            if (key == home)
            {
                return "homeDirectory";
            }

            var primary = home + "\\.claude";
            if (key == primary
                || key.StartsWith(primary + "\\", StringComparison.Ordinal)
                || primary.StartsWith(key + "\\", StringComparison.Ordinal))
            {
                return "defaultConfigDir";
            }
        }

        if (existing.Any(dir => Fold(dir) == key))
        {
            return "duplicate";
        }

        return existing.Count >= MaxDirs ? "limitExceeded" : null;
    }

    /// <summary>The native normalize's shape test on the raw string: a drive
    /// letter, a colon and a separator (<c>X:\</c> or <c>X:/</c>). Everything
    /// else (UNC, WSL, rooted, drive-relative) is refused by both registries.
    /// <c>ClaudeRootsNativeTests.UiRulesAgreeWithTheNativeSetter</c> checks a
    /// fixed set of shape cases against the real setter; it is a sample, not
    /// a proof that the two rules match.</summary>
    public static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>Whether Settings may check that <paramref name="dir"/>
    /// exists: only an absolute drive path. Anything else is refused by the
    /// registries anyway and already shows its reason; a drive-relative or
    /// rooted path would resolve against some current directory rather than
    /// name a folder; and on a Windows 11
    /// machine without WSL, touching <c>\\wsl.localhost</c> starts a WSL
    /// download and install that ends in a reboot (observed on the 188 test
    /// host through Explorer's picker; whether a plain stat does the same is
    /// unverified, so no such path is touched at all).</summary>
    public static bool MayCheckExists(string dir) => IsDrivePath(dir);

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
    /// and its scans are exactly what they were before this feature). Never
    /// faults: see <see cref="Observe"/>.</summary>
    public static Task LaunchPush { get; private set; } = Task.CompletedTask;

    /// <summary>Queue the launch push (<see cref="ClaudeRootsPush.Launch"/>).</summary>
    public static void StartLaunch(ClaudeRootsPusher pusher) =>
        LaunchPush = Observe(pusher.Request(launch: true), Log);

    private static volatile bool s_launchTimedOut;

    /// <summary>Whether every reader <paramref name="push"/> affects already
    /// waited for it, so the dashboard need not refresh for it: the launch
    /// push, unless a reader gave up waiting first (then the refresh is what
    /// brings the saved roots in).</summary>
    public static bool ReadersAlreadyWaited(ClaudeRootsPush push) => push.Launch && !s_launchTimedOut;

    /// <summary><paramref name="push"/>, logged once if it failed (type only)
    /// and never faulting, so every later reader waits on a finished task and
    /// writes nothing.</summary>
    public static Task Observe(Task push, Action<string> log) =>
        push.ContinueWith(
            done =>
            {
                if (done.IsFaulted)
                {
                    log($"claudeRoots launch push failed; using the current context: {done.Exception?.InnerException?.GetType().Name}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>How long a background reader waits for the launch push.
    /// ponytail: a fixed guess, not measured; the push is two setters and one
    /// context capture. A slower push only means that read uses the context
    /// that was current; the dashboard then refreshes when the push lands
    /// (<see cref="ReadersAlreadyWaited"/>).</summary>
    public static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(10);

    /// <summary>Block a background reader until the launch push has landed.
    /// Called by the graph coordinator's scans, the snapshot's source id
    /// (security review R4) and the shared quota fetch; the hourly, agents,
    /// window and live-trace lanes do not wait (they run after a graph
    /// publication or are a trailing live rate). On a timeout it returns and
    /// the reader carries on with the current context. Never call on the UI
    /// thread.</summary>
    public static void AwaitLaunch(Task launch, TimeSpan timeout, Action<string> log)
    {
        if (!launch.Wait(timeout))
        {
            s_launchTimedOut = true;
            log("claudeRoots launch push timed out; using the current context");
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
    IReadOnlyDictionary<int, string> Rejected,
    bool Launch);

/// <summary>
/// One serialized worker for both full-replace setters (security review R7):
/// requests coalesce, at most one push runs at a time, and each push reads the
/// persisted list when it RUNS, not when it was requested — so the last saved
/// list always wins, however the setters' completions would have interleaved.
/// Order inside a push: config directories, then the scan roots of the
/// directories that registry accepted. The two registries always end on the
/// same directories: one with a refused scan root is taken out of both
/// (no card without its usage, no half-scanned directory), and a scan setter that
/// throws puts the config registry back to the last list both accepted.
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
    // Touched only by the worker loop. A fresh process's registries are empty.
    private IReadOnlyList<string> _applied = [];
    private bool _nextIsLaunch;

    /// <summary>Raised after every push that reached both setters.</summary>
    public event Action<ClaudeRootsPush>? Pushed;

    public ClaudeRootsPush? Last { get; private set; }

    /// <summary>Queue a push. The returned task completes when a push that
    /// started after this call finishes, and is faulted if that push threw.
    /// Requests made while a push runs share the one push after it.</summary>
    public Task Request(bool launch = false)
    {
        lock (_gate)
        {
            // A launch request is the process's first, so nothing coalesces
            // into it; any later request makes the shared push a normal one.
            _nextIsLaunch = _next is null && launch;
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
            bool launch;
            lock (_gate)
            {
                if (_next is null)
                {
                    _running = false;
                    return;
                }

                waiter = _next;
                launch = _nextIsLaunch;
                _next = null;
            }

            try
            {
                PushOnce(launch);
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

    private void PushOnce(bool launch)
    {
        var dirs = load();
        var rejected = new Dictionary<int, string>();
        var config = setConfigDirs(dirs);
        foreach (var rejection in config.Rejected)
        {
            rejected[rejection.Index] = rejection.Reason;
        }

        var accepted = Enumerable.Range(0, dirs.Count).Where(i => !rejected.ContainsKey(i)).ToList();
        RootsResult scan;
        try
        {
            scan = setScanRoots(ClaudeExtraRoots.ScanRoots(accepted.Select(i => dirs[i])));
        }
        catch
        {
            // On error the scan registry is unchanged; match it. Best effort:
            // the original failure is the one reported.
            try
            {
                setConfigDirs(_applied);
            }
            catch (Exception ex)
            {
                log($"claudeRoots rollback failed: {ex.GetType().Name}");
            }

            throw;
        }

        foreach (var rejection in scan.Rejected)
        {
            // Two roots per accepted directory, in order.
            var dir = accepted[rejection.Index / 2];
            rejected.TryAdd(dir, rejection.Reason);
        }

        List<string> both = [.. Enumerable.Range(0, dirs.Count).Where(i => !rejected.ContainsKey(i)).Select(i => dirs[i])];
        if (both.Count != accepted.Count)
        {
            // A refused root can be one of a directory's two: drop the
            // directory from both registries, not only from the config one,
            // or its other root keeps counting toward the totals.
            setScanRoots(ClaudeExtraRoots.ScanRoots(both));
            setConfigDirs(both);
        }

        _applied = both;
        var push = new ClaudeRootsPush(dirs, rejected, launch);
        Last = push;
        Pushed?.Invoke(push);
    }
}
