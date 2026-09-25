using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace TokenBar.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private TrayService? _tray;
    private FlyoutWindow? _flyout;
    private GraphRequestCoordinator? _graphCoordinator;
    private bool _started;
    private bool _startupSmokeRequested;
    private string? _startupSmokePath;
    private string? _startupSmokeError;
    private int _updateCheckStarted;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A stray UI-thread exception (a render / hover / animation callback)
        // shouldn't take the whole tray-resident app down once it's up; during
        // startup we let it propagate so a broken launch stays visible.
        UnhandledException += OnUnhandledException;

        // Before any view is built: macOS reads its language once per launch
        // and requires a relaunch to change it, and this port keeps that
        // contract rather than rebuilding every view against a swapped table.
        AppLanguage.Apply(
            AppSettings.Store,
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets"));

        var startupSmoke = ParseStartupSmokeArguments();
        _startupSmokeRequested = startupSmoke.Requested;
        _startupSmokePath = startupSmoke.Path;
        _startupSmokeError = startupSmoke.Error;

        // Tray-resident app: no window shows at launch; the tray icon owns
        // the flyout's lifetime (mirrors the macOS NSStatusItem shell).
        // Single instance: a second launch (autostart + manual, say) exits
        // quietly; the mutex lives for the process lifetime.
        _singleInstance = new Mutex(true, @"Local\TokenBar.App.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            if (_startupSmokeRequested)
            {
                Environment.ExitCode = 1;
            }
            DevLog.Write("launch: another instance is running, exiting");
            Current.Exit();
            return;
        }

        ProcessPower.EnsureNormalPriority();

        try
        {
            DevLog.Write("launch: creating graph coordinator");
            _graphCoordinator = GraphRequestCoordinator.CreateForApp(
                boost: ProcessPower.Boost);
            DevLog.Write("launch: creating flyout");
            _flyout = new FlyoutWindow(_graphCoordinator);
            DevLog.Write("launch: creating tray");
            _tray = new TrayService(_flyout, _graphCoordinator);
            DevLog.Write("launch: tray up");

            if (Environment.GetCommandLineArgs().Contains("--dump-tray-icons"))
            {
                TrayIconGallery.Dump();
            }

            // Debug flag, macOS --settings parity.
            if (Environment.GetCommandLineArgs().Contains("--settings"))
            {
                _tray.ShowSettings();
            }

            // Debug flag, same convention as the macOS --open-popover: pop the
            // flyout right away so remote verification needs no tray click.
            if (Environment.GetCommandLineArgs().Contains("--open-flyout"))
            {
                _flyout.ShowFlyout();
                DevLog.Write($"launch: flyout shown, visible={_flyout.AppWindow.IsVisible}, " +
                    $"pos={_flyout.AppWindow.Position.X},{_flyout.AppWindow.Position.Y} " +
                    $"size={_flyout.AppWindow.Size.Width}x{_flyout.AppWindow.Size.Height}");
            }

            // Debug flag, same convention as --settings / --open-flyout /
            // --graph3d. The update dialog is otherwise unreachable without a
            // Velopack-installed build AND a newer published release, which
            // makes its layout and its changelog rendering impossible to
            // inspect. The action published here only writes a log line: this
            // path cannot download, verify or hand off anything.
            if (Environment.GetCommandLineArgs().Contains("--update-dialog-demo"))
            {
                PublishDemoUpdate(_tray);
            }

            _started = true;

            // The opt-in startup probe deliberately runs through one
            // DispatcherQueue turn after both windows/services are constructed.
            // The normal launch path is untouched when the flag is absent.
            if (_startupSmokeRequested)
            {
                QueueStartupSmoke();
            }

            StartUpdateCheckOnce();

            // Dev-only 3D panel (Phase 8 Gate 0). --graph3d mounts and shows
            // it for manual inspection; --soak3d runs the device-lifecycle
            // soak (50 cycles, or --soak3d-minutes duration mode) and exits
            // with a pass/fail code.
            if (Environment.GetCommandLineArgs().Contains("--graph3d"))
            {
                _flyout.EnableGraph3D();
                _flyout.ShowFlyout();
            }

            if (Environment.GetCommandLineArgs().Contains("--soak3d"))
            {
                Soak3D.Run(_flyout);
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"launch FAILED: {ex}");
            throw;
        }
    }

    private void StartUpdateCheckOnce()
    {
        if (Interlocked.Exchange(ref _updateCheckStarted, 1) != 0)
        {
            return;
        }

        try
        {
            TrayService.CheckForUpdates = () => CheckForUpdatesAsync(userInitiated: true);
            _ = Task.Run(() => CheckForUpdatesAsync(userInitiated: false));
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
        }
    }

    private readonly object _updateCheckGate = new();
    private Task<UpdateCheckResult>? _updateCheckInFlight;

    /// <summary>The single update check. Startup fires it and drops the
    /// result; settings' "Check now" awaits it for the line it shows.
    ///
    /// One entry point rather than two, because two produced three separate
    /// races: opening settings during the startup check ran a second
    /// UpdateFlow against the same feed, and if both found a version both
    /// published — two notifications, and the second replacing the first's
    /// pending action. Guarding the publisher only covers the window after an
    /// action is taken; a concurrent check has to be prevented before it
    /// starts, which is what returning the in-flight task does.</summary>
    /// <param name="userInitiated">Whether a person asked. A skipped version is
    /// withheld from the automatic check and offered again to a manual one —
    /// Sparkle does exactly this, applying the skipped version only when the
    /// check is a background one (SUAppcastDriver.m:228,
    /// <c>background ? skippedUpdateForHost : nil</c>). Skip means "stop
    /// nagging me", not "never show me this again": without the distinction a
    /// mis-clicked Skip leaves no way back except editing settings.json.
    ///
    /// <para>A user-initiated call that joins an in-flight background check
    /// still re-offers, because the offer decision is made per caller after
    /// the shared check resolves, not inside it.</para></param>
    private Task<UpdateCheckResult> CheckForUpdatesAsync(bool userInitiated)
    {
        Task<UpdateCheckResult> shared;
        lock (_updateCheckGate)
        {
            shared = _updateCheckInFlight is { IsCompleted: false } running
                ? running
                : _updateCheckInFlight = RunUpdateCheckAsync();
        }

        // Only a manual caller needs a second pass: the background one already
        // published under its own rules inside RunUpdateCheckAsync.
        return userInitiated ? ReofferIfSkippedAsync(shared) : shared;
    }

    private async Task<UpdateCheckResult> ReofferIfSkippedAsync(
        Task<UpdateCheckResult> shared)
    {
        var result = await shared.ConfigureAwait(true);
        if (result.State != UpdateCheckState.Available
            || _lastCandidate is not { } pending
            || !string.Equals(pending.Candidate.Version, result.Version, StringComparison.Ordinal))
        {
            return result;
        }

        // Only when the shared check's own publish was suppressed by the skip.
        // Republishing unconditionally would offer every manual check twice —
        // two notifications, and the second bumping the generation, which
        // invalidates a dialog opened from the first.
        if (PendingUpdateAction.ShouldOffer(
            pending.Candidate.Version,
            AppSettings.Store.GetString(PendingUpdateAction.SkippedVersionKey),
            userInitiated: false))
        {
            return result;
        }

        _ = QueueUpdateUi(() =>
            PublishUpdate(pending.Flow, pending.Candidate, userInitiated: true));
        return result;
    }

    private (UpdateFlow Flow, UpdateCandidate Candidate)? _lastCandidate;

    private async Task<UpdateCheckResult> RunUpdateCheckAsync()
    {
        try
        {
            var flow = new UpdateFlow();
            var candidate = await flow.CheckForUpdatesAsync().ConfigureAwait(false);
            if (candidate is null)
            {
                DevLog.Write("update-check: none");
                _lastCandidate = null;
                return UpdateCheckResult.UpToDate;
            }

            // Retained so a manual caller that joined this check can re-offer a
            // skipped version without running a second UpdateFlow.
            _lastCandidate = (flow, candidate);
            _ = QueueUpdateUi(() => PublishUpdate(flow, candidate));
            return UpdateCheckResult.Available(candidate.Version);
        }
        catch (UnmanagedInstallException)
        {
            // Not a failure. Caught before the general handler so it is not
            // logged as one and so the user is not sent looking for a network
            // problem that does not exist.
            DevLog.Write("update-check: unmanaged install");
            _lastCandidate = null;
            return UpdateCheckResult.Unmanaged;
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
            _lastCandidate = null;
            return UpdateCheckResult.FromFailure(ex);
        }
    }

    /// <summary>Where the app decides <b>whether to offer</b> a found update.
    ///
    /// <para>The skip gate belongs here and nowhere else. Putting it in
    /// <see cref="RunUpdateCheckAsync"/> would suppress
    /// <c>UpdateCheckResult.Available</c>, and <c>CheckForUpdatesAsync</c> is
    /// the single entry shared by the startup check and Settings' "Check now" —
    /// so Settings would report "You are up to date." while an update existed,
    /// the exact shape <c>.github/release-notes/v0.2.2.md</c> describes as a
    /// bug. Putting it in <c>TrayService.PublishUpdate</c> would conflate
    /// "skipped" with "already downloading". A skipped version still reports
    /// honestly to a manual check; it is only not <em>offered</em>.</para>
    /// </summary>
    private void PublishUpdate(
        UpdateFlow flow, UpdateCandidate candidate, bool userInitiated = false)
    {
        try
        {
            var tray = _tray;
            if (tray is null || !tray.CanHandoff)
            {
                return;
            }

            if (!PendingUpdateAction.ShouldOffer(
                candidate.Version,
                AppSettings.Store.GetString(PendingUpdateAction.SkippedVersionKey),
                userInitiated))
            {
                DevLog.Write($"update-available: v{candidate.Version} skipped by user");
                return;
            }

            if (tray.PublishUpdate(
                new UpdateOffer(
                    candidate.Version, candidate.InstalledVersion, candidate.Notes),
                () => StartUpdateDownload(flow, candidate)))
            {
                DevLog.Write($"update-available: v{candidate.Version}");
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-check", ex);
        }
    }

    /// <summary>--update-dialog-demo. Publishes a synthetic offer through the
    /// real tray path and opens the dialog on it, so the three buttons act on a
    /// genuine PendingUpdateAction — Later leaves the menu item, Skip removes it
    /// and writes the skip key, Install takes the action — while the action
    /// itself does nothing but log.</summary>
    private static void PublishDemoUpdate(TrayService tray)
    {
        const string notes = """
            # What's new

            **This build is a demo of the update dialog.** Nothing here is real, and *Install Update* only writes a log line. This paragraph is deliberately long enough to wrap, and it is hard-wrapped in the source to exercise the soft-wrap join.
            The second source line of the same paragraph.

            ## Fixed

            - A `RichTextBlock` no longer renders `#39` as a heading
            - Ordered lists render as bullets, with the numbering lost
            - [Links keep their text](https://example.invalid/discarded) and lose the URL

            1. First
            2. Second

            ## Known issues

            **Unsigned.** Windows SmartScreen will warn on first run. Choose
            *More info* → *Run anyway*.

            | Machine | File |
            |---|---|
            | x64 | Setup.exe |
            """;
        try
        {
            // The same gate the real path applies, so a Skip taken in the demo
            // is observable on the next launch: no dialog, no menu item, one
            // log line. Clear tokenbar.update.skippedVersion from settings.json
            // to get the offer back.
            // Background semantics, so a Skip taken in the demo is observable
            // on the next launch the way a real one would be.
            if (!PendingUpdateAction.ShouldOffer(
                "9.9.9",
                AppSettings.Store.GetString(PendingUpdateAction.SkippedVersionKey),
                userInitiated: false))
            {
                DevLog.Write("update-dialog-demo: v9.9.9 skipped by user");
                return;
            }

            if (tray.PublishUpdate(
                new UpdateOffer("9.9.9", "0.0.0", notes),
                RunDemoProgress))
            {
                _ = tray.TryOpenUpdateDialog();
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-dialog-demo", ex);
        }
    }

    /// <summary>--update-dialog-demo's Install. Walks the progress states with
    /// no download, so the phase copy and the in-place switch can be looked at
    /// on a dev build — the real path needs an installed Velopack build and a
    /// published newer version, which is a fifteen-minute round trip per look.
    ///
    /// <para>Nothing is downloaded, verified or applied; the app does not
    /// quit.</para></summary>
    private static void RunDemoProgress()
    {
        DevLog.Write("update-dialog-demo: install invoked");
        _ = Task.Run(async () =>
        {
            for (var percent = 0; percent <= 100; percent += 10)
            {
                UpdateDialog.Report(UpdateDialogText.Downloading(), percent);
                await Task.Delay(250).ConfigureAwait(false);
            }

            UpdateDialog.Report(UpdateDialogText.Restarting(), null);
            // The real path stays on this phase until the process exits, which
            // in a demo is indistinguishable from a hang. Close instead.
            await Task.Delay(1500).ConfigureAwait(false);
            DevLog.Write("update-dialog-demo: the real path would restart here");
            UpdateDialog.CloseIfOpen();
        });
    }

    private void StartUpdateDownload(UpdateFlow flow, UpdateCandidate candidate)
    {
        try
        {
            _ = Task.Run(() => DownloadUpdateAsync(flow, candidate));
        }
        catch (Exception ex)
        {
            RestoreUpdateAction("update-download");
            LogUpdateFailure("update-download", ex);
        }
    }

    /// <summary>How long "Installing update…" is held on screen before the
    /// hand-off ends this process.
    ///
    /// <para>Not cosmetic padding. The hand-off is queued on the same
    /// dispatcher as the phase switch and tears the process down in the next
    /// callback, so without a pause the text is set and never reaches a frame:
    /// the first real round trip logged the two one millisecond apart, and the
    /// phase was invisible. What follows it is several seconds with no window
    /// at all — this process is gone and Velopack is applying — so this line is
    /// the only account the user gets of that gap, and it has to be seen.</para>
    /// </summary>
    private static readonly TimeSpan RestartingDwell = TimeSpan.FromMilliseconds(900);

    private async Task DownloadUpdateAsync(
        UpdateFlow flow,
        UpdateCandidate candidate)
    {
        DevLog.Write($"update-download: start v{candidate.Version}");
        UpdateDialog.Report(UpdateDialogText.Downloading(), 0);
        try
        {
            await flow.DownloadAndVerifyAsync(
                candidate,
                new Progress<int>(p => UpdateDialog.Report(UpdateDialogText.Downloading(), p)))
                .ConfigureAwait(false);
            DevLog.Write($"update-download: verified v{candidate.Version}");
            // Verification and the nuspec check are already done by the line
            // above; what remains is the hand-off, which ends this process
            // while Velopack applies the update. Nothing this process draws
            // survives that, so the only honest thing it can do is say so
            // before it goes.
            UpdateDialog.Report(UpdateDialogText.Restarting(), null);
            await Task.Delay(RestartingDwell).ConfigureAwait(false);
            if (!QueueUpdateUi(() => HandoffUpdate(flow, candidate)))
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex)
        {
            LogUpdateFailure("update-download", ex);
            _ = QueueUpdateUi(() => RestoreUpdateAction("update-download"));
        }
    }

    private void HandoffUpdate(UpdateFlow flow, UpdateCandidate candidate)
    {
        var tray = _tray;
        if (tray is null || !tray.CanHandoff)
        {
            return;
        }

        try
        {
            var quit = TrayService.QuitApp
                ?? throw new InvalidOperationException();
            DevLog.Write($"update-handoff: start v{candidate.Version}");
            if (flow.TryHandoff(
                candidate,
                () => tray.CanHandoff,
                () =>
                {
                    tray.CompleteUpdateAction();
                    quit();
                }))
            {
                DevLog.Write($"update-handoff: started v{candidate.Version}");
            }
        }
        catch (Exception ex)
        {
            RestoreUpdateAction("update-handoff");
            LogUpdateFailure("update-handoff", ex);
        }
    }

    private void RestoreUpdateAction(string stage)
    {
        try
        {
            _tray?.RestoreUpdateAction();
        }
        catch (Exception ex)
        {
            LogUpdateFailure(stage, ex);
        }
    }

    private bool QueueUpdateUi(Action action) =>
        _flyout?.DispatcherQueue.TryEnqueue(() => action()) == true;

    private static void LogUpdateFailure(string stage, Exception exception)
    {
        var type = exception.GetType().Name;
        if (type.Length > 64)
        {
            type = type[..64];
        }

        DevLog.Write($"{stage}: failed {type}");
    }

    private static (bool Requested, string? Path, string? Error)
        ParseStartupSmokeArguments()
    {
        var args = Environment.GetCommandLineArgs();
        var indexes = args
            .Select((value, index) => (value, index))
            .Where(item => string.Equals(
                item.value, "--startup-smoke", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToList();
        if (indexes.Count == 0)
        {
            return (false, null, null);
        }

        if (indexes.Count != 1)
        {
            return (true, null, "--startup-smoke must appear exactly once");
        }

        var index = indexes[0];
        if (index + 1 >= args.Length)
        {
            return (true, null, "--startup-smoke requires a sentinel path");
        }

        var path = args[index + 1];
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("--", StringComparison.Ordinal))
        {
            return (true, null, "--startup-smoke requires a non-empty sentinel path");
        }

        return (true, path, null);
    }

    private void QueueStartupSmoke()
    {
        if (_flyout is null || _tray is null)
        {
            FailStartupSmoke("startup services were not constructed");
            return;
        }

        if (!_flyout.DispatcherQueue.TryEnqueue(async () =>
            await CompleteStartupSmokeAsync(_startupSmokePath, _startupSmokeError)
                .ConfigureAwait(true)))
        {
            FailStartupSmoke("DispatcherQueue rejected the startup probe");
        }
    }

    private async Task CompleteStartupSmokeAsync(string? path, string? parseError)
    {
        try
        {
            if (parseError is not null)
            {
                throw new InvalidOperationException(parseError);
            }

            if (path is null)
            {
                throw new InvalidOperationException(
                    "--startup-smoke sentinel path is missing");
            }

            // Hard gate: await per-episode ForceCreate ticks with bounded timeout.
            await _tray!.AssertTrayReadyAsync(
                TimeSpan.FromMilliseconds(
                    TrayForceCreatePolicy.StartupSmokeTimeoutMilliseconds))
                .ConfigureAwait(true);

            WriteStartupSmokeSentinel(path);
            Environment.ExitCode = 0;
            DevLog.Write("startup-smoke: success stage=tray-ready");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            DevLog.Write($"startup-smoke: FAILED {ex.Message}");
        }
        finally
        {
            // Route shutdown through the existing tray owner so feed/timers,
            // tray resources, and owned GDI handles are released first.
            TrayService.QuitApp?.Invoke();
        }
    }

    private void FailStartupSmoke(string message)
    {
        Environment.ExitCode = 1;
        DevLog.Write($"startup-smoke: FAILED {message}");
        TrayService.QuitApp?.Invoke();
    }

    private static void WriteStartupSmokeSentinel(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException($"sentinel path already exists: {path}");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"sentinel parent directory does not exist: {path}");
        }

        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            throw new InvalidOperationException(
                "assembly informational version is missing");
        }

        var sentinel = new StartupSmokeSentinel(
            Environment.ProcessId,
            version,
            RuntimeInformation.ProcessArchitecture.ToString(),
            "tray-ready");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        // Serialize and flush beside the destination, then atomically rename
        // without overwrite. A verifier can therefore treat appearance of the
        // final path as a complete record, while a racing existing destination
        // remains a hard failure.
        var tempPath = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, sentinel, options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record StartupSmokeSentinel(
        int Pid,
        string InformationalVersion,
        string ProcessArchitecture,
        string Stage);

    private void OnUnhandledException(
        object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        DevLog.Write($"UNHANDLED: {e.Message}\n{e.Exception}");

        // Keep the tray alive for steady-state faults (a stray render / hover /
        // animation throw); let startup faults surface by leaving them unhandled.
        if (_started)
        {
            e.Handled = true;
        }
    }
}
