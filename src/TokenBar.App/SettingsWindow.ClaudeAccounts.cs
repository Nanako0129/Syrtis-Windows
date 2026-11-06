using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// Accounts page → "Claude" (macOS SettingsPanel claudeExtraRootsSection).
// Saving the list is the whole action: App's store observer queues the push
// (ClaudeRootsPusher), which also refreshes the dashboard when it lands.
public sealed partial class SettingsWindow
{
    private string? _claudeAccountsNotice;
    private bool _claudeAccountsSubscribed;
    private ClaudeSection? _claude;

    /// <summary>Everything one page build owns. Rebuild() makes a new body
    /// (every Present and some store keys), and a handler of an older build
    /// may still be running; each handler captures its own section and touches
    /// only that, so a stale one is inert and the window's Fill uses
    /// <see cref="_claude"/>.</summary>
    private sealed class ClaudeSection
    {
        public StackPanel Body { get; } = new() { Spacing = 6 };
        public List<ClaudeAccountRow> Rows { get; } = [];
        public Button Add { get; } = new()
        {
            Content = ClaudeAccountsCopy.Add.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        public TextBlock Notice { get; } = Ui.Dim("", 11);
    }

    /// <summary>One saved directory's row, kept across redraws (MA-43): the
    /// controls are updated in place so a focused Remove button survives a
    /// push or another row's removal.</summary>
    private sealed record ClaudeAccountRow(
        string Key, string Dir, StackPanel Panel, FontIcon Missing, Button Remove, TextBlock Why);

    private StackPanel BuildClaudeAccounts(SettingsStore store)
    {
        var section = _claude = new ClaudeSection();
        section.Add.Click += async (_, _) => await AddClaudeAccount(store, section);
        section.Notice.Foreground = Ui.BrushFromHex(DashboardView.PaceOrange);
        section.Notice.Visibility = Visibility.Collapsed;
        section.Body.Children.Add(section.Add);
        section.Body.Children.Add(section.Notice);
        if (!_claudeAccountsSubscribed && ClaudeExtraRoots.Shared is { } pusher)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _claudeAccountsSubscribed = true;
            pusher.Pushed += _ => DispatcherQueue.TryEnqueue(() =>
            {
                if (_claude is { } current)
                {
                    FillClaudeAccounts(store, current);
                }
            });
        }

        FillClaudeAccounts(store, section);
        return section.Body;
    }

