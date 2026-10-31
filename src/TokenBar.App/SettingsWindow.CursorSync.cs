using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;

namespace TokenBar.App;

// General page → "Cursor usage sync" (macOS SettingsPanel cursorSyncSection,
// shared spec §2). Every action goes through CursorSyncController.Shared; this
// only draws its state and the stored preferences.
public sealed partial class SettingsWindow
{
    private StackPanel? _cursorSyncBody;
    private bool _cursorSyncSubscribed;

    private StackPanel BuildCursorSync(SettingsStore store)
    {
        _cursorSyncBody = new StackPanel { Spacing = 6 };
        if (!_cursorSyncSubscribed && CursorSyncController.Shared is { } cursor)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _cursorSyncSubscribed = true;
            cursor.StateChanged += () => DispatcherQueue.TryEnqueue(() => FillCursorSync(store));
        }

        FillCursorSync(store);
        return _cursorSyncBody;
    }

    private void FillCursorSync(SettingsStore store)
    {
        if (_cursorSyncBody is not { } body || CursorSyncController.Shared is not { } cursor)
        {
            return;
        }

        // Rebuilding removes the focused control and focus falls to the next
        // focusable one (the Discord toggle). Remember which control had it
        // and hand it to its replacement (issue #243).
        var focusedKey = body.XamlRoot is { } root
            && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is FrameworkElement { Tag: string key }
            ? key : null;
        body.Children.Clear();
        var on = CursorSync.ToggleShowsOn(CursorSync.Enabled(store), CursorSync.NoticeAcknowledged(store));
        var toggle = new ToggleSwitch { IsOn = on, OnContent = null, OffContent = null };
        toggle.Toggled += (_, _) =>
        {
            cursor.SetEnabled(toggle.IsOn);
            FillCursorSync(store);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, CursorSync.Copy.Toggle.Localized());
        body.Children.Add(ToggleRow(CursorSync.Copy.Toggle.Localized(), toggle));
        body.Children.Add(Hint(CursorSync.Copy.Privacy.Localized()));

        if (CursorSync.CleanupFailedVisible(on, cursor.CleanupFailed))
        {
            body.Children.Add(WarningLine(CursorSync.Copy.CleanupFailed.Localized()));
        }

        if (!on)
        {
            return;
        }

        var state = cursor.State;
        if (CursorSync.StatusLine(state, cursor.LastSuccessMs) is { } line)
        {
            body.Children.Add(CursorSync.StatusIsWarning(state) ? WarningLine(line) : Hint(line));
        }

        if (state == "cliPresent" && !CursorSync.TakeoverConfirmed(store))
        {
            body.Children.Add(Hint(CursorSync.Copy.CliQuestion.Localized()));
            body.Children.Add(CursorSyncButton("useSyrtis", CursorSync.Copy.UseSyrtis,
                () => cursor.SetTakeoverConfirmed(true)));
        }
        else if (CursorSync.TakeoverConfirmed(store))
        {
            // Undo for the D6 answer; the question returns on the next sync.
            body.Children.Add(CursorSyncButton("keepCli", CursorSync.Copy.KeepCli,
                () => cursor.SetTakeoverConfirmed(false)));
        }

        // Stays enabled while syncing: a disabled button cannot hold keyboard
        // focus, and RunSync already ignores a call during a running sync.
        body.Children.Add(CursorSyncButton("syncNow",
            cursor.Syncing ? CursorSync.Copy.Syncing : CursorSync.Copy.SyncNow,
            () => _ = cursor.RunSync(userInitiated: true)));
        RestoreFocus(body, focusedKey);
    }

    private static void RestoreFocus(StackPanel body, string? key)
    {
        if (key is null)
        {
            return;
        }

        foreach (var child in body.Children)
        {
            if (child is Button { Tag: string k } b && k == key)
            {
                // The replacement is not in a layout pass yet; focus it once loaded.
                RoutedEventHandler? focus = null;
                focus = (_, _) =>
                {
                    b.Loaded -= focus;
                    b.Focus(FocusState.Programmatic);
                };
                b.Loaded += focus;
                return;
            }
        }
    }

    private static Button CursorSyncButton(string key, string text, Action click)
    {
        var button = new Button
        {
            Content = text.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
            Tag = key,
        };
        button.Click += (_, _) => click();
        return button;
    }
}
