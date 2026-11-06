using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;
using TokenBar.Interop;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// Accounts page → "Antigravity" (macOS SettingsPanel
// antigravityAccountsSection). Every action goes through
// AntigravityAutoCapture.Shared; this only draws its state and the stored list.
public sealed partial class SettingsWindow
{
    private bool _antigravitySubscribed;
    private AgySection? _agy;

    /// <summary>Everything one page build owns: the body, its rows, the fixed
    /// controls and the switch's flags. Rebuild() makes a new body (every
    /// Present and some store keys) while an older build's switch handler may
    /// still be awaiting its confirmation flyout; each handler captures its own
    /// section and touches only that, so a stale one is inert and the window's
    /// Fill uses <see cref="_agy"/>. Controls exist only when the capture
    /// controller does.</summary>
    private sealed class AgySection
    {
        public StackPanel Body { get; } = new() { Spacing = 6 };
        public List<AntigravityAccountRow> Rows { get; } = [];
        public Button? Capture { get; set; }
        public TextBlock? Error { get; set; }
        public ToggleSwitch? Toggle { get; set; }
        public TextBlock? Paused { get; set; }
        public bool SyncingToggle { get; set; }
        public bool Toggling { get; set; }
    }

    /// <summary>One captured account's row, kept across redraws (MA-43).</summary>
    private sealed record AntigravityAccountRow(string Key, TextBlock Label, Button Remove, Grid Grid);