    // Updates the rows in place: a row that stays keeps its controls (and so
    // keyboard focus); a row that goes hands focus to a neighbour first; only a
    // new directory builds controls (MA-43).
    private void FillClaudeAccounts(SettingsStore store, ClaudeSection section)
    {
        var body = section.Body;
        var add = section.Add;
        var noticeText = section.Notice;
        var rows = section.Rows;

        var dirs = ClaudeExtraRoots.Load(store);
        var keys = SectionRows.Keys(dirs);
        // Reasons belong to the list the last push read; a list changed since
        // then shows none until its own push lands.
        var last = ClaudeExtraRoots.Shared?.Last;
        var rejected = last is not null && last.Dirs.SequenceEqual(dirs)
            ? last.Rejected
            : new Dictionary<int, string>();

        var focused = rows.FirstOrDefault(r => r.Remove.FocusState != FocusState.Unfocused);
        var (move, to) = SectionRows.FocusHandoff([.. rows.Select(r => r.Key)], keys, focused?.Key);
        if (move && focused is not null)
        {
            Control target = to is not null ? rows.First(r => r.Key == to).Remove : add;
            MoveFocusBeforeHiding(focused.Remove, false, target);
        }

        foreach (var gone in rows.Where(r => !keys.Contains(r.Key)).ToList())
        {
            body.Children.Remove(gone.Panel);
            rows.Remove(gone);
        }

        for (var i = 0; i < dirs.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Key == keys[i]) ?? NewClaudeRow(store, section, keys[i], dirs[i]);
            if (body.Children.IndexOf(row.Panel) != i)
            {
                body.Children.Remove(row.Panel);
                body.Children.Insert(i, row.Panel);
            }

            if (rejected.TryGetValue(i, out var reason))
            {
                row.Why.Text = ClaudeAccountsCopy.Reason(reason).Localized();
                row.Why.Visibility = Visibility.Visible;
            }
            else
            {
                row.Why.Visibility = Visibility.Collapsed;
            }

            var dir = dirs[i];
            // Off the UI thread: a stat on a stalled network drive can block.
            // Only a path the registries accept is stat-ed (MayCheckExists):
            // never a UNC/WSL, rooted or drive-relative one, which already
            // shows its refusal reason.
            _ = Task.Run(() => !ClaudeExtraRoots.MayCheckExists(dir) || Directory.Exists(dir)).ContinueWith(
                exists =>
                {
                    if (exists.IsCompletedSuccessfully)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                            row.Missing.Visibility = exists.Result ? Visibility.Collapsed : Visibility.Visible);
                    }
                },
                TaskScheduler.Default);
        }

        noticeText.Text = _claudeAccountsNotice ?? "";
        noticeText.Visibility = _claudeAccountsNotice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private ClaudeAccountRow NewClaudeRow(SettingsStore store, ClaudeSection section, string key, string dir)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var path = Ui.Text(dir, 12);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(path, dir);
        grid.Children.Add(path);

        var missing = new FontIcon
        {
            Glyph = "", // Warning
            FontSize = 12,
            Foreground = Ui.BrushFromHex(DashboardView.PaceOrange),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(missing, ClaudeAccountsCopy.Missing.Localized());
        Grid.SetColumn(missing, 1);
        grid.Children.Add(missing);

        var remove = new Button
        {
            Content = ClaudeAccountsCopy.Remove.Localized(),
            FontSize = 11,
            Padding = new Thickness(7, 2, 7, 3),
        };
        // Every row's button reads "Remove"; the accessible name says which.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            remove, ClaudeAccountsCopy.RemoveNamed.Localized(dir));
        remove.Click += (_, _) =>
        {
            var next = ClaudeExtraRoots.Load(store).ToList();
            if (next.IndexOf(dir) is var index and >= 0)
            {
                next.RemoveAt(index);
                _claudeAccountsNotice = null;
                ClaudeExtraRoots.Save(store, next);
                FillClaudeAccounts(store, section);
            }
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var why = Ui.Dim("", 11);
        why.Foreground = Ui.BrushFromHex(DashboardView.PaceOrange);
        why.Visibility = Visibility.Collapsed;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(grid);
        panel.Children.Add(why);
        var row = new ClaudeAccountRow(key, dir, panel, missing, remove, why);
        section.Rows.Add(row);
        return row;
    }

    private async Task AddClaudeAccount(SettingsStore store, ClaudeSection section)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder;
        try
        {
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            DevLog.Write($"claudeAccounts picker failed: {ex.GetType().Name}");
            return;
        }

        if (folder?.Path is not { Length: > 0 } path)
        {
            return;
        }

        // The check is a blocking native call, so it runs off the UI thread;
        // a Remove during that await changes the list, so check again against
        // the list that will actually be saved.
        IReadOnlyList<string> dirs;
        string? reason;
        do
        {
            dirs = ClaudeExtraRoots.Load(store);
            var checkedList = dirs;
            reason = await Task.Run(() => ClaudeExtraRoots.UiRejection(path, checkedList));
        }
        while (!ClaudeExtraRoots.Load(store).SequenceEqual(dirs));

        if (reason is not null)
        {
            _claudeAccountsNotice = ClaudeAccountsCopy.Reason(reason).Localized();
            FillClaudeAccounts(store, section);
            return;
        }

        _claudeAccountsNotice = null;
        ClaudeExtraRoots.Save(store, [.. dirs, path]);
        FillClaudeAccounts(store, section);
    }
}
