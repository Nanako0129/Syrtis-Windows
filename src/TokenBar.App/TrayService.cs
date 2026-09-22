using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// Owns the notification-area icon (H.NotifyIcon): left click toggles the
/// flyout, right click gets the context menu (quota-source picker joins with
/// the full menu work item). The icon is drawn live: a non-hidden tray mode
/// renders its short value into the icon (Windows has no tray text — parity
/// table #1: value in icon, full string in the tooltip, always-on flyout
/// header), Hidden mode shows the pictorial gauge.
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly string[] IconKeys =
    [
        TrayModes.StorageKey, "tokenbar.tray.animationStyle",
        "tokenbar.icon.coloring", "tokenbar.quota.source",
        "tokenbar.tray.animate",
    ];

    private readonly TaskbarIcon _icon;
    private readonly TrayFeed _feed;
    private readonly TrayAnimator _animator;
    private readonly Action<string> _onStoreChanged;
    private readonly PendingUpdateAction _pendingUpdate = new();
    private PendingUpdateAction.PendingAction? _activeUpdate;
    /// <summary>What the dialog displays for the currently pending action.
    /// PendingUpdateAction carries only a version string; the dialog also needs
    /// the installed version and the release notes, and this is the cheaper of
    /// the two ways the plan allowed — the tray holds the display record beside
    /// the pending action rather than PendingUpdateAction growing a payload it
    /// has no opinion about.</summary>
    private UpdateOffer? _pendingOffer;
    private string _iconSignature = "";
    private nint _hicon;
    private bool _disposed;
    private readonly DispatcherQueue _dispatcher;
    private TrayForceCreateEpisode? _creationEpisode;
    private DispatcherQueueTimer? _retryTimer;
    private TaskCompletionSource<bool>? _readyTcs;
    private bool _trayCreationFailed;

    public TrayService(FlyoutWindow flyout, GraphRequestCoordinator graphCoordinator)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = ProductIdentity.Name,
        };
        _flyout = flyout;
        _icon.LeftClickCommand = new RelayCommand(flyout.ToggleFlyout);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += (_, eventArgs) =>
        {
            if (eventArgs.MouseEvent == MouseEvent.BalloonToolTipClicked)
            {
                _ = TryOpenUpdateDialog();
            }
        };
        _icon.NoLeftClickDelay = true;
        // NOT SecondWindow: that mode parks a transparent helper window over
        // the desktop, which swallowed hover/wheel input meant for the flyout.
        _icon.ContextMenuMode = ContextMenuMode.PopupMenu;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _feed = new TrayFeed(_dispatcher, graphCoordinator);
        _animator = new TrayAnimator(_dispatcher, () => _feed.TokensPerMin, ApplyCachedIcon);
        OpenSettings = ShowSettings;
        _feed.Changed += () =>
        {
            UpdateIcon();
            RebuildMenu(); // quota percentages in the source picker move
        };
        _onStoreChanged = key =>
        {
            if (IconKeys.Contains(key))
            {
                // Changed fires on the writing thread; today's writers are
                // all UI-thread, but the XAML objects here must never bet
                // on that staying true.
                _ = _dispatcher.TryEnqueue(() =>
                {
                    UpdateIcon();
                    RebuildMenu();
                });
            }
        };
        AppSettings.Store.Changed += _onStoreChanged;

        QuitApp = () =>
        {
            Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        };

        UpdateIcon();
        RebuildMenu();
        // Soft first attempt without Sleep; schedule timer ticks if needed.
        BeginCreationEpisode();
    }

    /// <summary>True when H.NotifyIcon reports the shell icon was created.</summary>
    internal bool IsTrayIconCreated => _icon.IsCreated;

    /// <summary>
    /// Hard tray-ready gate for --startup-smoke: awaits a fresh creation
    /// episode with a bounded timeout (timer ticks; no UI-thread blocking wait).
    /// </summary>
    internal Task AssertTrayReadyAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (IsTrayIconCreated)
        {
            return Task.CompletedTask;
        }

        if (_trayCreationFailed)
        {
            return Task.FromException(new InvalidOperationException(
                "tray icon creation already exhausted without success."));
        }

        var budget = timeout
            ?? TimeSpan.FromMilliseconds(TrayForceCreatePolicy.StartupSmokeTimeoutMilliseconds);
        BeginCreationEpisode();
        return WaitForTrayReadyAsync(budget, cancellationToken);
    }

    private readonly FlyoutWindow _flyout;

    /// <summary>The flyout footer's gear reaches settings through here —
    /// the view layer never sees the tray feed.</summary>
    public static Action? OpenSettings { get; private set; }

    /// <summary>Settings' manual "Check now" reaches the update flow through
    /// here. Unlike the two neighbours this is set by App, which owns the
    /// flow — the tray only already owns the update surface (PublishUpdate),
    /// so this is where the view layer looks for it.</summary>
    internal static Func<Task<UpdateCheckResult>>? CheckForUpdates { get; set; }

    public void ShowSettings() => SettingsWindow.Present(
        () => _feed.Quota, () => _feed.Graph, () => _feed.Trace);

    internal bool CanHandoff => !Volatile.Read(ref _disposed);

    /// <summary>Offer an update in the tray. Refuses while one is already
    /// being downloaded: PendingUpdateAction.Publish overwrites unconditionally,
    /// so republishing would re-expose "Update to v…" while _activeUpdate is
    /// still running, and taking it would start a second UpdateFlow against the
    /// same package path — each flow's failure path calls CleanAllPackages, so
    /// the two can destroy each other's download. The automatic check has the
    /// same shape and only escaped because it runs once at startup; the manual
    /// check made it reachable at any moment, so the guard belongs here rather
    /// than at either caller.</summary>
    internal bool PublishUpdate(UpdateOffer offer, Action action)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var version = offer.Version;
        if (_disposed || _activeUpdate is not null
            || !_pendingUpdate.Publish(version, action))
        {
            return false;
        }

        _pendingOffer = offer;
        try
        {
            RebuildMenu();
            try
            {
                _icon.ShowNotification(
                    title: ProductIdentity.Name,
                    message: "Update to v{0}".Localized(version),
                    icon: NotificationIcon.Info,
                    sound: false,
                    respectQuietTime: true);
            }
            catch (Exception ex)
            {
                var type = ex.GetType().Name;
                DevLog.Write(
                    $"update-notification: failed {type[..Math.Min(type.Length, 64)]}");
            }

            return true;
        }
        catch
        {
            _pendingUpdate.Clear();
            _pendingOffer = null;
            RebuildMenu();
            throw;
        }
    }

    /// <summary>Open the Sparkle-style dialog for the pending update.
    ///
    /// <para><b>This is what the menu item and the balloon click do now — the
    /// dialog is opened <em>before</em> the pending action is taken, never from
    /// inside it.</b> That ordering is the whole design.
    /// <see cref="InvokePendingUpdate"/> does <c>Take()</c> and sets
    /// <c>_activeUpdate</c> before running the action, so a dialog opened from
    /// inside the published action would be looking at an update that has
    /// already been taken: Remind Me Later's "the pending action stays" would
    /// be false, and Skip would leave <c>_activeUpdate</c> non-null, whose
    /// guard in <see cref="PublishUpdate"/> then refuses <em>every</em> future
    /// offer for the life of the process — skipping 0.2.3 would silently
    /// suppress 0.2.4.</para>
    ///
    /// <para>Returns false when there is nothing to show, which is also how a
    /// stale handler learns to close instead of re-presenting.</para></summary>
    internal bool TryOpenUpdateDialog()
    {
        if (_disposed
            || _pendingUpdate.Peek() is not { } version
            || _pendingOffer is not { } offer
            || !string.Equals(offer.Version, version, StringComparison.Ordinal))
        {
            return false;
        }

        // Captured before the dialog opens. The version alone is not identity:
        // Settings' "Check now" can republish the *same* version while the
        // dialog is open, and Publish overwrites the action unconditionally, so
        // a same-version republish whose notes or checksum moved would pass a
        // string comparison while Install ran a different action.
        var shownGeneration = _pendingUpdate.PeekGeneration();

        UpdateDialog.Present(
            offer,
            install: () => ActOnShownOffer(
                shownGeneration, version, InvokePendingUpdate, closeAfterActing: false),
            later: () =>
            {
                // Nothing to undo: nothing was taken. The pending action stays,
                // so the tray still offers it and the next check offers it again.
                DevLog.Write(
                    $"update-dialog: later v{version} active={_activeUpdate is not null}");
                return true;
            },
            skip: () => ActOnShownOffer(
                shownGeneration, version, () => SkipUpdate(offer), closeAfterActing: true));
        return true;
    }

    /// <summary>Run a dialog action only if the update it was shown for is
    /// still the pending one.
    ///
    /// <para><c>_activeUpdate</c> is null while the dialog is open, so
    /// <see cref="PublishUpdate"/> lets a newer candidate through and
    /// <c>PendingUpdateAction.Publish</c> overwrites unconditionally. Without
    /// this check the dialog would show vX's notes while Install took vY —
    /// installing a different version than the one described — and Skip would
    /// record vX while clearing vY's pending action.</para></summary>
    /// <param name="closeAfterActing">Whether the dialog should close once the
    /// action has run. Skip closes; Install does not — it stays open and turns
    /// into the download's progress report, which is the entire point of having
    /// progress at all. Returning true here hides the window, and a hidden
    /// window renders progress nobody can see.</param>
    private bool ActOnShownOffer(
        int? shownGeneration, string shownVersion, Action act, bool closeAfterActing)
    {
        if (_disposed)
        {
            return true;
        }

        // Generation first: it distinguishes two offers of the same version,
        // which the string cannot. The version is still compared so the DevLog
        // line names something a human can read.
        if (_pendingUpdate.PeekGeneration() != shownGeneration
            || !string.Equals(_pendingUpdate.Peek(), shownVersion, StringComparison.Ordinal))
        {
            DevLog.Write($"update-dialog: offer moved off v{shownVersion}");
            // Re-present against whatever is pending now; if nothing is, close.
            return !TryOpenUpdateDialog();
        }

        act();
        return closeAfterActing;
    }

    /// <summary>Skip This Version: record it, stop offering it, and leave
    /// <c>_activeUpdate</c> null — nothing was taken.
    ///
    /// <para><c>Clear()</c> is the right primitive and the other two are not:
    /// <c>RestoreUpdateAction</c> restores an action that was <em>taken</em>,
    /// and <c>CompleteUpdateAction</c> clears after a successful hand-off. What
    /// is new here is only the menu rebuild and the settings write.</para>
    ///
    /// <para>The version written is <c>UpdateCandidate.Version</c> as it
    /// travelled through <see cref="PublishUpdate"/> — never a string read back
    /// out of the dialog's own display text.</para></summary>
    private void SkipUpdate(UpdateOffer offer)
    {
        if (_disposed)
        {
            return;
        }

        AppSettings.Store.SetString(
            PendingUpdateAction.SkippedVersionKey, offer.Version);
        _pendingUpdate.Clear();
        _pendingOffer = null;
        RebuildMenu();
        DevLog.Write(
            $"update-dialog: skip v{offer.Version} active={_activeUpdate is not null}");
    }

    internal void RestoreUpdateAction()
    {
        if (_disposed || _activeUpdate is not { } active)
        {
            return;
        }

        if (_pendingUpdate.Restore(active))
        {
            _activeUpdate = null;
            RebuildMenu();
        }
    }

    internal void CompleteUpdateAction()
    {
        if (_disposed)
        {
            return;
        }

        _activeUpdate = null;
        _pendingUpdate.Clear();
        _pendingOffer = null;
    }

    /// <summary>Takes the pending action and runs it. Reached only from the
    /// dialog's Install button now: it is what the menu item used to do
    /// directly.
    ///
    /// <para>Install must come through here rather than calling
    /// <c>UpdateFlow.DownloadAndVerifyAsync</c> itself.
    /// <c>UpdateFlow._downloadActive</c> is per instance and every check builds
    /// a new flow, so <c>_activeUpdate</c> is the only cross-flow guard there
    /// is. Bypassing it would leave "Update to v…" live during the download,
    /// and invoking it again would start a second flow against the same package
    /// path — either flow's failure calls <c>CleanAllPackages</c>, so the two
    /// destroy each other's work.</para></summary>
    private void InvokePendingUpdate()
    {
        if (_disposed || _pendingUpdate.Take() is not { } pending)
        {
            return;
        }

        _activeUpdate = pending;
        DevLog.Write($"update-dialog: install v{pending.Version} active=True");
        RebuildMenu();
        try
        {
            if (!pending.TryInvoke())
            {
                _activeUpdate = null;
            }
        }
        catch (Exception ex)
        {
            _ = _pendingUpdate.Restore(pending);
            _activeUpdate = null;
            RebuildMenu();
            var type = ex.GetType().Name;
            DevLog.Write($"update-download: failed {type[..Math.Min(type.Length, 64)]}");
        }
    }

    /// <summary>The full context menu (macOS splits this between the
    /// status-item right-click quota menu and the settings panel; Windows
    /// keeps one battery-style menu): Open · Menu bar shows · Quota source ·
    /// Settings · Quit. Rebuilt whenever the data it displays moves — the
    /// flyout is closed at that moment, so a rebuild is invisible.</summary>
    private void RebuildMenu()
    {
        // PopupMenu mode converts this MenuFlyout to a NATIVE Win32 menu:
        // only Command fires (Click never does), and only
        // ToggleMenuFlyoutItem's IsChecked survives the conversion — so
        // every actionable item is Command-wired, and "radios" are toggles
        // whose exclusivity comes from rebuilding off the store.
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Open {0}".Localized(ProductIdentity.Name),
            Command = new RelayCommand(_flyout.ShowFlyout),
        });
        if (_pendingUpdate.Peek() is { } updateVersion)
        {
            menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
            {
                Text = "Update to v{0}".Localized(updateVersion),
                Command = new RelayCommand(() => TryOpenUpdateDialog()),
            });
        }
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

        // Menu bar shows — the seven TrayModes as a radio group.
        var modes = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem { Text = "Menu bar shows".Localized() };
        var current = TrayModes.Parse(AppSettings.Store.GetString(TrayModes.StorageKey));
        foreach (var mode in TrayModes.All)
        {
            var picked = mode;
            modes.Items.Add(new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
            {
                Text = mode.Label(),
                IsChecked = mode == current,
                Command = new RelayCommand(() =>
                    AppSettings.Store.SetString(TrayModes.StorageKey, picked.RawValue())),
            });
        }

        menu.Items.Add(modes);

        // Quota source — macOS showQuotaMenu: Auto plus every error-free
        // agent's windows with live remaining percentages.
        var source = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem { Text = "Quota source".Localized() };
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var payload = _feed.Quota;
        var selection = QuotaSelectionPolicy.EffectiveSelection(
            payload, persistedSelection);
        AddQuotaChoice(
            source, "Auto (tightest window)".Localized(),
            QuotaResolver.Auto, selection);
        if (payload is null)
        {
            source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
            {
                Text = "Loading quotas…".Localized(),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var agent in payload.Agents.Where(
                a => a.Error is null && a.UniqueCardWindows.Count > 0))
            {
                source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
                source.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
                {
                    Text = ClientRegistry.ShortName(agent.ClientId),
                    IsEnabled = false,
                });
                foreach (var window in agent.UniqueCardWindows)
                {
                    var left = (int)Math.Round(
                        Math.Clamp(window.RemainingPercent, 0, 100),
                        MidpointRounding.AwayFromZero);
                    AddQuotaChoice(
                        source, "{0} — {1}% left".Localized(window.Label.Localized(), left),
                        QuotaResolver.Selection(agent.ClientId, window.CardId), selection);
                }
            }
        }

        menu.Items.Add(source);
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Settings".Localized(),
            Command = new RelayCommand(ShowSettings),
        });
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Quit".Localized(),
            Command = new RelayCommand(() => QuitApp?.Invoke()),
        });
        _icon.ContextFlyout = menu;
    }

    private static void AddQuotaChoice(
        Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem parent,
        string label, string value, string selection)
    {
        parent.Items.Add(new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem
        {
            Text = label,
            IsChecked = selection == value,
            Command = new RelayCommand(() =>
                AppSettings.Store.SetString("tokenbar.quota.source", value)),
        });
    }

    /// <summary>The full summary the tray tooltip shows in every mode
    /// (Windows has no menu-bar text, so this is where the exact figures
    /// live): today and all-time tokens and cost, plus the selected quota
    /// window. Kept well under the ~127-char Shell_NotifyIcon tip limit.</summary>
    private string BuildTooltip()
    {
        if (_feed.VisibleTotals is not { } totals)
        {
            return "{0} — loading…".Localized(ProductIdentity.Name);
        }

        var lines = new List<string>
        {
            ProductIdentity.Name,
            "Today {0}".Localized(Format.CompactTokens(totals.TodayTokens))
                + " · "
                + CostSurfaceProjection.CostText(
                    totals.TodayTokens, totals.TodayCost, _feed.CostAuthoritative),
            "All time {0}".Localized(Format.CompactTokens(totals.TotalTokens))
                + " · "
                + CostSurfaceProjection.CostText(
                    totals.TotalTokens, totals.TotalCost, _feed.CostAuthoritative),
        };
        var persistedSelection = AppSettings.Store.GetString(
            "tokenbar.quota.source", QuotaResolver.Auto) ?? QuotaResolver.Auto;
        var hidden = ClientRegistry.QuotaExcludedClients(AppSettings.Store);
        if (QuotaSelectionPolicy.Resolve(_feed.Quota, persistedSelection, hidden)
            is { } pick)
        {
            var left = Math.Clamp(pick.Window.RemainingPercent, 0, 100);
            lines.Add("{0} {1} {2}% left".Localized(
                ClientRegistry.ShortName(pick.ClientId),
                pick.Window.Label.Localized(),
                left.ToString("F0", System.Globalization.CultureInfo.CurrentCulture)));
        }

        return string.Join("\n", lines);
    }

    private void UpdateIcon()
    {
        var mode = TrayModes.Parse(AppSettings.Store.GetString(TrayModes.StorageKey));
        var styleRaw = AppSettings.Store.GetString("tokenbar.tray.animationStyle", "cat");
        var coloring = TrayIconRenderer.ParseColoring(
            AppSettings.Store.GetString("tokenbar.icon.coloring"));
        var dark = IsSystemDark();
        var remaining = _feed.QuotaRemaining;
        var title = CostSurfaceProjection.TrayTitle(
            mode,
            _feed.VisibleTotals,
            _feed.TokensPerMin,
            remaining,
            _feed.CostAuthoritative);

        // The tooltip is the summary layer (parity table #1): it carries the
        // full figures whatever the icon shows — including the icon-only
        // gauge/animation modes, which draw no value at all. Cheap, so it
        // refreshes every tick, ahead of the icon-repaint short-circuit.
        _icon.ToolTipText = BuildTooltip();

        // Re-render only when the drawn state actually changed (macOS
        // iconSettingsSignature): the feed ticks far more often than the
        // numbers move.
        var animate = AppSettings.Store.GetBool("tokenbar.tray.animate", true);
        var signature = $"{mode}|{styleRaw}|{coloring}|{dark}|{title}|{remaining:F1}|{animate}";
        if (signature == _iconSignature)
        {
            return;
        }

        _iconSignature = signature;

        // Hidden mode with an animation style hands the icon to the
        // animator; every other state renders one static frame here.
        if (mode == TrayMode.Hidden && styleRaw is "cat" or "parrot")
        {
            _animator.Start(styleRaw, dark,
                animate: animate);
            return;
        }

        _animator.Stop();
        using var bmp = mode != TrayMode.Hidden && title.Length > 0
            ? TrayIconRenderer.RenderTitle(
                TrayModes.IconTitle(title),
                mode == TrayMode.QuotaLeft && remaining is { } q
                    ? TrayIconRenderer.GaugeColor(q) : null,
                dark)
            : TrayIconRenderer.RenderGauge(
                TrayIconRenderer.ParseGaugeStyle(styleRaw) ?? QuotaIconStyle.Bars,
                remaining, dark, coloring);
        ApplyIcon(bmp);
    }

    /// <summary>One-shot render: the service owns the HICON and destroys it
    /// when the next owned one replaces it.</summary>
    private void ApplyIcon(System.Drawing.Bitmap bmp)
    {
        var hicon = bmp.GetHicon();
        _icon.Icon = System.Drawing.Icon.FromHandle(hicon);
        EnsureIconCreated();
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
        }

        _hicon = hicon;
    }

    /// <summary>Animator frames: cached icons the animator owns — never
    /// destroyed here, but a previously owned one-shot HICON is released
    /// once it is off screen.</summary>
    private void ApplyCachedIcon(System.Drawing.Icon icon)
    {
        _icon.Icon = icon;
        EnsureIconCreated();
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
            _hicon = 0;
        }
    }

    private void EnsureIconCreated()
    {
        if (_icon.IsCreated || _trayCreationFailed)
        {
            return;
        }

        BeginCreationEpisode();
    }

    /// <summary>
    /// Starts a new per-episode ForceCreate budget if none is running. One
    /// attempt runs immediately; further attempts ride DispatcherQueueTimer
    /// ticks (no blocking sleep on the UI thread).
    /// </summary>
    private void BeginCreationEpisode()
    {
        if (_disposed || _icon.IsCreated || _trayCreationFailed)
        {
            return;
        }

        if (_creationEpisode is not null && !_creationEpisode.IsExhausted)
        {
            return; // episode already in flight
        }

        _creationEpisode = new TrayForceCreateEpisode();
        _readyTcs ??= new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessCreationTick();
    }

    private void ProcessCreationTick()
    {
        if (_disposed || _icon.IsCreated)
        {
            CompleteReady(success: _icon.IsCreated);
            StopRetryTimer();
            return;
        }

        var episode = _creationEpisode
            ?? throw new InvalidOperationException("creation episode missing");
        TrayForceCreateTickResult result;
        try
        {
            result = episode.Tick(() =>
            {
                _icon.ForceCreate();
                return _icon.IsCreated;
            });
        }
        catch (Exception ex)
        {
            // Non-retryable: fail closed visibly.
            DevLog.Write($"ForceCreate hard failure: {ex}");
            FailTrayCreation(ex.Message);
            throw;
        }

        switch (result)
        {
            case TrayForceCreateTickResult.Success:
                CompleteReady(success: true);
                StopRetryTimer();
                return;
            case TrayForceCreateTickResult.Exhausted:
                FailTrayCreation(
                    $"tray icon not ready after {episode.Attempts} ForceCreate attempt(s) "
                    + $"over {episode.Elapsed.TotalMilliseconds:0} ms "
                    + $"(deadline {TrayForceCreatePolicy.EpisodeDeadlineMilliseconds} ms)");
                return;
            case TrayForceCreateTickResult.Continue:
                // Use the episode-computed delay (capped and clamped to remaining budget).
                ScheduleRetryTick(episode.NextDelayMilliseconds);
                return;
            default:
                throw new InvalidOperationException($"unknown tick result {result}");
        }
    }

    private void ScheduleRetryTick(int delayMs)
    {
        StopRetryTimer();
        _retryTimer = _dispatcher.CreateTimer();
        _retryTimer.IsRepeating = false;
        _retryTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(delayMs, 1));
        _retryTimer.Tick += (_, _) =>
        {
            StopRetryTimer();
            if (!_disposed)
            {
                ProcessCreationTick();
            }
        };
        _retryTimer.Start();
    }

    private void StopRetryTimer()
    {
        if (_retryTimer is null)
        {
            return;
        }

        _retryTimer.Stop();
        _retryTimer = null;
    }

    private void CompleteReady(bool success)
    {
        if (success)
        {
            _trayCreationFailed = false;
            _creationEpisode = null;
            _readyTcs?.TrySetResult(true);
            _readyTcs = null;
        }
    }

    private void FailTrayCreation(string message)
    {
        _trayCreationFailed = true;
        _creationEpisode = null;
        StopRetryTimer();
        DevLog.Write($"tray creation FAILED: {message}");
        _readyTcs?.TrySetException(new InvalidOperationException(message));
        _readyTcs = null;

        // Normal launch must not leave an invisible tray-less process.
        // Startup-smoke path also exits via QuitApp after assert failure.
        try
        {
            Environment.ExitCode = 1;
            QuitApp?.Invoke();
        }
        catch (Exception ex)
        {
            DevLog.Write($"tray fail-exit threw: {ex.Message}");
            Microsoft.UI.Xaml.Application.Current?.Exit();
        }
    }

    private async Task WaitForTrayReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IsTrayIconCreated)
        {
            return;
        }

        var tcs = _readyTcs
            ?? throw new InvalidOperationException("tray creation episode was not started");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await using var reg = linked.Token.Register(
            () => tcs.TrySetException(new TimeoutException(
                $"tray icon not ready within {timeout.TotalMilliseconds:0} ms")));
        _ = await tcs.Task.ConfigureAwait(true);
        if (!IsTrayIconCreated)
        {
            throw new InvalidOperationException("tray icon creation finished without ready state");
        }
    }

    /// <summary>Taskbar theme; missing value = dark (the Windows default).</summary>
    private static bool IsSystemDark()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    /// <summary>The single shutdown path. Every Quit entry (tray menu,
    /// flyout footer button, Ctrl+Q) routes here so the icon, timers, and
    /// GDI handles are always released before the process exits — no entry
    /// bypasses cleanup by calling Application.Exit() directly.</summary>
    public static Action? QuitApp { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return; // every Quit entry routes here; make it safe to call twice
        }

        _disposed = true;
        StopRetryTimer();
        _creationEpisode = null;
        _readyTcs?.TrySetCanceled();
        _readyTcs = null;
        _pendingUpdate.Dispose();
        _activeUpdate = null;
        _pendingOffer = null;
        AppSettings.Store.Changed -= _onStoreChanged;
        _feed.Dispose(); // stop polling before the icon it feeds goes away
        _animator.Stop();
        // Remove the shell icon FIRST: in animation mode _icon.Icon points at
        // one of the animator's cached HICONs, so destroying those before the
        // TaskbarIcon lets go would hand it an invalid handle.
        _icon.Dispose();
        _animator.Dispose();
        if (_hicon != 0)
        {
            _ = DestroyIcon(_hicon);
            _hicon = 0;
        }
    }
}

/// <summary>Minimal ICommand so the skeleton avoids an MVVM package pull.</summary>
public sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
