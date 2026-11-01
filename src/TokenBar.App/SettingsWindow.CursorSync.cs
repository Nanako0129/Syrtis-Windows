using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;

namespace TokenBar.App;

// General page → "Cursor usage sync" (macOS SettingsPanel cursorSyncSection,
// shared spec §2). Every action goes through CursorSyncController.Shared; this
// only draws its state and the stored preferences.
//
// The controls are built once and updated in place on every state change
// (issue #243): recreating them removed the focused control, and WinUI then
// moved focus to the next focusable one (the Discord toggle below the section).
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
    private bool _csAnswerConfirms;
    private bool _csUpdating;

    private StackPanel BuildCursorSync(SettingsStore store)
    {
        var body = new StackPanel { Spacing = 6 };
        if (CursorSyncController.Shared is not { } cursor)
        {
            return body; // nothing to draw or act on without the controller
        }

        var toggle = new ToggleSwitch { OnContent = null, OffContent = null };
        toggle.Toggled += (_, _) =>
        {
            if (_csUpdating)
            {
                return; // programmatic IsOn from UpdateCursorSync, not the user
            }

            cursor.SetEnabled(toggle.IsOn);
            UpdateCursorSync(store);
        };
        AutomationProperties.SetName(toggle, CursorSync.Copy.Toggle.Localized());
        _csToggle = toggle;
        body.Children.Add(ToggleRow(CursorSync.Copy.Toggle.Localized(), toggle));
        body.Children.Add(Hint(CursorSync.Copy.Privacy.Localized()));

        _csCleanupWarning = WarningLine(CursorSync.Copy.CleanupFailed.Localized());
        _csStatusHint = Hint("");
        _csStatusWarning = WarningLine("");
        // The result of a sync (Last synced, warnings) is announced when it changes.
        AutomationProperties.SetLiveSetting(_csStatusHint, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_csStatusWarning, AutomationLiveSetting.Polite);
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
        SwallowKeyRepeat(_csSyncNow);
        SwallowKeyRepeat(_csAnswer);

        foreach (var element in new UIElement[]
        {
            _csCleanupWarning, _csStatusHint, _csStatusWarning, _csCliQuestion,
            _csAnswer, _csSyncNow,
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
            || _csAnswer is not { } answer || CursorSyncController.Shared is not { } cursor)
        {
            return;
        }

        var on = CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store));
        _csUpdating = true;
        try
        {
            toggle.IsOn = on;
        }
        finally
        {
            _csUpdating = false;
        }

        Show(_csCleanupWarning!, CursorSync.CleanupFailedVisible(on, cursor.CleanupFailed));

        var state = cursor.State;
        var line = on ? CursorSync.StatusLine(state, cursor.LastSuccessMs) : null;
        var warning = line is not null && CursorSync.StatusIsWarning(state);
        var statusChanged = line is not null && _csStatusHint!.Text != line;
        _csStatusHint!.Text = line ?? "";
        _csStatusWarning!.Text = line ?? "";
        Show(_csStatusHint, line is not null && !warning);
        Show(_csStatusWarning, line is not null && warning);
        if (statusChanged)
        {
            // LiveSetting alone announces nothing; the change has to be raised.
            RaiseLiveRegionChanged(warning ? _csStatusWarning : _csStatusHint);
        }

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
        // RunSync ignores a call made during a running sync.
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
    private static void MoveFocusBeforeHiding(Control leaving, bool staysVisible, Control target)
    {
        if (!staysVisible && leaving.FocusState != FocusState.Unfocused)
        {
            target.Visibility = Visibility.Visible;
            target.UpdateLayout();
            target.Focus(leaving.FocusState);
        }
    }

    private static void RaiseLiveRegionChanged(UIElement element) =>
        FrameworkElementAutomationPeer.FromElement(element)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
