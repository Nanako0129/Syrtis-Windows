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
            body.Children.Add(CursorSyncButton(CursorSync.Copy.UseSyrtis, enabled: true,
                () => cursor.SetTakeoverConfirmed(true)));
        }
        else if (CursorSync.TakeoverConfirmed(store))
        {
            // Undo for the D6 answer; the question returns on the next sync.
            body.Children.Add(CursorSyncButton(CursorSync.Copy.KeepCli, enabled: true,
                () => cursor.SetTakeoverConfirmed(false)));
        }

        var syncing = cursor.Syncing;
        body.Children.Add(CursorSyncButton(
            syncing ? CursorSync.Copy.Syncing : CursorSync.Copy.SyncNow, enabled: !syncing,
            () => _ = cursor.RunSync(userInitiated: true)));
    }

    private static Button CursorSyncButton(string text, bool enabled, Action click)
    {
        var button = new Button
        {
            Content = text.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
            IsEnabled = enabled,
        };
        button.Click += (_, _) => click();
        return button;
    }
}
