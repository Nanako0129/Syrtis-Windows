using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// General page → "Claude accounts" (macOS SettingsPanel claudeExtraRootsSection).
// Saving the list is the whole action: App's store observer queues the push
// (ClaudeRootsPusher), which also refreshes the dashboard when it lands.
public sealed partial class SettingsWindow
{
    private StackPanel? _claudeAccountsBody;
    private string? _claudeAccountsNotice;
    private bool _claudeAccountsSubscribed;

    private StackPanel BuildClaudeAccounts(SettingsStore store)
    {
        _claudeAccountsBody = new StackPanel { Spacing = 6 };
        if (!_claudeAccountsSubscribed && ClaudeExtraRoots.Shared is { } pusher)
        {
            // The window is hidden, never closed, so one subscription lives as
            // long as the app.
            _claudeAccountsSubscribed = true;
            pusher.Pushed += _ => DispatcherQueue.TryEnqueue(() => FillClaudeAccounts(store));
        }

        FillClaudeAccounts(store);
        return _claudeAccountsBody;
    }

    private void FillClaudeAccounts(SettingsStore store)
    {
        if (_claudeAccountsBody is not { } body)
        {
            return;
        }

        body.Children.Clear();
        var dirs = ClaudeExtraRoots.Load(store);
        // Reasons belong to the list the last push read; a list changed since
        // then shows none until its own push lands.
        var last = ClaudeExtraRoots.Shared?.Last;
        var rejected = last is not null && last.Dirs.SequenceEqual(dirs)
            ? last.Rejected
            : new Dictionary<int, string>();

        for (var i = 0; i < dirs.Count; i++)
        {
            var dir = dirs[i];
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var path = Ui.Text(dir, 12);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetToolTip(path, dir);
            row.Children.Add(path);

            var missing = new FontIcon
            {
                Glyph = "", // Warning
                FontSize = 12,
                Foreground = Ui.BrushFromHex(DashboardView.PaceOrange),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(missing, ClaudeAccountsCopy.Missing.Localized());
            Grid.SetColumn(missing, 1);
            row.Children.Add(missing);
            // Off the UI thread: a stat on a stalled network drive can block.
            // Only a path the registries accept is stat-ed (MayCheckExists):
            // never a UNC/WSL, rooted or drive-relative one, which already
            // shows its refusal reason.
            _ = Task.Run(() => !ClaudeExtraRoots.MayCheckExists(dir) || Directory.Exists(dir)).ContinueWith(
                exists =>
                {
                    if (exists.IsCompletedSuccessfully && !exists.Result)
                    {
                        DispatcherQueue.TryEnqueue(() => missing.Visibility = Visibility.Visible);
                    }
                },
                TaskScheduler.Default);

            var remove = new Button
            {
                Content = ClaudeAccountsCopy.Remove.Localized(),
                FontSize = 11,
                Padding = new Thickness(7, 2, 7, 3),
            };
            var index = i;
            remove.Click += (_, _) =>
            {
                var next = ClaudeExtraRoots.Load(store).ToList();
                if (index < next.Count && next[index] == dir)
                {
                    next.RemoveAt(index);
                    _claudeAccountsNotice = null;
                    ClaudeExtraRoots.Save(store, next);
                    FillClaudeAccounts(store);
                }
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            body.Children.Add(row);

            if (rejected.TryGetValue(i, out var reason))
            {
                var why = Ui.Dim(ClaudeAccountsCopy.Reason(reason).Localized(), 11);
                why.Foreground = Ui.BrushFromHex(DashboardView.PaceOrange);
                body.Children.Add(why);
            }
        }

        var add = new Button
        {
            Content = ClaudeAccountsCopy.Add.Localized(),
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 4),
        };
        add.Click += async (_, _) => await AddClaudeAccount(store);
        body.Children.Add(add);

        if (_claudeAccountsNotice is { } notice)
        {
            var text = Ui.Dim(notice, 11);
            text.Foreground = Ui.BrushFromHex(DashboardView.PaceOrange);
            body.Children.Add(text);
        }

        foreach (var hint in new[]
                 {
                     ClaudeAccountsCopy.HowTo, ClaudeAccountsCopy.Reads, ClaudeAccountsCopy.Transport,
                     ClaudeAccountsCopy.Locations, ClaudeAccountsCopy.RemoveMeans,
                 })
        {
            body.Children.Add(Hint(hint.Localized()));
        }
    }

    private async Task AddClaudeAccount(SettingsStore store)
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

        var dirs = ClaudeExtraRoots.Load(store);
        if (ClaudeExtraRoots.UiRejection(path, dirs) is { } reason)
        {
            _claudeAccountsNotice = ClaudeAccountsCopy.Reason(reason).Localized();
            FillClaudeAccounts(store);
            return;
        }

        _claudeAccountsNotice = null;
        ClaudeExtraRoots.Save(store, [.. dirs, path]);
        FillClaudeAccounts(store);
    }
}
