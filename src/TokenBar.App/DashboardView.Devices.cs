using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenBar.Core;
using TokenBar.Interop;
// TokenBar.Core.Grid collides with the XAML Grid (as in DashboardView.xaml.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// The dashboard's device overlay (SA-4g): the header menu, the lens surfaces
// that show other devices' pages, the per-device card and the notes. Every
// decision is in DashboardDeviceLens (UI-free, tested); this file draws it.
//
// Isolation: the overlay's pages never enter DashboardModel, _snapshot,
// SetClientSelection or LocalRecordClients. An overlay lens is built from a
// LensFrame, a throwaway copy of this PC's snapshot whose graph/models/hourly/
// agents are the overlay's, together with the overlay's own client slice. The
// frame lives only for the duration of one build (WithFrame).
//
// Remote strings (device names, clients, models, agents) are shown only as
// TextBlock.Text / Run.Text through Ui.Text and friends; none is markup, a
// format string, a hyperlink, a localization key or an automation id.
public sealed partial class DashboardView
{
    /// <summary>What an overlay lens is built from: a copy of the snapshot
    /// carrying the overlay's page(s) and the overlay's own client slice.</summary>
    private sealed record LensFrame(
        DashboardModel.Snapshot Snapshot,
        IReadOnlyList<string> Clients,
        HashSet<string> Set,
        UsageStats? Stats,
        string? ModelsNote = null,
        ModelColorMap? Colors = null);

    private DeviceOverlayState? _overlay;
    private DeviceOverlaySync? _sync;
    private OverviewMergeState? _overviewMerge;
    private LensFrame? _frame;
    private LensFrame? _threeDFrame;
    private bool _quotaSummaryHasToday;
    private string _overlayRenderSignature = "";

    // The device menu. Controls are built when the rows change and the menu is
    // closed; everything else is updated on them in place. A row is never
    // rebuilt or re-parented inside its own Click (the 4f crash patterns).
    private string _deviceMenuStructure = "";
    private bool _deviceFlyoutOpen;
    private bool _deviceMenuDirty;
    private readonly List<(string? Peer, CheckBox Check, DeviceStatusView? Status)> _deviceRows = [];

    /// <summary>A device name in the menu's rows is cut at this width (the
    /// header button's own title is capped at 120 in DashboardView.xaml: macOS
    /// caps it at 150, but the Windows header shares 368 DIP with the wordmark,
    /// year and refresh button; chosen for the layout, not measured).</summary>
    private const double DeviceMenuTextWidth = 240;

    /// <summary>The info flyout's width (macOS 260).</summary>
    private const double DeviceInfoWidth = 260;

    private static readonly Thickness DeviceStatusIndent = new(28, 0, 0, 4);

    /// <summary>The one process-wide overlay, handed in by the app. Its events
    /// arrive on any thread and are queued to this dispatcher, never run inline:
    /// a toggle raises <c>Changed</c> synchronously inside its own Click, where
    /// rebuilding the menu would be the 4f crash pattern.</summary>
    public void BindOverlay(DeviceOverlayState overlay)
    {
        _overlay = overlay;
        _sync = new DeviceOverlaySync(overlay);
        // SnapshotChanged is not subscribed: every snapshot change is raised
        // with a Changed (the same Touch), and Changed re-reads all of it.
        overlay.Changed += () => _ = DispatcherQueue.TryEnqueue(OnOverlayChanged);
    }

