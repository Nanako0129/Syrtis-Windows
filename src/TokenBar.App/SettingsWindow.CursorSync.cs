using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;

namespace TokenBar.App;

// Accounts page → "Cursor" (macOS SettingsPanel cursorSyncSection,
// shared spec §2). Every action goes through CursorSyncController.Shared; this
// only draws its state and the stored preferences.
//
// The controls are built once and updated in place on every state change
// (issue #243): recreating them removed the focused control, and WinUI then
// moved focus to the next focusable one (on the General page, where the
// section was then, the Discord toggle below it).
public sealed partial class SettingsWindow
{
    private bool _cursorSyncSubscribed;

    private ToggleSwitch? _csToggle;
    private TextBlock? _csCleanupWarning;
    private TextBlock? _csStatusHint;
    private TextBlock? _csStatusWarning;
    private TextBlock? _csCliQuestion;
    private Button? _csAnswer;
    private Button? _csSyncNow;
    // S3b: the Cursor encrypted-sign-in consent. Allow opens the disclosure
    // confirmation; Withdraw appears once the grant is stored.
    private Button? _csKeychainAllow;
    private Button? _csKeychainWithdraw;
    private bool _csAnswerConfirms;
    private bool _csUpdating;
    // The switch whose enable confirmation is open. UpdateCursorSync leaves
    // only that switch's IsOn alone; a switch built later (Rebuild) always
    // shows the stored state, even if that confirmation never resolves.
    private ToggleSwitch? _csConfirmingToggle;

    // Called only with the controller (BuildAccountsPage states the contract).
    private StackPanel BuildCursorSync(SettingsStore store, CursorSyncController cursor)
    {
        var body = new StackPanel { Spacing = 6 };
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null };
        toggle.Toggled += async (_, _) =>
        {
            if (_csUpdating)
            {
                return; // programmatic IsOn from UpdateCursorSync, not the user
            }

            try
            {
                try
                {
                    if (!toggle.IsOn)
                    {
                        cursor.TurnOff();
                    }
                    else
                    {
                        // Consent is the controller's (CursorSyncController.TurnOn,
                        // which also records how this differs from macOS): while the
                        // notice is unanswered it opens this confirmation, and only
                        // Continue writes. While it is open, UpdateCursorSync leaves
                        // this switch's IsOn alone, so a state change (a sync ending,
                        // the Overview card) does not flip it under the flyout.
                        _csConfirmingToggle = toggle;
                        try
                        {
                            await cursor.TurnOn(() => ConfirmFlyout(toggle, CursorSync.Copy.Title,
                                CursorSync.Copy.Privacy, CursorSync.Copy.Continue, CursorSync.Copy.Cancel));
                        }
                        finally
                        {
                            if (ReferenceEquals(_csConfirmingToggle, toggle))
                            {
                                _csConfirmingToggle = null;
                            }
                        }
                    }
                }
                finally
                {
                    // Cancel or a failure: the switch shows the stored state again.
                    UpdateCursorSync(store);
                }
            }
            catch (Exception ex)
            {
                // async void: nothing in this handler, UpdateCursorSync
                // included, may escape to the dispatcher. The type only, as
                // elsewhere in Settings.
                DevLog.Write($"cursor-sync: settings toggle failed: {ex.GetType().Name}");
            }
        };
        AutomationProperties.SetName(toggle, CursorSync.Copy.Toggle.Localized());
        _csToggle = toggle;
        body.Children.Add(ToggleRow(CursorSync.Copy.Toggle.Localized(), toggle));
        // The privacy paragraph is in the section's info flyout, the enable
        // confirmation and the Overview notice card, not inline.

        _csCleanupWarning = WarningLine(CursorSync.Copy.CleanupFailed.Localized());
        _csStatusHint = Hint("");
        _csStatusWarning = WarningLine("");
        _csCliQuestion = Hint(CursorSync.Copy.CliQuestion.Localized());

