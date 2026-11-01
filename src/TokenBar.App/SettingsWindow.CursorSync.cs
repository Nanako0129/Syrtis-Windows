using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private StackPanel? _cursorSyncBody;
    private bool _cursorSyncSubscribed;

    private ToggleSwitch? _csToggle;
    private TextBlock? _csCleanupWarning;
    private TextBlock? _csStatusHint;
    private TextBlock? _csStatusWarning;
    private TextBlock? _csCliQuestion;
    private Button? _csUseSyrtis;
    private Button? _csKeepCli;
    private Button? _csSyncNow;
    private bool _csUpdating;
    private bool _csSyncPressIsRepeat;

    private StackPanel BuildCursorSync(SettingsStore store)
    {
        _cursorSyncBody = new StackPanel { Spacing = 6 };
        var cursor = CursorSyncController.Shared;

        var toggle = new ToggleSwitch { OnContent = null, OffContent = null };
        toggle.Toggled += (_, _) =>
        {
            if (_csUpdating)
            {
                return; // programmatic IsOn from UpdateCursorSync, not the user
            }

            cursor?.SetEnabled(toggle.IsOn);
            UpdateCursorSync(store);
        };
        AutomationProperties.SetName(toggle, CursorSync.Copy.Toggle.Localized());
        _csToggle = toggle;
        _cursorSyncBody.Children.Add(ToggleRow(CursorSync.Copy.Toggle.Localized(), toggle));
        _cursorSyncBody.Children.Add(Hint(CursorSync.Copy.Privacy.Localized()));

        _csCleanupWarning = WarningLine(CursorSync.Copy.CleanupFailed.Localized());
        _csStatusHint = Hint("");
        _csStatusWarning = WarningLine("");
        _csCliQuestion = Hint(CursorSync.Copy.CliQuestion.Localized());
        _csUseSyrtis = CursorSyncButton(CursorSync.Copy.UseSyrtis, () => cursor?.SetTakeoverConfirmed(true));
        _csKeepCli = CursorSyncButton(CursorSync.Copy.KeepCli, () => cursor?.SetTakeoverConfirmed(false));
        _csSyncNow = CursorSyncButton(CursorSync.Copy.SyncNow, () =>
        {
            // A held Enter repeats Click. A press that began before this one
            // (a key-repeat, possibly arriving after the sync it started has
            // ended) must not start another sync; only a fresh press does.
            var repeat = _csSyncPressIsRepeat;
            _csSyncPressIsRepeat = false;
            if (!repeat)
            {
                _ = cursor?.RunSync(userInitiated: true);
            }
        });
        _csSyncNow.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                _csSyncPressIsRepeat = e.KeyStatus.WasKeyDown;
            }
        };
        foreach (var element in new UIElement[]
        {
            _csCleanupWarning, _csStatusHint, _csStatusWarning, _csCliQuestion,
            _csUseSyrtis, _csKeepCli, _csSyncNow,
        })
        {
            _cursorSyncBody.Children.Add(element);
        }

        if (!_cursorSyncSubscribed && cursor is not null)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _cursorSyncSubscribed = true;
            cursor.StateChanged += () => DispatcherQueue.TryEnqueue(() => UpdateCursorSync(store));
        }

        UpdateCursorSync(store);
        return _cursorSyncBody;
    }

    private void UpdateCursorSync(SettingsStore store)
    {
        if (_csToggle is not { } toggle || _csSyncNow is not { } syncNow
            || _csUseSyrtis is not { } useSyrtis || _csKeepCli is not { } keepCli
            || CursorSyncController.Shared is not { } cursor)
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
        _csStatusHint!.Text = line ?? "";
        _csStatusWarning!.Text = line ?? "";
        Show(_csStatusHint, line is not null && !warning);
        Show(_csStatusWarning, line is not null && warning);

        var confirmed = CursorSync.TakeoverConfirmed(store);
        var askTakeover = on && state == "cliPresent" && !confirmed;
        var offerUndo = on && confirmed;
        Show(_csCliQuestion!, askTakeover);
        // The pressed button of the pair disappears when its answer lands: show
        // its replacement first and hand focus over (with the same keyboard or
        // pointer focus state) before collapsing it, so focus never leaves the
        // section. If nothing replaces it, Sync Now takes the focus.
        Button? gone = null;
        if (useSyrtis.FocusState != FocusState.Unfocused && !askTakeover)
        {
            gone = useSyrtis;
        }
        else if (keepCli.FocusState != FocusState.Unfocused && !offerUndo)
        {
            gone = keepCli;
        }

        if (gone is not null)
        {
            var target = gone == useSyrtis && offerUndo ? keepCli
                : gone == keepCli && askTakeover ? useSyrtis
                : on ? syncNow : null;
            if (target is not null)
            {
                var focusState = gone.FocusState;
                Show(target, true);
                target.UpdateLayout();
                target.Focus(focusState);
            }
        }

        Show(useSyrtis, askTakeover);
        Show(keepCli, offerUndo);

        Show(syncNow, on);
        var syncing = cursor.Syncing;
        var label = (syncing ? CursorSync.Copy.Syncing : CursorSync.Copy.SyncNow).Localized();
        syncNow.Content = label;
        // Stays enabled while syncing: a disabled button cannot keep keyboard
        // focus. The busy state is carried by the name screen readers announce
        // and by RunSync ignoring a second call.
        AutomationProperties.SetName(syncNow, label);
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button CursorSyncButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) => click();
        return button;
    }
}
