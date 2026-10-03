using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TokenBar.Core;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// General page → "Antigravity accounts" (macOS SettingsPanel
// antigravityAccountsSection). Every action goes through
// AntigravityAutoCapture.Shared; this only draws its state and the stored list.
public sealed partial class SettingsWindow
{
    private StackPanel? _antigravityBody;
    private bool _antigravitySubscribed;

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

    private void FillAntigravityAccounts(SettingsStore store)
    {
        if (_antigravityBody is not { } body || AntigravityAutoCapture.Shared is not { } capture)
        {
            return;
        }

        body.Children.Clear();
        var busy = capture.Busy;
        foreach (var account in AntigravityAccounts.Load(store))
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
                IsEnabled = !busy,
            };
            var key = account.Key;
            remove.Click += async (_, _) => await capture.Remove(key);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            body.Children.Add(row);
        }

        var captureButton = new Button
        {
            Content = (busy ? AntigravityAccountsCopy.Capturing : AntigravityAccountsCopy.Capture).Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
            IsEnabled = !busy,
        };
        captureButton.Click += async (_, _) => await capture.ManualCapture();
        body.Children.Add(captureButton);

        if (capture.LastErrorCode is { } code)
        {
            body.Children.Add(WarningLine(AntigravityAccountsCopy.Message(code).Localized()));
        }

        var enabled = capture.IsEnabled;
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = null, OffContent = null };
        var reverting = false;
        toggle.Toggled += async (_, _) =>
        {
            if (reverting)
            {
                return;
            }

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

            FillAntigravityAccounts(store);
        };
        body.Children.Add(ToggleRow(AntigravityAccountsCopy.Toggle.Localized(), toggle));

        if (enabled && capture.Paused)
        {
            body.Children.Add(WarningLine(AntigravityAccountsCopy.Paused.Localized()));
        }

        if (!enabled)
        {
            body.Children.Add(Hint(AntigravityAccountsCopy.MergeOnlyUntilRefresh.Localized()));
        }

        foreach (var hint in new[]
                 {
                     AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.HowTo, AntigravityAccountsCopy.Reads,
                     AntigravityAccountsCopy.RemoveMeans, AntigravityAccountsCopy.TwiceFromIde,
                 })
        {
            body.Children.Add(Hint(hint.Localized()));
        }
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
    private static Task<bool> ConfirmAutoCapture(FrameworkElement anchor)
    {
        var answer = new TaskCompletionSource<bool>();
        var panel = new StackPanel { Spacing = 10, MaxWidth = 320 };
        foreach (var line in new[]
                 {
                     Ui.Text(AntigravityAccountsCopy.ConfirmTitle.Localized(), 13, bold: true),
                     Ui.Text(AntigravityAccountsCopy.ConfirmBody.Localized(), 12),
                 })
        {
            line.TextWrapping = TextWrapping.Wrap;
            line.TextTrimming = TextTrimming.None;
            panel.Children.Add(line);
        }

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        var cancel = new Button { Content = AntigravityAccountsCopy.ConfirmCancel.Localized() };
        var ok = new Button
        {
            Content = AntigravityAccountsCopy.ConfirmOk.Localized(),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        cancel.Click += (_, _) => flyout.Hide();
        ok.Click += (_, _) =>
        {
            answer.TrySetResult(true);
            flyout.Hide();
        };
        flyout.Closed += (_, _) => answer.TrySetResult(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        flyout.ShowAt(anchor);
        return answer.Task;
    }
}