    private StackPanel BuildAntigravityAccounts(SettingsStore store)
    {
        var section = _agy = new AgySection();
        if (AntigravityAutoCapture.Shared is { } capture)
        {
            BuildAntigravityControls(store, section, capture);
            if (!_antigravitySubscribed)
            {
                // The window is hidden, never closed, so one subscription lives
                // as long as the app.
                _antigravitySubscribed = true;
                capture.StateChanged += () => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_agy is { } current)
                    {
                        FillAntigravityAccounts(store, current);
                    }
                });
            }
        }

        FillAntigravityAccounts(store, section);
        return section.Body;
    }

    // The fixed controls (Capture, the error line, the switch, the paused line)
    // are built once per page build and updated in place; a row exists while
    // its account does. A focused control that is about to be removed or
    // disabled hands focus to the switch first (MA-43: a rebuild dropped
    // keyboard focus, so Tab+Space on the switch lost it at once).
    private void FillAntigravityAccounts(SettingsStore store, AgySection section)
    {
        if (section.Toggle is not { } toggle || AntigravityAutoCapture.Shared is not { } capture)
        {
            return;
        }

        var body = section.Body;
        var rows = section.Rows;
        var busy = capture.Busy;
        var accounts = AntigravityAccounts.Load(store);
        var keys = SectionRows.Keys(accounts.Select(a => a.Key));

        var focused = rows.FirstOrDefault(r => r.Remove.FocusState != FocusState.Unfocused);
        var (move, to) = SectionRows.FocusHandoff([.. rows.Select(r => r.Key)], keys, focused?.Key);
        if (move && focused is not null)
        {
            Control target = to is not null && !busy ? rows.First(r => r.Key == to).Remove : toggle;
            MoveFocusBeforeHiding(focused.Remove, false, target);
        }

        if (busy)
        {
            // A disabled button cannot keep focus.
            foreach (var r in rows.Where(r => r.Remove.FocusState != FocusState.Unfocused))
            {
                MoveFocusBeforeHiding(r.Remove, false, toggle);
            }

            MoveFocusBeforeHiding(section.Capture!, false, toggle);
        }

        foreach (var gone in rows.Where(r => !keys.Contains(r.Key)).ToList())
        {
            body.Children.Remove(gone.Grid);
            rows.Remove(gone);
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var row = rows.FirstOrDefault(r => r.Key == keys[i]) ?? NewAntigravityRow(section, capture, keys[i], account);
            row.Label.Text = account.Label;
            row.Remove.IsEnabled = !busy;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                row.Remove, AntigravityAccountsCopy.RemoveNamed.Localized(account.Label));
            if (body.Children.IndexOf(row.Grid) != i)
            {
                body.Children.Remove(row.Grid);
                body.Children.Insert(i, row.Grid);
            }
        }

        section.Capture!.Content = (busy ? AntigravityAccountsCopy.Capturing : AntigravityAccountsCopy.Capture).Localized();
        section.Capture.IsEnabled = !busy;

        var code = capture.LastErrorCode;
        section.Error!.Text = code is null ? "" : AntigravityAccountsCopy.Message(code).Localized();
        section.Error.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;

        var enabled = capture.IsEnabled;
        // Not while the switch's own change is in flight (the confirmation
        // flyout is open with the switch already on): its handler redraws after.
        if (!section.Toggling && toggle.IsOn != enabled)
        {
            section.SyncingToggle = true;
            toggle.IsOn = enabled;
            section.SyncingToggle = false;
        }

        section.Paused!.Visibility = enabled && capture.Paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private static AntigravityAccountRow NewAntigravityRow(
        AgySection section, AntigravityAutoCapture capture, string rowKey, AntigravityAccount account)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.Text(account.Label, 12);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);

        var remove = new Button
        {
            Content = AntigravityAccountsCopy.Remove.Localized(),
            FontSize = 11,
            Padding = new Thickness(7, 2, 7, 3),
        };
        var key = account.Key;
        remove.Click += async (_, _) => await capture.Remove(key);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        var made = new AntigravityAccountRow(rowKey, label, remove, row);
        section.Rows.Add(made);
        return made;
    }

    private void BuildAntigravityControls(SettingsStore store, AgySection section, AntigravityAutoCapture capture)
    {
        var body = section.Body;
        section.Capture = new Button
        {
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        section.Capture.Click += async (_, _) => await capture.ManualCapture();
        body.Children.Add(section.Capture);

        section.Error = WarningLine("");
        section.Error.Visibility = Visibility.Collapsed;
        body.Children.Add(section.Error);

        var toggle = new ToggleSwitch { IsOn = capture.IsEnabled, OnContent = null, OffContent = null };
        section.Toggle = toggle;
        var reverting = false;
        toggle.Toggled += async (_, _) =>
        {
            if (reverting || section.SyncingToggle)
            {
                return;
            }

            section.Toggling = true;
            try
            {
                if (toggle.IsOn)
                {
                    if (!await capture.TurnOn(() => ConfirmAutoCapture(toggle)))
                    {
                        reverting = true;
                        toggle.IsOn = false;
                        reverting = false;
                    }
                }
                else
                {
                    await capture.SetEnabled(false);
                }
            }
            finally
            {
                section.Toggling = false;
            }

            FillAntigravityAccounts(store, section);
        };
        // The visible label is a sibling TextBlock; give the switch the same name.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, AntigravityAccountsCopy.Toggle.Localized());
        body.Children.Add(ToggleRow(AntigravityAccountsCopy.Toggle.Localized(), toggle));

        section.Paused = WarningLine(AntigravityAccountsCopy.Paused.Localized());
        section.Paused.Visibility = Visibility.Collapsed;
        body.Children.Add(section.Paused);
    }

    private static TextBlock WarningLine(string text)
    {
        var line = Ui.Dim(text, 11);
        line.Foreground = Ui.BrushFromHex(DashboardView.PaceOrange);
        return line;
    }

    /// <summary>The confirmation before automatic capture turns on, as a
    /// flyout on the toggle (no ContentDialog: see UpdateDialog's note on a
    /// second open throwing). Light dismiss counts as Cancel.</summary>
    private static Task<bool> ConfirmAutoCapture(FrameworkElement anchor) =>
        ConfirmFlyout(anchor, AntigravityAccountsCopy.ConfirmTitle, AntigravityAccountsCopy.ConfirmBody,
            AntigravityAccountsCopy.ConfirmOk, AntigravityAccountsCopy.ConfirmCancel);
}
