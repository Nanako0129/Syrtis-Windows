using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// Accounts page → "Antigravity" (macOS SettingsPanel
// antigravityAccountsSection). Every action goes through
// AntigravityAutoCapture.Shared; this only draws its state and the stored list.
public sealed partial class SettingsWindow
{
    private StackPanel? _antigravityBody;
    private bool _antigravitySubscribed;
    private readonly List<AntigravityAccountRow> _antigravityRows = [];
    private Button? _agyCapture;
    private TextBlock? _agyError;
    private ToggleSwitch? _agyToggle;
    private TextBlock? _agyPaused;
    private bool _agySyncingToggle;
    private bool _agyToggling;

    /// <summary>One captured account's row, kept across redraws (MA-43).</summary>
    private sealed record AntigravityAccountRow(string Key, TextBlock Label, Button Remove, Grid Grid);

    private StackPanel BuildAntigravityAccounts(SettingsStore store)
    {
        _antigravityBody = new StackPanel { Spacing = 6 };
        if (!_antigravitySubscribed && AntigravityAutoCapture.Shared is { } capture)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _antigravitySubscribed = true;
            capture.StateChanged += () => DispatcherQueue.TryEnqueue(() => FillAntigravityAccounts(store));
        }

        FillAntigravityAccounts(store);
        return _antigravityBody;
    }

    // The fixed controls (Capture, the error line, the switch, the paused line)
    // are built once and updated in place; a row exists while its account does.
    // A focused control that is about to be removed or disabled hands focus to
    // the switch first (MA-43: a rebuild dropped keyboard focus, so Tab+Space on
    // the switch lost it at once).
    private void FillAntigravityAccounts(SettingsStore store)
    {
        if (_antigravityBody is not { } body || AntigravityAutoCapture.Shared is not { } capture)
        {
            return;
        }

        if (_agyToggle is null)
        {
            BuildAntigravityControls(store, body, capture);
        }

        var toggle = _agyToggle!;
        var busy = capture.Busy;
        var accounts = AntigravityAccounts.Load(store);
        var keys = accounts.Select(a => a.Key).ToList();

        var focused = _antigravityRows.FirstOrDefault(r => r.Remove.FocusState != FocusState.Unfocused);
        var (move, to) = SectionRows.FocusHandoff([.. _antigravityRows.Select(r => r.Key)], keys, focused?.Key);
        if (move && focused is not null)
        {
            Control target = to is not null && !busy ? _antigravityRows.First(r => r.Key == to).Remove : toggle;
            MoveFocusBeforeHiding(focused.Remove, false, target);
        }

        if (busy)
        {
            // A disabled button cannot keep focus.
            foreach (var r in _antigravityRows.Where(r => r.Remove.FocusState != FocusState.Unfocused))
            {
                MoveFocusBeforeHiding(r.Remove, false, toggle);
            }

            MoveFocusBeforeHiding(_agyCapture!, false, toggle);
        }

        foreach (var gone in _antigravityRows.Where(r => !keys.Contains(r.Key)).ToList())
        {
            body.Children.Remove(gone.Grid);
            _antigravityRows.Remove(gone);
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var row = _antigravityRows.FirstOrDefault(r => r.Key == account.Key) ?? NewAntigravityRow(capture, account);
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

        _agyCapture!.Content = (busy ? AntigravityAccountsCopy.Capturing : AntigravityAccountsCopy.Capture).Localized();
        _agyCapture.IsEnabled = !busy;

        var code = capture.LastErrorCode;
        _agyError!.Text = code is null ? "" : AntigravityAccountsCopy.Message(code).Localized();
        _agyError.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;

        var enabled = capture.IsEnabled;
        // Not while the switch's own change is in flight (the confirmation
        // flyout is open with the switch already on): its handler redraws after.
        if (!_agyToggling && toggle.IsOn != enabled)
        {
            _agySyncingToggle = true;
            toggle.IsOn = enabled;
            _agySyncingToggle = false;
        }

        _agyPaused!.Visibility = enabled && capture.Paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private AntigravityAccountRow NewAntigravityRow(AntigravityAutoCapture capture, AntigravityAccount account)
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
        var made = new AntigravityAccountRow(key, label, remove, row);
        _antigravityRows.Add(made);
        return made;
    }

    private void BuildAntigravityControls(SettingsStore store, StackPanel body, AntigravityAutoCapture capture)
    {
        _agyCapture = new Button
        {
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        _agyCapture.Click += async (_, _) => await capture.ManualCapture();
        body.Children.Add(_agyCapture);

        _agyError = WarningLine("");
        _agyError.Visibility = Visibility.Collapsed;
        body.Children.Add(_agyError);

        var toggle = new ToggleSwitch { IsOn = capture.IsEnabled, OnContent = null, OffContent = null };
        _agyToggle = toggle;
        var reverting = false;
        toggle.Toggled += async (_, _) =>
        {
            if (reverting || _agySyncingToggle)
            {
                return;
            }

            _agyToggling = true;
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
                _agyToggling = false;
            }

            FillAntigravityAccounts(store);
        };
        // The visible label is a sibling TextBlock; give the switch the same name.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, AntigravityAccountsCopy.Toggle.Localized());
        body.Children.Add(ToggleRow(AntigravityAccountsCopy.Toggle.Localized(), toggle));

        _agyPaused = WarningLine(AntigravityAccountsCopy.Paused.Localized());
        _agyPaused.Visibility = Visibility.Collapsed;
        body.Children.Add(_agyPaused);
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