    private void InitDeviceMenu()
    {
        ToolTipService.SetToolTip(DeviceButton, DeviceOverlayCopy.MenuHelp.Localized());
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DeviceInfoButton, DeviceOverlayCopy.AboutCombined.Localized());
        var menu = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) =>
        {
            _deviceFlyoutOpen = true;
            UpdateDeviceMenu(); // fresh ages; rows are only updated, never rebuilt, while open
        };
        menu.Closed += (_, _) =>
        {
            _deviceFlyoutOpen = false;
            if (_deviceMenuDirty)
            {
                // After the close, from the dispatcher: not from the flyout's own event.
                _ = DispatcherQueue.TryEnqueue(UpdateDeviceMenu);
            }
        };
        DeviceButton.Flyout = menu;

        var info = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        info.Opening += (_, _) => info.Content = BuildDeviceInfo();
        DeviceInfoButton.Flyout = info;
        UpdateRateCaption();
    }

    // ---- wiring -----------------------------------------------------------

    /// <summary>Asks for the lens on screen's pages when the key changed. A
    /// no-op while the flyout is closed or nothing but this PC is chosen.</summary>
    private void SyncOverlay() =>
        _ = _sync?.Sync(
            _view, _model?.Year, OverviewScope.SingleClient(_activeClientTab),
            DashboardDeviceLens.HiddenRaw(AppSettings.Store), _model?.RootsGeneration ?? 0);

    /// <summary>The queued handler of every overlay change.</summary>
    private void OnOverlayChanged()
    {
        if (_overlay is not { } overlay)
        {
            return;
        }

        SyncOverlay();
        UpdateDeviceMenu();
        UpdateRateCaption();
        UpdateRefreshControl(loading: _snapshot is null);
        if (!_flyoutVisible || _snapshot is null)
        {
            return;
        }

        // RenderContent records the signature it drew, so a render for another
        // reason (a lens switch) is not followed by a second one here, and a
        // change that arrived since the last draw is not skipped.
        if (OverlayRenderSignature() != _overlayRenderSignature)
        {
            RenderContent(animated: false);
        }
    }

    /// <summary>What the overlay contributes to the content on screen, or "" without one.</summary>
    private string OverlayRenderSignature() =>
        _overlay is { } overlay
            ? DashboardDeviceLens.RenderSignature(
                overlay, _view, _model?.Year, OverviewScope.SingleClient(_activeClientTab),
                DashboardDeviceLens.HiddenRaw(AppSettings.Store))
            : "";

    private void UpdateRateCaption() =>
        RateCaption.Text = "tok/min".Localized() + DashboardDeviceLens.LiveRateSuffix(_overlay);

    // ---- the menu ---------------------------------------------------------

    private void UpdateDeviceMenu()
    {
        var menu = DashboardDeviceLens.Menu(_overlay, DateTimeOffset.Now);
        if (menu is null || _overlay is null)
        {
            DeviceMenuHost.Visibility = Visibility.Collapsed;
            return;
        }

        DeviceMenuHost.Visibility = Visibility.Visible;
        DeviceTitle.Text = menu.Title;
        if (menu.Structure != _deviceMenuStructure)
        {
            if (_deviceFlyoutOpen)
            {
                _deviceMenuDirty = true;
            }
            else
            {
                RebuildDeviceMenu(menu);
            }
        }
        else
        {
            UpdateDeviceRows(menu);
        }

        var lensHasOverlay = DeviceOverlayRequests.KindsFor(_view).Count > 0;
        DeviceInfoButton.Visibility = _overlay.IncludesRemote && lensHasOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(DeviceInfoButton, string.Join(" ", DeviceExplanations()));
    }

    private IReadOnlyList<string> DeviceExplanations()
    {
        var rangeLimited = _overlay is { } overlay
            && overlay.RangeLimited(
                _view, _model?.Year,
                DashboardDeviceLens.Clients(
                    OverviewScope.SingleClient(_activeClientTab), DashboardDeviceLens.HiddenRaw(AppSettings.Store)));
        return DeviceOverlayCopy.Explanations(rangeLimited);
    }

    private FrameworkElement BuildDeviceInfo()
    {
        var panel = new StackPanel { Spacing = 8, Width = DeviceInfoWidth };
        foreach (var sentence in DeviceExplanations())
        {
            panel.Children.Add(new TextBlock { Text = sentence, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        return panel;
    }

    /// <summary>Builds the rows' controls: only while the menu is closed.</summary>
    private void RebuildDeviceMenu(DeviceMenuState menu)
    {
        if (_overlay is not { } overlay || DeviceButton.Flyout is not Flyout flyout)
        {
            return;
        }

        _deviceRows.Clear();
        var panel = new StackPanel { Spacing = 2, MinWidth = 220 };
        foreach (var row in menu.Rows)
        {
            var peer = row.Peer;
            var check = new CheckBox
            {
                // The name is another device's data: a TextBlock's Text.
                Content = new TextBlock
                {
                    Text = row.Name,
                    MaxWidth = DeviceMenuTextWidth,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                MinWidth = 0,
            };
            // Click, not Checked: only a user toggle raises it. The control
            // flips itself; the queued overlay change sets the real state.
            check.Click += (_, _) =>
            {
                if (peer is null)
                {
                    overlay.ToggleLocal();
                }
                else
                {
                    overlay.TogglePeer(peer);
                }
            };
            panel.Children.Add(check);
            DeviceStatusView? status = null;
            if (row.Status is not null)
            {
                status = new DeviceStatusView(10);
                status.Root.Margin = DeviceStatusIndent;
                panel.Children.Add(status.Root);
            }

            _deviceRows.Add((peer, check, status));
        }

        flyout.Content = panel;
        _deviceMenuStructure = menu.Structure;
        _deviceMenuDirty = false;
        UpdateDeviceRows(menu);
    }

    /// <summary>Checked, enabled and status of the existing controls.</summary>
    private void UpdateDeviceRows(DeviceMenuState menu)
    {
        if (menu.Rows.Count != _deviceRows.Count)
        {
            return;
        }

        for (var i = 0; i < menu.Rows.Count; i++)
        {
            var (row, controls) = (menu.Rows[i], _deviceRows[i]);
            controls.Check.IsChecked = row.Checked;
            controls.Check.IsEnabled = row.CanToggle;
            if (controls.Status is not null && row.Status is { } status)
            {
                controls.Status.Update(status);
            }
        }
    }

    // ---- lens frames ------------------------------------------------------

    private T WithFrame<T>(LensFrame frame, Func<T> build)
    {
        var previous = _frame;
        _frame = frame;
        try
        {
            return build();
        }
        finally
        {
            _frame = previous;
        }
    }

    /// <summary>A throwaway copy of this PC's snapshot carrying the overlay's
    /// pages. <paramref name="authoritative"/> comes from
    /// <see cref="DashboardDeviceLens.Authoritative"/> (the overlay graph's own
    /// pricing meta, never assumed). <paramref name="replaceModels"/>: Models is the overlay's (possibly
    /// absent) report; otherwise this PC's stays, for colours only.</summary>
    private static DashboardModel.Snapshot OverlaySnapshot(
        DashboardModel.Snapshot local, UsagePayload? graph, ModelReport? models, bool replaceModels,
        bool authoritative, HourlyReport? hourly = null, AgentsReport? agents = null)
    {
        var id = local.RequestId ?? default;
        var snapshot = local with
        {
            CostAuthoritative = authoritative,
            RequestId = id,
            ModelsRequestId = id,
            ModelsRankByCost = authoritative,
        };
        if (graph is not null)
        {
            snapshot = snapshot with { Graph = graph };
        }

        if (replaceModels)
        {
            snapshot = snapshot with { Models = models };
        }

        if (hourly is not null)
        {
            snapshot = snapshot with { Hourly = hourly, HourlyAttempted = true };
        }

        if (agents is not null)
        {
            snapshot = snapshot with { Agents = agents, AgentsAttempted = true };
        }

        return snapshot;
    }

    private UIElement BuildOverlayLens(LensChoice<DashboardModel.Snapshot> choice, DashboardModel.Snapshot local)
    {
        if (choice.Source != LensSource.Overlay)
        {
            return DeviceLensLine(
                (choice.Source == LensSource.Failed
                    ? DeviceOverlayCopy.FailedNoData
                    : DeviceOverlayCopy.Loading).Localized());
        }

        var authoritative = DashboardDeviceLens.Authoritative(
            choice.Graph, _overlay, _model?.Year, local.CostAuthoritative);
        var snapshot = OverlaySnapshot(
            local, choice.Graph, choice.Models, replaceModels: _view is AppView.Models or AppView.Stats,
            authoritative, choice.Hourly, choice.Agents);
        var ids = choice.ClientIdList;
        var frame = new LensFrame(
            snapshot, ids, ids.ToHashSet(StringComparer.Ordinal), choice.Stats, choice.ModelsNote, choice.Colors);
        var lens = WithFrame(frame, () => _view switch
        {
            AppView.Models => BuildModels(snapshot),
            AppView.Monthly => BuildMonthly(snapshot),
            AppView.Daily => BuildDaily(snapshot),
            AppView.Hourly => BuildHourly(snapshot),
            AppView.Stats => BuildStats(snapshot),
            AppView.Agents => BuildAgents(snapshot),
            _ => throw new InvalidOperationException($"No overlay lens for {_view}"),
        });

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(CombinedBadge(choice.DeviceCount));
        if (choice.NoteLines.Count > 0)
        {
            stack.Children.Add(DeviceNoteLines(choice.NoteLines));
        }

        stack.Children.Add(lens);
        if (choice.PerDeviceCard && choice.Devices is { } devices)
        {
            stack.Children.Add(BuildDeviceCard(devices, ids, authoritative));
        }

        return stack;
    }

    // ---- Overview's combined cards ----------------------------------------

    private UIElement BuildMergedChart(OverviewMergeState merge, DashboardModel.Snapshot local)
    {
        UIElement body;
        if (merge.Graph is { } graph)
        {
            var snapshot = OverlaySnapshot(
                local, graph.Payload, merge.Models ?? local.Models, replaceModels: true,
                CostSurfaceProjection.IsAuthoritative(graph.Payload));
            var frame = new LensFrame(
                snapshot, merge.ClientIds, merge.ClientIds.ToHashSet(StringComparer.Ordinal), graph.Stats,
                Colors: merge.Colors);
            body = WithFrame(frame, () => BuildUsageChartCard(snapshot));
        }
        else
        {
            body = DeviceLoadingCard("Token Usage".Localized(), merge.GraphFailed);
        }

        return Combined(merge.DeviceCount, body);
    }

    private UIElement BuildMergedModels(OverviewMergeState merge, DashboardModel.Snapshot local, string? singleClient)
    {
        var title = OverviewScope.ModelsTitle(singleClient);
        UIElement body;
        switch (OverviewMerge.ModelsCard(merge.Models is not null, merge.ModelsFailed))
        {
            case OverviewMerge.ModelsCardState.Card:
                var snapshot = OverlaySnapshot(
                    local, merge.Graph?.Payload, merge.Models, replaceModels: true,
                    DashboardDeviceLens.Authoritative(
                        merge.Graph?.Payload, _overlay, _model?.Year, local.CostAuthoritative));
                var frame = new LensFrame(
                    snapshot, merge.ModelClientIds, merge.ModelClientIds.ToHashSet(StringComparer.Ordinal), null,
                    Colors: merge.Colors);
                body = Ui.Card(title, WithFrame(frame, () => BuildModelRows(snapshot, collapsible: true)));
                break;
            case OverviewMerge.ModelsCardState.Failed:
                body = DeviceLoadingCard(title, failed: true);
                break;
            default:
                body = DeviceLoadingCard(title, failed: false);
                break;
        }

        return Combined(merge.DeviceCount, body);
    }

    private UIElement BuildMergedStreaks(OverviewMergeState merge, DashboardModel.Snapshot local)
    {
        UIElement body;
        if (merge.Graph is { } graph)
        {
            var snapshot = OverlaySnapshot(
                local, graph.Payload, merge.Models ?? local.Models, replaceModels: true,
                CostSurfaceProjection.IsAuthoritative(graph.Payload));
            var frame = new LensFrame(
                snapshot, merge.ClientIds, merge.ClientIds.ToHashSet(StringComparer.Ordinal), graph.Stats,
                Colors: merge.Colors);
            body = Ui.Card("Streaks".Localized(), WithFrame(frame, () => BuildStreaks(snapshot)));
        }
        else
        {
            body = DeviceLoadingCard("Streaks".Localized(), merge.GraphFailed);
        }

        return Combined(merge.DeviceCount, body);
    }

    // ---- pieces -----------------------------------------------------------

    private static TextBlock CombinedBadge(int count)
    {
        var badge = Ui.Text(DeviceOverlayCopy.Combined.Localized(count), 10, 0.6);
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        return badge;
    }

    /// <summary>A combined card with its "N devices combined" label above it.</summary>
    private static StackPanel Combined(int count, UIElement card)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(CombinedBadge(count));
        stack.Children.Add(card);
        return stack;
    }

    /// <summary>One caption line per sentence. The sentences carry other
    /// devices' names as plain text.</summary>
    private static StackPanel DeviceNoteLines(IReadOnlyList<string> lines)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var line in lines)
        {
            stack.Children.Add(Ui.Dim(line, 10));
        }

        return stack;
    }

    /// <summary>A page that is not there: a fixed line, never this PC's own data.</summary>
    private static FrameworkElement DeviceLensLine(string text)
    {
        var line = Ui.Dim(text, 11);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.VerticalAlignment = VerticalAlignment.Center;
        return new Border { MinHeight = 80, Child = line };
    }

    private static UIElement DeviceLoadingCard(string title, bool failed) =>
        Ui.Card(title, Ui.Dim(
            (failed ? DeviceOverlayCopy.FailedNoData : DeviceOverlayCopy.Loading).Localized()));

    /// <summary>The "Per device" card: name, messages and tokens, time (this
    /// PC), the short status (other devices), cost. Remote names and figures
    /// are only text.</summary>
    private UIElement BuildDeviceCard(
        IReadOnlyList<RemoteDevice> devices, IReadOnlyList<string> clientIds, bool authoritative)
    {
        var rows = DeviceTotals.Rows(devices, clientIds, _overlay?.LearnedNames);
        var statusRows = _overlay?.Snapshot.Rows ?? devices;
        var inFlight = _overlay?.Fetching == true;
        var now = DateTimeOffset.Now;
        var panel = new StackPanel { Spacing = 8 };
        foreach (var row in rows)
        {
            var left = new StackPanel { Spacing = 1 };
            left.Children.Add(Ui.MiddleText(row.Name, 11, bold: true));
            // The short status below carries the time for another device.
            left.Children.Add(Ui.Text(DeviceTotals.Detail(row, now, showsUpdated: row.IsLocal), 10, 0.6));
            if (!row.IsLocal)
            {
                left.Children.Add(DeviceStatusView.Make(DeviceOverlayCopy.ShortStatus(
                    DeviceOverlayCopy.WorstRow(row.Id, statusRows), selected: true, inFlight, now)));
            }

            // Behind the cost gate like the combined card above it.
            var cost = Ui.Text(DashboardDeviceLens.DeviceCostText(row, authoritative), 11);
            cost.VerticalAlignment = VerticalAlignment.Top;
            panel.Children.Add(Ui.Row(left, cost));
        }

        return Ui.Card(DeviceOverlayCopy.PerDevice.Localized(), panel);
    }

    /// <summary>Above the lens: the status lines (one per device that is not
    /// fresh) and, on Quota, the note that window usage is this PC's alone.</summary>
    private UIElement WithDeviceHeader(UIElement content)
    {
        if (_overlay is not { } overlay)
        {
            return content;
        }

        var lines = overlay.StatusLines(
            _view, _model?.Year,
            DashboardDeviceLens.Clients(
                OverviewScope.SingleClient(_activeClientTab), DashboardDeviceLens.HiddenRaw(AppSettings.Store)));
        var quotaNote = _view == AppView.Quota ? DashboardDeviceLens.QuotaNotes(overlay).QuotaLens : null;
        if (lines.Count == 0 && quotaNote is null)
        {
            return content;
        }

        var stack = new StackPanel { Spacing = 8 };
        if (lines.Count > 0)
        {
            stack.Children.Add(DeviceNoteLines(lines));
        }

        if (quotaNote is not null)
        {
            var note = Ui.Dim(quotaNote, 10);
            note.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(note);
        }

        stack.Children.Add(content);
        return stack;
    }

    /// <summary>The quota summary's "this PC only" notes: a caption above the
    /// card and, when it has its "Today" row, a second one in its corner.</summary>
    private UIElement? WithQuotaNote(FrameworkElement card)
    {
        var notes = DashboardDeviceLens.QuotaNotes(_overlay);
        // An empty StackPanel is the all-hidden answer: no card, so no note.
        if (notes.Summary is null || card is StackPanel)
        {
            return card;
        }

        var stack = new StackPanel { Spacing = 4 };
        var above = Ui.Dim(notes.Summary, 10);
        above.HorizontalAlignment = HorizontalAlignment.Right;
        stack.Children.Add(above);
        if (_quotaSummaryHasToday && notes.Corner is { } corner)
        {
            var host = new Grid();
            host.Children.Add(card);
            host.Children.Add(new TextBlock
            {
                Text = corner,
                FontSize = 10,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 12, 8),
                IsHitTestVisible = false,
            });
            stack.Children.Add(host);
        }
        else
        {
            stack.Children.Add(card);
        }

        return stack;
    }
}