        // One button for the takeover answer: its Content and action switch
        // between "Use Syrtis" and "Keep CLI", so the control that has focus
        // when the answer lands is never replaced or hidden.
        _csAnswer = new Button
        {
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        _csAnswer.Click += (_, _) => cursor.SetTakeoverConfirmed(_csAnswerConfirms);

        _csSyncNow = new Button
        {
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        _csSyncNow.Click += (_, _) => _ = cursor.RunSync(userInitiated: true);

        // Allow: the disclosure confirmation (C4), then store the "cursor"
        // grant and run the one explicit round that reads the sealed sign-in.
        _csKeychainAllow = new Button
        {
            Content = CursorSync.Copy.KeychainAllowAccess.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        _csKeychainAllow.Click += async (_, _) =>
        {
            try
            {
                // Raw English sources: ConfirmFlyout localizes each once.
                if (await ConfirmFlyout(_csKeychainAllow, CursorSync.Copy.Title,
                        CursorSync.Copy.KeychainDisclosure, CursorSync.Copy.KeychainAllow,
                        CursorSync.Copy.Cancel))
                {
                    AppSettings.KeychainConsent.Install(KeychainConsentComposer.CursorId, true);
                    UpdateCursorSync(store); // Withdraw shows from the stored answer now
                    // Never dropped: a round already running is discarded and
                    // rerun under the new answer (CursorSyncController.ConsentChanged).
                    await cursor.ConsentChanged();
                }
            }
            catch (Exception ex)
            {
                DevLog.Write($"cursor-sync: keychain allow failed: {ex.GetType().Name}");
            }
        };

        // Withdraw: store the "cursor" denial (composed payload) and refresh,
        // so the consent prompt returns and no sealed sign-in is read.
        _csKeychainWithdraw = new Button
        {
            Content = CursorSync.Copy.KeychainWithdraw.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        _csKeychainWithdraw.Click += (_, _) =>
        {
            try
            {
                AppSettings.KeychainConsent.Install(KeychainConsentComposer.CursorId, false);
                UpdateCursorSync(store); // Withdraw hides from the stored answer now
                // Never dropped (ConsentChanged): the rerun reports
                // keychain_consent, which brings back the consent line.
                _ = cursor.ConsentChanged();
            }
            catch (Exception ex)
            {
                DevLog.Write($"cursor-sync: keychain withdraw failed: {ex.GetType().Name}");
            }
        };

        SwallowKeyRepeat(_csSyncNow);
        SwallowKeyRepeat(_csAnswer);
        SwallowKeyRepeat(_csKeychainWithdraw);

        foreach (var element in new UIElement[]
        {
            _csCleanupWarning, _csStatusHint, _csStatusWarning, _csCliQuestion,
            _csAnswer, _csSyncNow, _csKeychainAllow, _csKeychainWithdraw,
        })
        {
            element.Visibility = Visibility.Collapsed;
            body.Children.Add(element);
        }

        if (!_cursorSyncSubscribed)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _cursorSyncSubscribed = true;
            cursor.StateChanged += () => DispatcherQueue.TryEnqueue(() => UpdateCursorSync(store));
        }

        UpdateCursorSync(store);
        return body;
    }

    private void UpdateCursorSync(SettingsStore store)
    {
        if (_csToggle is not { } toggle || _csSyncNow is not { } syncNow
            || _csAnswer is not { } answer || _csKeychainAllow is not { } allow
            || _csKeychainWithdraw is not { } withdraw || CursorSyncController.Shared is not { } cursor)
        {
            return;
        }

        var on = CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store));
        if (!ReferenceEquals(toggle, _csConfirmingToggle))
        {
            _csUpdating = true;
            try
            {
                toggle.IsOn = on;
            }
            finally
            {
                _csUpdating = false;
            }
        }

        Show(_csCleanupWarning!, CursorSync.CleanupFailedVisible(on, cursor.CleanupFailed));

        var state = cursor.State;
        var line = on ? CursorSync.StatusLine(state, cursor.LastSuccessMs, reason: cursor.Reason) : null;
        var warning = line is not null && CursorSync.StatusIsWarning(state);
        _csStatusHint!.Text = line ?? "";
        _csStatusWarning!.Text = line ?? "";
        Show(_csStatusHint, line is not null && !warning);
        Show(_csStatusWarning, line is not null && warning);

        // S3b: the encrypted-sign-in consent. Allow shows while the core asks
        // (keychain_consent); Withdraw shows once the grant is stored (so the
        // user can take it back). Both only while sync is on.
        var needAllow = on && CursorSync.NeedsKeychainAllow(state, cursor.Reason);
        var cursorGranted = store.GetNullableBool(KeychainConsentComposer.CursorStorageKey) == true;
        var fallback = on ? (Control)syncNow : toggle;
        MoveFocusBeforeHiding(allow, needAllow, fallback);
        MoveFocusBeforeHiding(withdraw, on && cursorGranted, fallback);
        Show(allow, needAllow);
        Show(withdraw, on && cursorGranted);

        var confirmed = CursorSync.TakeoverConfirmed(store);
        var askTakeover = on && state == "cliPresent" && !confirmed;
        Show(_csCliQuestion!, askTakeover);
        // Question shown: answer "Use Syrtis". Answered: the undo, "Keep CLI".
        _csAnswerConfirms = askTakeover;
        answer.Content = (askTakeover ? CursorSync.Copy.UseSyrtis : CursorSync.Copy.KeepCli).Localized();
        var showAnswer = askTakeover || (on && confirmed);

        if (on)
        {
            Show(syncNow, true); // before focus can be handed to it
        }

        MoveFocusBeforeHiding(answer, showAnswer, on ? syncNow : toggle);
        MoveFocusBeforeHiding(syncNow, on, toggle);
        Show(answer, showAnswer);
        Show(syncNow, on);
        var syncing = cursor.Syncing;
        var label = (syncing ? CursorSync.Copy.Syncing : CursorSync.Copy.SyncNow).Localized();
        syncNow.Content = label;
        // Stays enabled while syncing: a disabled button cannot keep keyboard
        // focus, and moving focus away would strand the user when the sync ends.
        // What a press during a sync does: see the RunSync summary.
        AutomationProperties.SetName(syncNow, label);
    }

    // A held Enter repeats Click (the answer button would flip back and forth,
    // Sync Now would restart). Swallow the key-repeat KeyDowns; the first press
    // and a Space release (Click fires on KeyUp) still go through.
    private static void SwallowKeyRepeat(Button button) =>
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space
                && e.KeyStatus.WasKeyDown)
            {
                e.Handled = true;
            }
        };

    // Focus must never be on a control that is about to collapse: WinUI would
    // move it out of the section. Hand it over first, keeping its FocusState.
    // Only while the Settings window is visible, active or not: moving focus
    // inside an inactive visible window is harmless and keeps it in the section
    // for when the user returns. The window is hidden, never closed. While it
    // is hidden the handoff is skipped (no layout or focus calls). A focused
    // control collapsing while hidden can still move WinUI focus (unmeasured).
    // Present() calls Activate(), which gives the window keyboard focus, and
    // Rebuild() regenerates the page; which control receives focus after a
    // re-show is not measured.
    private void MoveFocusBeforeHiding(Control leaving, bool staysVisible, Control target)
    {
        if (!staysVisible && AppWindow.IsVisible
            && leaving.FocusState != FocusState.Unfocused)
        {
            target.Visibility = Visibility.Visible;
            target.UpdateLayout();
            target.Focus(leaving.FocusState);
        }
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
