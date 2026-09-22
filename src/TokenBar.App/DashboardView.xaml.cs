using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.UI;
// TokenBar.Core.Grid (the contribution-grid builder) collides with the XAML
// Grid — same clash the macOS port hit with SwiftUI's GridLayout.
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>
/// The dashboard shell: global header, lens switch, and the seven lenses of
/// the macOS AppView router, each built in code from the current snapshot.
/// </summary>
public sealed partial class DashboardView : UserControl
{
    private DashboardModel.Snapshot? _snapshot;
    private DashboardModel? _model;
    private AppView _view = AppView.Overview;
    private readonly Dictionary<AppView, Button> _tabs = [];
    private IReadOnlyList<string> _displayClients = [];
    private IReadOnlyList<string> _selectedClients = [];
    private HashSet<string> _selectedSet = new(StringComparer.Ordinal);
    private UsageStats? _selectedStats;
    private string _activeClientTab = ClientRegistry.OverviewTab;
    private string _clientTabsSignature = "";
    private string? _expandedDay; // Daily drill-down state
    private string? _expandedMonth; // Monthly drill-down state
    private bool _hourlyProfileMode;
    private int _hourlyWindow = 48; // Timeline rows shown; +48 per "Show more"
    // Chart toggles, persisted with the macOS rawValue strings.
    private StackBy _chartStackBy =
        AppSettings.Store.GetString("tokenbar.chart.stackBy") == "agent"
            ? StackBy.Agent : StackBy.Model;
    private ChartMetric _chartMetric =
        AppSettings.Store.GetString("tokenbar.chart.metric") == "cost"
            ? ChartMetric.Cost : ChartMetric.Tokens;
    private bool _chartView3D =
        AppSettings.Store.GetString("tokenbar.chart.view", "2d") == "3d";

    public DashboardView()
    {
        InitializeComponent();
        ProductTitle.Text = ProductIdentity.Name;
        // WinUI otherwise synthesizes a tooltip containing "Esc" for the
        // dashboard-wide Escape accelerator whenever the pointer rests over
        // the graph. The accelerator remains active; only its automatic
        // tooltip chrome is suppressed.
        KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        foreach (var view in Enum.GetValues<AppView>())
        {
            var button = new Button
            {
                // AppViews.Label, not view.ToString(): the settings page already
                // routes through it, and the enum name alone renders English.
                Content = AppViews.Label(view),
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => SwitchTo(view);
            _tabs[view] = button;
            TabsPanel.Children.Add(button);
        }

        ApplyLensVisibility();
        UpdateTabChrome();

        AddHandler(
            PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWheel),
            handledEventsToo: true);

        // XAML attribute values cannot call Localized(), and adding x:Uid/PRI
        // resources for five strings would mean shipping a second translation
        // system beside the JSON table. They are assigned here instead, at
        // construction, before the control is ever measured.
        TodayCaption.Text = "today".Localized();
        TotalCaption.Text = "total".Localized();
        RateCaption.Text = "tok/min".Localized();
        YearButton.Content = "All".Localized();
        QuitButton.Content = "Quit".Localized();

        RefreshButton.Click += (_, _) =>
        {
            _model?.RefreshForce();
            UpdateRefreshControl();
        };
        HoverTip.Attach(RefreshButton, () => "Refresh usage data".Localized());

        SettingsButton.Click += (_, _) => TrayService.OpenSettings?.Invoke();
        HoverTip.Attach(SettingsButton, () => "Settings".Localized());
        QuitButton.Click += (_, _) => TrayService.QuitApp?.Invoke();

        // Limits/trace settings re-render the open flyout live (the macOS
        // panel's right-column preview equivalent is the flyout itself).
        AppSettings.Store.Changed += key =>
        {
            if (key is ClientRegistry.TabHiddenKey or ClientRegistry.TabOrderKey)
            {
                _ = DispatcherQueue.TryEnqueue(() => RefreshSelection(animated: false));
            }
            else if (key == AppViews.HiddenKey)
            {
                _ = DispatcherQueue.TryEnqueue(ApplyLensVisibility);
            }
            else if (key.StartsWith("tokenbar.limits.", StringComparison.Ordinal)
                || key == "tokenbar.trace.detailed")
            {
                _ = DispatcherQueue.TryEnqueue(() => RenderContent(animated: false));
            }
        };

        // In-flyout shortcuts, the macOS ⌘ set on Ctrl: Esc/Ctrl+W close,
        // Ctrl+R refresh, Ctrl+, settings, Ctrl+Q quit, Ctrl+1..8 lenses,
        // Ctrl+[ / Ctrl+] cycle.
        // Esc yields to an open transient (the year menu): light-dismiss
        // should collapse the popup, not slide the whole flyout away.
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape,
        };
        escape.Invoked += (_, e) =>
        {
            // Yield only to a REAL transient (the year MenuFlyout). The HoverTip
            // tooltip is also an open popup for this XamlRoot but must not
            // swallow Esc, or Esc silently fails to close the flyout whenever a
            // tooltip happens to be showing.
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper
                .GetOpenPopupsForXamlRoot(XamlRoot).Any(p => !HoverTip.IsHoverPopup(p)))
            {
                return; // unhandled: the real popup takes it
            }

            HideRequested?.Invoke();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        AddAccel(Windows.System.VirtualKey.W, Windows.System.VirtualKeyModifiers.Control,
            () => HideRequested?.Invoke());
        AddAccel(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () =>
            {
                _model?.RefreshForce();
                UpdateRefreshControl();
            });
        AddAccel(Windows.System.VirtualKey.G, Windows.System.VirtualKeyModifiers.Control,
            ToggleChartView);
        AddAccel(Windows.System.VirtualKey.Q, Windows.System.VirtualKeyModifiers.Control,
            () => TrayService.QuitApp?.Invoke());
        var lenses = Enum.GetValues<AppView>();
        for (var i = 0; i < lenses.Length && i < 9; i++)
        {
            var view = lenses[i];
            AddAccel(Windows.System.VirtualKey.Number1 + i,
                Windows.System.VirtualKeyModifiers.Control, () => SwitchTo(view));
        }

        // WinRT's VirtualKey enum has no OEM members, so casting VK_OEM_4 /
        // VK_OEM_6 / VK_OEM_COMMA into it hands KeyboardAccelerator a value it
        // never matches — which is exactly why the numeric accelerators worked (Number1 is a
        // real member) while Ctrl+[ , Ctrl+] and Ctrl+, silently did nothing.
        // KeyDown carries the raw Win32 virtual-key code, so compare the
        // underlying integer instead. handledEventsToo keeps this working even
        // if a child control has already marked the event handled.
        AddHandler(
            KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnOemShortcut),
            handledEventsToo: true);

        ActualThemeChanged += (_, _) => UpdateGraph3DData(_snapshot);
    }

    /// <summary>The flyout owns hiding (Esc/Ctrl+W land here).</summary>
    public event Action? HideRequested;

    // ── 3D contribution graph ────────────────────────────────────────────

    private Graph3DPanel? _graph3d;
    private Border? _graph3dContentHost;
    private bool _graph3dDevMode;
    private bool _flyoutVisible;

    /// <summary>Mount the 3D contribution-graph panel and reveal its host.
    /// Dev-only Gate 0 path used by --graph3d / --soak3d. The normal product
    /// path mounts the same panel inside the Token Usage card.</summary>
    public void EnableGraph3D()
    {
        if (_graph3dDevMode)
        {
            return;
        }

        _graph3dDevMode = true;
        var panel = EnsureGraph3D();
        DetachGraph3DContentHost();
        _graph3d = panel;
        Graph3DHost.Child = _graph3d;
        Graph3DHost.Visibility = Visibility.Visible;
        UpdateGraph3DData(_snapshot);
        SyncGraph3DActivity();
    }

    /// <summary>Flyout show/hide hooks — the renderer holds no GPU resources
    /// while the flyout is away.</summary>
    public void OnFlyoutShown()
    {
        _flyoutVisible = true;
        SyncGraph3DActivity();
    }

    public void OnFlyoutHidden()
    {
        _flyoutVisible = false;
        _graph3d?.Release();
    }

    private Graph3DPanel EnsureGraph3D() => _graph3d ??= new Graph3DPanel();

    private bool Graph3DShouldBeActive =>
        _graph3dDevMode || (_chartView3D
            && _view is AppView.Overview or AppView.Stats);

    private void SyncGraph3DActivity()
    {
        if (_flyoutVisible && Graph3DShouldBeActive)
        {
            EnsureGraph3D().Activate();
        }
        else
        {
            _graph3d?.Release();
        }
    }

    private void UpdateGraph3DData(DashboardModel.Snapshot? snapshot)
    {
        if (snapshot is null || _graph3d is null)
        {
            return;
        }

        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var year = _model?.Year ?? Format.TodayKey()[..4];
        var grid = TokenBar.Core.Grid.Build(year, stats.PerDayMap);
        _graph3d.SetData(
            grid,
            ActualTheme == ElementTheme.Dark,
            snapshot.CostAuthoritative);
    }

    private void DetachGraph3DContentHost()
    {
        if (_graph3dContentHost is not null
            && ReferenceEquals(_graph3dContentHost.Child, _graph3d))
        {
            _graph3dContentHost.Child = null;
        }

        _graph3dContentHost = null;
    }

    private void ToggleChartView() => SetChartView(!_chartView3D);

    private void SetChartView(bool use3D)
    {
        if (_chartView3D == use3D)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _chartView3D = use3D;
        AppSettings.Store.SetString("tokenbar.chart.view", use3D ? "3d" : "2d");
        RenderContent(animated: false);
        SyncGraph3DActivity();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DevLog.Write($"graph3d: toggle view={(use3D ? "3d" : "2d")} "
            + $"elapsed={elapsed:F1}ms");
    }

    /// <summary>Ctrl shortcuts whose key has no VirtualKey member: [ and ]
    /// cycle lenses, comma opens Settings. Kept as raw virtual-key codes
    /// rather than characters, so the binding follows the physical key the
    /// same way the accelerator-based shortcuts do.</summary>
    private void OnOemShortcut(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        const int VkOem4 = 0xDB;  // [
        const int VkOem6 = 0xDD;  // ]
        const int VkOemComma = 0xBC;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl)
        {
            return;
        }

        switch ((int)e.Key)
        {
            case VkOem4:
                CycleLens(-1);
                break;
            case VkOem6:
                CycleLens(1);
                break;
            case VkOemComma:
                TrayService.OpenSettings?.Invoke();
                break;
            default:
                return;
        }

        e.Handled = true;
        DevLog.Write($"oem shortcut: key=0x{(int)e.Key:X2}");
    }

    private void AddAccel(
        Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mods,
        Action action)
    {
        var accel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = key,
            Modifiers = mods,
        };
        accel.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(accel);
    }

    /// <summary>Push the hidden-lens set into the tab row. Buttons are built
    /// once in the constructor, so this toggles Visibility rather than
    /// rebuilding, and then re-runs the current selection through SwitchTo —
    /// whose Effective() guard moves us off a lens that was just hidden.
    /// Hiding the lens you are looking at must not leave it on screen with no
    /// tab to return to.</summary>
    private void ApplyLensVisibility()
    {
        var visible = AppViews.Visible(AppSettings.Store);
        foreach (var (view, button) in _tabs)
        {
            button.Visibility = visible.Contains(view)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        SwitchTo(_view);
    }

    private void CycleLens(int step)
    {
        var lenses = AppViews.Visible(AppSettings.Store);
        var index = lenses.IndexOf(_view);
        if (index < 0)
        {
            index = 0;
        }

        SwitchTo(lenses[(index + step + lenses.Count) % lenses.Count]);
    }

    /// <summary>One control, two states (macOS refreshButton): the glyph
    /// while idle, a spinner while a forced re-read or the initial load runs.</summary>
    private void UpdateRefreshControl(bool loading = false)
    {
        var spinning = loading || _model?.Refreshing == true;
        RefreshButton.Visibility = spinning ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.IsActive = spinning;
    }

    /// <summary>The model powers lazy lens loading, told which lens is
    /// active by <see cref="SwitchTo"/>.</summary>
    public void Bind(DashboardModel model) => _model = model;

    /// <summary>Persist a requested client tab. The next selection pass validates
    /// it against the visible clients before any usage surface renders.</summary>
    public void SelectClientTab(string? clientId)
    {
        var requested = string.IsNullOrWhiteSpace(clientId)
            ? ClientRegistry.OverviewTab
            : ClientRegistry.CanonicalClient(clientId.Trim());
        AppSettings.Store.SetString(ClientRegistry.ActiveTabKey, requested);
        RefreshSelection(animated: false);
    }

    public void SwitchTo(AppView view)
    {
        // One guard for every caller: the numeric accelerators each bind a
        // fixed lens at construction, so a hidden lens would otherwise stay
        // reachable by shortcut even though its tab is gone.
        view = AppViews.Effective(view, AppSettings.Store);
        if (_view == view)
        {
            return;
        }

        _view = view;
        _model?.SetActiveView(view);

        UpdateTabChrome();
        RenderContent(animated: true);
    }

    public void Render(DashboardModel.Snapshot? snapshot)
    {
        _snapshot = snapshot;
        UpdateRefreshControl(loading: snapshot is null);
        if (snapshot is null)
        {
            RenderLoadingState();
            return;
        }

        snapshot = ApplyClientSelection(snapshot);
        RenderHeader(snapshot);
        UpdateYearPicker();
        UpdateGraph3DData(snapshot);
        RenderContent(animated: false);
    }

    private void RenderLoadingState()
    {
        TodayValue.Text = "—";
        TotalValue.Text = "—";
        RateValue.Text = "—";
        CostLine.Text = CostSurfaceProjection.Checking;
        FooterText.Text = "loading usage…".Localized();
        UpdateYearPicker();

        _displayClients = [];
        _selectedClients = [];
        _selectedSet = new HashSet<string>(StringComparer.Ordinal);
        _selectedStats = null;
        _activeClientTab = ClientRegistry.OverviewTab;
        _clientTabsSignature = "";
        ClientTabsPanel.Children.Clear();

        DetachGraph3DContentHost();
        ContentHost.Content = null;
        if (!_graph3dDevMode)
        {
            _graph3d?.Release();
        }
    }

    private void RefreshSelection(bool animated)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        snapshot = ApplyClientSelection(snapshot);
        RenderHeader(snapshot);
        UpdateYearPicker();
        UpdateGraph3DData(snapshot);
        RenderContent(animated);
    }

    private DashboardModel.Snapshot ApplyClientSelection(DashboardModel.Snapshot snapshot)
    {
        var selection = ClientRegistry.ResolveSelection(
            snapshot.Graph.Summary.Clients, AppSettings.Store);
        _displayClients = selection.DisplayClients;
        _selectedClients = selection.SelectedClients;
        _selectedSet = new HashSet<string>(selection.SelectedClients, StringComparer.Ordinal);
        _selectedStats = new UsageStats(snapshot.Graph, _selectedSet);
        _activeClientTab = selection.ActiveTab;
        UpdateClientTabs();

        if (_model?.SetClientSelection(selection.SelectedClients) == true
            && _model.Current is { } current)
        {
            // Membership changes clear stale pre-aggregated lazy reports in the
            // model before this same Dashboard render reaches Hourly/Agents.
            snapshot = current;
        }

        _snapshot = snapshot;
        if (AppSettings.Store.GetString(ClientRegistry.ActiveTabKey) != selection.ActiveTab)
        {
            AppSettings.Store.SetString(ClientRegistry.ActiveTabKey, selection.ActiveTab);
        }

        return snapshot;
    }

    private void RenderHeader(DashboardModel.Snapshot snapshot)
    {
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        stats.PerDayMap.TryGetValue(Format.TodayKey(), out var today);
        var rate = TraceCollapse.FilterByClients(snapshot.Trace, _selectedSet)
            .Sum(b => b.TokensPerMin);
        TodayValue.Text = Format.CompactTokens(today?.Tokens ?? 0);
        TotalValue.Text = Format.CompactTokens(stats.TotalTokens);
        RateValue.Text = Format.CompactTokens((long)rate);
        CostLine.Text = CostSurfaceProjection.HeaderCostLine(
            today?.Cost ?? 0, stats, snapshot.CostAuthoritative);
        FooterText.Text = "updated {0}".Localized(
            snapshot.FetchedAt.ToString(
                "HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
    }

    private string _yearPickerSignature = "";

    /// <summary>Rebuilds the year menu only when the selection or the known
    /// years actually change, so the 10s fast lane's re-render never closes
    /// a menu the user has open.</summary>
    private void UpdateYearPicker()
    {
        var model = _model;
        var years = model?.KnownYears ?? [];
        if (model is null || years.Count == 0)
        {
            YearButton.Visibility = Visibility.Collapsed;
            return;
        }

        YearButton.Visibility = Visibility.Visible;
        var signature = $"{model.Year}|{string.Join(',', years)}";
        if (signature == _yearPickerSignature)
        {
            return;
        }

        _yearPickerSignature = signature;
        YearButton.Content = model.Year?.ToString() ?? "All".Localized();
        var flyout = new MenuFlyout();
        AddYearItem(flyout, "All years".Localized(), null, model);
        foreach (var year in years)
        {
            AddYearItem(flyout, year, year, model);
        }

        YearButton.Flyout = flyout;
    }

    private void AddYearItem(
        MenuFlyout flyout, string label, string? value, DashboardModel model)
    {
        // Radio (not Toggle): re-clicking the checked item must not flip its
        // checkmark off while the filter stays active.
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            IsChecked = model.Year == value,
            GroupName = "tokenbar.year",
        };
        item.Click += (_, _) =>
        {
            model.SetYear(value);
            UpdateYearPicker(); // reflect the pick before the new slice lands
        };
        flyout.Items.Add(item);
    }

    public void ScrollBy(double delta) =>
        CardsScroll.ChangeView(
            null, CardsScroll.VerticalOffset + delta, null, disableAnimation: false);

    /// <summary>Route the flyout's WH_MOUSE_LL wheel event. Coordinates are
    /// physical pixels relative to the window; graph hit-testing happens in
    /// XAML DIPs, and only wheel events over the live 3D surface become zoom.</summary>
    public void RouteGlobalWheel(double windowPixelX, double windowPixelY, int delta)
    {
        var rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        var point = new Windows.Foundation.Point(
            windowPixelX / rasterScale, windowPixelY / rasterScale);
        if (!TryZoomGraphAt(point, delta) && !TryScrollTabRowAt(point, delta))
        {
            ScrollBy(-delta);
        }
    }

    /// <summary>A vertical wheel over either tab row scrolls that row
    /// horizontally (macOS HorizontalWheelScroll). Returns false when the row
    /// is already at the end it is being pushed toward, so the wheel falls
    /// through to the dashboard's vertical scroll instead of dying under the
    /// cursor.</summary>
    private bool TryScrollTabRowAt(Windows.Foundation.Point point, int delta)
    {
        foreach (var row in new[] { ClientTabsScroll, TabsScroll })
        {
            if (row is null || row.Visibility != Visibility.Visible)
            {
                continue;
            }

            var origin = row.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X < origin.X || point.X > origin.X + row.ActualWidth
                || point.Y < origin.Y || point.Y > origin.Y + row.ActualHeight)
            {
                continue;
            }

            var (offset, moved) = WheelScroll.Clamped(
                row.HorizontalOffset, delta, row.ScrollableWidth);
            if (!moved)
            {
                return false;
            }

            row.ChangeView(offset, null, null, disableAnimation: true);
            return true;
        }

        return false;
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Graph3DPanel consumes native wheel events itself. This root handler is
        // registered with handledEventsToo for the rest of the dashboard, so
        // honor that flag or a focused 3D panel zooms twice per notch.
        if (e.Handled)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;
        if (!TryZoomGraphAt(point.Position, delta)
            && !TryScrollTabRowAt(point.Position, delta))
        {
            ScrollBy(-delta);
        }

        e.Handled = true;
    }

    private bool TryZoomGraphAt(Windows.Foundation.Point point, int delta)
    {
        var panel = _graph3d;
        if (!Graph3DShouldBeActive || panel?.IsActive != true
            || panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var origin = panel.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X < origin.X || point.Y < origin.Y
                || point.X >= origin.X + panel.ActualWidth
                || point.Y >= origin.Y + panel.ActualHeight)
            {
                return false;
            }

            panel.ZoomFromWheel(delta);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The content presenter can detach the old chart between a model
            // refresh and this queued hook callback. Treat it as normal scroll.
            return false;
        }
    }

    private void UpdateTabChrome()
    {
        foreach (var (view, button) in _tabs)
        {
            var active = view == _view;
            button.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            button.Opacity = active ? 1.0 : 0.6;
            button.Background = active
                ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }
    }

    private void UpdateClientTabs()
    {
        var signature = $"{_activeClientTab}|{string.Join(',', _displayClients)}";
        if (signature == _clientTabsSignature)
        {
            return;
        }

        _clientTabsSignature = signature;
        ClientTabsPanel.Children.Clear();
        AddClientTab(ClientRegistry.OverviewTab, "Overview".Localized());
        foreach (var id in _displayClients)
        {
            AddClientTab(id, ClientRegistry.ShortName(id));
        }
    }

    private void AddClientTab(string id, string label)
    {
        var active = id == _activeClientTab;
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(9, 4, 9, 4),
            FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = active ? 1.0 : 0.6,
            Background = active
                ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            button, $"ClientTab_{id}");
        button.Click += (_, _) => SelectClientTab(id);
        if (id != ClientRegistry.OverviewTab)
        {
            HoverTip.Attach(button, () => ClientRegistry.Style(id).DisplayName);
        }

        ClientTabsPanel.Children.Add(button);
    }

    private void RenderContent(bool animated)
    {
        if (_snapshot is null)
        {
            return;
        }

        DetachGraph3DContentHost();
        UIElement content = _view switch
        {
            AppView.Quota => BuildQuota(_snapshot),
            AppView.Models => BuildModels(_snapshot),
            AppView.Monthly => BuildMonthly(_snapshot),
            AppView.Daily => BuildDaily(_snapshot),
            AppView.Hourly => BuildHourly(_snapshot),
            AppView.Stats => BuildStats(_snapshot),
            AppView.Agents => BuildAgents(_snapshot),
            _ => BuildOverview(_snapshot),
        };
        ContentHost.Content = content;
        SyncGraph3DActivity();
        if (animated)
        {
            CardsScroll.ChangeView(null, 0, null, disableAnimation: true);
            AnimateLensIn(content);
        }
    }

    /// <summary>Lens-switch transition, macOS parity: crossfade + subtle
    /// scale from the top (0.16s).</summary>
    private static void AnimateLensIn(UIElement element)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.Opacity = 0f;
        visual.Scale = new System.Numerics.Vector3(0.985f, 0.985f, 1f);
        var easing = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, System.Numerics.Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", scale);
    }

    // ── Overview ─────────────────────────────────────────────────────────

    private UIElement BuildOverview(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        // Which client tab this render answers for — null on the Overview tab
        // itself, a client id on that client's own tab. See OverviewScope's
        // own doc comment for why that changes what below renders.
        var singleClient = OverviewScope.SingleClient(_activeClientTab);
        var limitsClientId = OverviewScope.LimitsClientId(singleClient);

        // Order comes from OverviewCards.RenderOrder, not from the sequence of
        // these calls, so a test can see it. macOS got this same sequence wrong
        // once and its pinned order is what caught it; this arrived at the same
        // mistake independently, with the quota summary built directly above
        // the limits card and the chart therefore ahead of it.
        foreach (var card in OverviewCards.RenderOrder)
        {
            var element = card switch
            {
                OverviewCard.QuotaSummary => OverviewScope.ShowsQuotaSummary(singleClient)
                    ? BuildQuotaSummary(snapshot)
                    : null,
                OverviewCard.Chart => BuildUsageChartCard(snapshot),
                OverviewCard.Limits => Ui.Card(
                    limitsClientId is { } cid
                        ? "{0} limits".Localized(ClientRegistry.ShortName(cid))
                        : "Agent limits".Localized(),
                    BuildLimits(snapshot, limitsClientId)),
                // Absent when there is no live session, or when this tab is
                // scoped to one client — the trace answers "across everything
                // right now", which a single-client tab did not ask.
                OverviewCard.Trace => OverviewScope.ShowsTrace(singleClient)
                    && BuildTrace(snapshot) is { } trace
                    ? Ui.Card("Live session".Localized(), trace)
                    : null,
                OverviewCard.Models => Ui.Card("Models".Localized(), BuildModelRows(snapshot, maxRows: 8)),
                OverviewCard.Streaks => Ui.Card("Streaks".Localized(), BuildStreaks(snapshot)),
                _ => throw new InvalidOperationException($"Unhandled overview card: {card}"),
            };
            if (element is not null)
            {
                stack.Children.Add(element);
            }
        }

        return stack;
    }

    /// <summary>The same persisted 2D/3D usage card is shared by Overview and
    /// Stats, matching the macOS UsageChartCard ownership. Keeping one builder
    /// also ensures the single Graph3DPanel is reparented through the same
    /// lifecycle path in both lenses.</summary>
    private Border BuildUsageChartCard(DashboardModel.Snapshot snapshot) =>
        Ui.Card(
            "Token Usage".Localized(),
            BuildChart(snapshot),
            (_chartView3D ? "Full year" : "30 days").Localized(),
            BuildChartViewToggle());

    private FrameworkElement BuildChartViewToggle()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };
        var twoD = LensPill("2D", !_chartView3D);
        var threeD = LensPill("3D", _chartView3D);
        twoD.Click += (_, _) => SetChartView(false);
        threeD.Click += (_, _) => SetChartView(true);
        row.Children.Add(twoD);
        row.Children.Add(threeD);
        return row;
    }

    private FrameworkElement BuildChart(DashboardModel.Snapshot snapshot)
    {
        if (_selectedClients.Count == 0)
        {
            return Ui.Dim("No visible client usage.".Localized());
        }

        if (_chartView3D)
        {
            return BuildGraph3D(snapshot);
        }

        var holder = new StackPanel();

        // Stack-by + metric pills, the macOS UsageChartCard secondary row.
        var toggles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var byModel = LensPill("Model".Localized(), _chartStackBy == StackBy.Model);
        var byAgent = LensPill("Agent".Localized(), _chartStackBy == StackBy.Agent);
        byModel.Click += (_, _) => SetStackBy(StackBy.Model);
        byAgent.Click += (_, _) => SetStackBy(StackBy.Agent);
        var byTokens = LensPill("Tokens".Localized(), _chartMetric == ChartMetric.Tokens);
        var costEnabled = CostSurfaceProjection.CanUseCost(
            snapshot.CostAuthoritative);
        var byCost = LensPill(
            CostSurfaceProjection.ChartCostLabel(costEnabled),
            _chartMetric == ChartMetric.Cost);
        byCost.IsEnabled = costEnabled;
        byTokens.Click += (_, _) => SetMetric(ChartMetric.Tokens);
        byCost.Click += (_, _) =>
        {
            if (costEnabled)
            {
                SetMetric(ChartMetric.Cost);
            }
        };
        toggles.Children.Add(byModel);
        toggles.Children.Add(byAgent);
        toggles.Children.Add(new Border { Width = 8 });
        toggles.Children.Add(byTokens);
        toggles.Children.Add(byCost);

        void SetStackBy(StackBy value)
        {
            _chartStackBy = value;
            AppSettings.Store.SetString("tokenbar.chart.stackBy",
                value == StackBy.Agent ? "agent" : "model");
            RenderContent(false);
        }

        void SetMetric(ChartMetric value)
        {
            if (value == ChartMetric.Cost && !costEnabled)
            {
                return;
            }

            _chartMetric = value;
            AppSettings.Store.SetString("tokenbar.chart.metric",
                value == ChartMetric.Cost ? "cost" : "tokens");
            RenderContent(false);
        }
        holder.Children.Add(toggles);
        if (CostSurfaceProjection.IsChartCostChecking(
                snapshot.CostAuthoritative, _chartMetric))
        {
            holder.Children.Add(Ui.Dim(CostSurfaceProjection.ChartChecking));
            return holder;
        }

        var colors = new ModelColorMap(snapshot.Models, snapshot.CostAuthoritative);
        var metric = _chartMetric;
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var bars = DayBars.Build(
            snapshot.Graph,
            _selectedClients,
            _chartStackBy,
            metric,
            colors,
            Format.TodayKey(),
            rangeEnd: stats.DateRange.End);
        var canvas = new Canvas { Height = 120 };
        holder.Children.Insert(0, canvas);

        // Wrapping legend, capped like the macOS FlowLayout (12 + "+N").
        var legend = new WrapRow { Margin = new Thickness(0, 8, 0, 0) };
        var allSegments = DayBars.Legend(bars, metric);
        foreach (var seg in allSegments.Take(12))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(Ui.Disc(seg.Color));
            item.Children.Add(Ui.Text(seg.Label, 10, 0.75));
            legend.Children.Add(item);
        }

        if (allSegments.Count > 12)
        {
            legend.Children.Add(Ui.Text($"+{allSegments.Count - 12}", 10, 0.6));
        }

        holder.Children.Add(legend);

        void Draw()
        {
            canvas.Children.Clear();
            var width = canvas.ActualWidth;
            var height = canvas.Height;
            if (width <= 0 || bars.Count == 0)
            {
                return;
            }

            // Bar length follows the metric toggle (tokens or spend).
            double SegValue(DaySegment s) =>
                metric == ChartMetric.Cost ? s.Cost : s.Tokens;
            double BarValue(DayBar b) =>
                metric == ChartMetric.Cost ? b.TotalCost : b.TotalTokens;
            var maxValue = Math.Max(bars.Max(BarValue), 1e-9);
            const double gap = 3;
            var barWidth = Math.Max(2, (width - gap * (bars.Count - 1)) / bars.Count);
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var x = i * (barWidth + gap);
                var y = height;
                foreach (var seg in bar.Segments)
                {
                    var segHeight = height * SegValue(seg) / maxValue;
                    if (segHeight < 0.5)
                    {
                        continue;
                    }

                    y -= segHeight;
                    var rect = new Rectangle
                    {
                        Width = barWidth,
                        Height = segHeight,
                        Fill = Ui.BrushFromHex(seg.Color),
                        RadiusX = 1,
                        RadiusY = 1,
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    canvas.Children.Add(rect);
                }

                if (bar.IsEmpty)
                {
                    var tick = new Rectangle
                    {
                        Width = barWidth,
                        Height = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                    };
                    Canvas.SetLeft(tick, x);
                    Canvas.SetTop(tick, height - 2);
                    canvas.Children.Add(tick);
                }
                else
                {
                    var outline = new Rectangle
                    {
                        Width = barWidth,
                        Height = height - y,
                        Fill = new SolidColorBrush(Colors.Transparent),
                        Stroke = new SolidColorBrush(Colors.Transparent),
                        StrokeThickness = 1,
                        RadiusX = 1,
                        RadiusY = 1,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(outline, x);
                    Canvas.SetTop(outline, y);
                    canvas.Children.Add(outline);

                    // Full-height hover column: ONE tooltip per day listing
                    // every segment with its color dot (the macOS layout),
                    // instead of sliver-sized per-segment targets.
                    var overlay = new Rectangle
                    {
                        Width = barWidth + gap,
                        Height = height,
                        Fill = new SolidColorBrush(Colors.Transparent),
                        Stroke = new SolidColorBrush(Colors.Transparent),
                        StrokeThickness = 2,
                    };
                    Canvas.SetLeft(overlay, x - gap / 2);
                    Canvas.SetTop(overlay, 0);
                    var capturedBar = bar;
                    AttachHoverOutline(overlay, hovered =>
                        outline.Stroke = hovered
                            ? HoverOutlineBrush()
                            : new SolidColorBrush(Colors.Transparent));
                    HoverTip.AttachRich(
                        overlay,
                        () => DayTip(capturedBar, snapshot.CostAuthoritative));
                    canvas.Children.Add(overlay);
                }
            }
        }

        canvas.SizeChanged += (_, _) => Draw();
        return holder;
    }

    private FrameworkElement BuildGraph3D(DashboardModel.Snapshot snapshot)
    {
        const double chartHeight = 196;
        var root = new Grid { Height = chartHeight };
        var panel = EnsureGraph3D();
        UpdateGraph3DData(snapshot);

        // --graph3d owns the panel through the fixed Gate 0 host. In normal
        // operation the same panel lives here, inside the product card.
        if (!_graph3dDevMode)
        {
            _graph3dContentHost = new Border
            {
                Height = chartHeight,
                Child = panel,
            };
            root.Children.Add(_graph3dContentHost);
        }

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var fit = LensPill("Fit".Localized(), false);
        var reset = LensPill("Reset".Localized(), false);
        fit.Click += (_, _) => panel.FitToContent();
        reset.Click += (_, _) => panel.ResetCamera();
        HoverTip.Attach(fit, () => "Frame active days".Localized());
        HoverTip.Attach(reset, () => "Reset the saved 3D camera".Localized());
        controls.Children.Add(fit);
        controls.Children.Add(reset);
        root.Children.Add(controls);
        return root;
    }

    /// <summary>The all-agent Overview's opening statement: which
    /// subscription is closest to running out, whether anything is burning
    /// faster than its schedule allows, and today's totals. Port of the
    /// macOS QuotaSummaryLine — its own card, not a titled one, since it is
    /// prose rather than another labeled section (matches
    /// BuildUsageChartCard's neighbors only in background/padding, not in
    /// having a header row).
    ///
    /// A three-row label/value grid rather than a flowing sentence: Chinese
    /// breaks between any two characters, so a paragraph wraps mid-unit
    /// ("3 小時 23" then "分 後重置"). Short fragments in a fixed-width label
    /// column are a layout the width cannot break.</summary>
    private FrameworkElement BuildQuotaSummary(DashboardModel.Snapshot snapshot)
    {
        var excluding = ClientRegistry.QuotaExcludedClients(AppSettings.Store);
        var now = DateTimeOffset.Now;
        var summary = QuotaSummaryFold.Build(snapshot.Quota, excluding, CurrentPaceMode(), now);
        // The quota lane publishes its own Snapshot.Quota independently of
        // the graph lane that seeds the first snapshot, so Quota can still be
        // null on an otherwise-ready snapshot while the first fetch is in
        // flight. summary == null alone cannot tell that apart from "asked
        // and nothing reported a window" — QuotaSummaryText.State needs both.
        // Read from the snapshot rather than inferred from the payload: a
        // failed fetch publishes completion with no payload, and inferring
        // would render that as still-in-flight forever.
        var outcome = snapshot.QuotaOutcome;
        var allHidden = QuotaResolver.ExcludedAllCandidates(
            snapshot.Quota, QuotaResolver.Auto, excluding);

        var stack = new StackPanel { Spacing = 7 };
        switch (QuotaSummaryText.State(summary, outcome, allHidden))
        {
            case QuotaSummaryState.AllHidden:
                // No card at all. The user hid these clients; saying so would
                // be a placeholder for a choice they already made.
                return new StackPanel();
            case QuotaSummaryState.Ready:
                BuildQuotaSummaryReady(stack, summary!, snapshot, now);
                break;
            case QuotaSummaryState.NoWindowReporting:
                stack.Children.Add(Ui.Dim(QuotaSummaryText.NoWindowReporting()));
                break;
            case QuotaSummaryState.Failed:
                stack.Children.Add(Ui.Dim(QuotaSummaryText.CouldNotCheckLimits()));
                break;
            default: // Loading
                stack.Children.Add(Ui.Dim(QuotaSummaryText.CheckingLimits()));
                break;
        }

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack,
        };
    }

    private void BuildQuotaSummaryReady(
        StackPanel stack, QuotaSummary summary, DashboardModel.Snapshot snapshot, DateTimeOffset now)
    {
        var tightest = new StackPanel { Spacing = 1 };
        tightest.Children.Add(Ui.Text(QuotaSummaryText.TightestHeadline(summary), 13, bold: true));
        tightest.Children.Add(Ui.Text(QuotaSummaryText.TightestDetail(summary, now), 11, 0.6));
        if (summary.OtherWindows > 0)
        {
            tightest.Children.Add(Ui.Text(QuotaSummaryText.OthersText(summary), 10, 0.45));
        }

        stack.Children.Add(QuotaSummaryRow("Tightest".Localized(), tightest));

        // "Burning fastest" replaces "Pace" whenever there is a burn
        // warning: a slot that is empty whenever nothing is wrong teaches
        // the user to ignore it, and on real data it is empty nearly always
        // — inverting it turns the most reliable fact here into something
        // worth reading. Gated on PaceCheckedWindows too: with pace off, or
        // the historical basis still learning, Compute returns null for
        // every window and Burning is null because nothing was asked, not
        // because nothing was wrong — printing the reassurance then would
        // vouch for a check that never ran.
        switch (QuotaSummaryText.SecondRow(summary))
        {
            case QuotaSummarySecondRow.Burning:
                var burning = summary.Burning!;
                var burn = new StackPanel { Spacing = 1 };
                burn.Children.Add(Ui.Text(QuotaSummaryText.BurnHeadline(burning), 11, bold: true));
                var detail = Ui.Text(QuotaSummaryText.BurnDetail(burning), 10);
                detail.Foreground = Ui.BrushFromHex(PaceOrange);
                burn.Children.Add(detail);
                stack.Children.Add(QuotaSummaryRow("Burning fastest".Localized(), burn));
                break;
            case QuotaSummarySecondRow.Reassurance:
                stack.Children.Add(QuotaSummaryRow(
                    "Pace".Localized(), Ui.Dim(QuotaSummaryText.PaceReassurance(), 10)));
                break;
            case QuotaSummarySecondRow.None:
                break;
        }

        // Today's totals mirror the header's own today figure: same PerDayMap
        // lookup, same cost-authority policy, and — the part this comment
        // previously claimed and did not deliver — the same treatment of a day
        // with no entry. The header renders `today?.Tokens ?? 0`, so it commits
        // to showing a zero; gating this row on TryGetValue made the two
        // surfaces disagree on exactly the inactive days, six centimetres
        // apart, while the comment above them asserted they could not.
        //
        // macOS omits its row here, and does not face the choice: its Overview
        // has no separate today field to contradict. Windows does, so agreeing
        // with the header that already ships beats matching a platform whose
        // header does not exist.
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        stats.PerDayMap.TryGetValue(Format.TodayKey(), out var today);
        stack.Children.Add(QuotaSummaryRow(
            "Today".Localized(),
            Ui.Text(
                QuotaSummaryText.TodayText(
                    today?.Tokens ?? 0, today?.Cost ?? 0, snapshot.CostAuthoritative),
                11,
                0.85)));

    }

    /// <summary>Label column fixed width so the rows align, matching the
    /// macOS QuotaSummaryLine's 62pt column (widened slightly here for the
    /// longer zh-Hant labels this port also ships).</summary>
    private const double QuotaSummaryLabelWidth = 64;

    private static Grid QuotaSummaryRow(string label, UIElement content)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(QuotaSummaryLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = Ui.Text(label, 10, 0.6);
        labelBlock.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(labelBlock);
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>The user's saved pace policy (macOS PaceMode parity), shared
    /// by the Agent-limits card and the quota summary above it so the two
    /// surfaces can never disagree about whether a window is ahead of
    /// schedule.</summary>
    private static PaceMode CurrentPaceMode() =>
        AppSettings.Store.GetString("tokenbar.limits.paceMode", "historical") switch
        {
            "linear" => PaceMode.Linear,
            "off" => PaceMode.Off,
            _ => PaceMode.Historical,
        };

    /// <summary><paramref name="clientId"/> narrows the card to one
    /// subscription for the per-client Quota lens (5e). A parameter rather than
    /// a second builder: this card answers "where does the allowance stand
    /// right now", and a copy of it would be free to disagree with the original
    /// on the same window.</summary>
    private static FrameworkElement BuildLimits(
        DashboardModel.Snapshot snapshot, string? clientId = null)
    {
        var panel = new StackPanel { Spacing = 10 };
        var agents = snapshot.Quota?.Agents ?? [];
        if (clientId is not null)
        {
            agents = [.. agents.Where(agent => agent.ClientId == clientId)];
        }

        // Round 11's P2 finding, corrected: retained data wins over a failed
        // refetch — QuotaSummaryText.LimitsState checks `agents.Count > 0`
        // before `snapshot.QuotaOutcome`, the same order this file's own
        // BuildQuotaSummary already applies to the sibling summary card. See
        // that method's own doc comment for why the earlier ordering (outcome
        // checked first) was wrong.
        switch (QuotaSummaryText.LimitsState(agents.Count > 0, snapshot.QuotaOutcome))
        {
            case AgentLimitsState.Failed:
                panel.Children.Add(Ui.Dim(QuotaSummaryText.CouldNotCheckLimits()));
                return panel;
            case AgentLimitsState.Loading:
                panel.Children.Add(Ui.Dim("No quota data yet.".Localized()));
                return panel;
            case AgentLimitsState.Ready:
                break;
        }

        // macOS windowRow settings: fill direction, density, pace policy.
        var asUsed = AppSettings.Store.GetBool("tokenbar.limits.asUsed", false);
        var classic = AppSettings.Store.GetString("tokenbar.limits.layout", "full") == "classic";
        var paceMode = CurrentPaceMode();

        var now = DateTimeOffset.Now;
        foreach (var agent in agents)
        {
            var section = new StackPanel { Spacing = 5 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(Ui.Disc(ClientRegistry.Style(agent.ClientId).Color));
            header.Children.Add(Ui.Text(ClientRegistry.ShortName(agent.ClientId), 12, bold: true));
            if (agent.Identity?.Plan is { } plan)
            {
                header.Children.Add(Ui.Dim(plan, 10));
            }

            section.Children.Add(header);
            if (agent.Error is { } error)
            {
                section.Children.Add(Ui.Dim(error, 11));
                panel.Children.Add(section);
                continue;
            }

            foreach (var window in agent.UniqueCardWindows)
            {
                var row = UsagePace.RowPresentation(
                    window, paceMode, asUsed, classic, now);
                section.Children.Add(QuotaRow(window, row, classic));
            }

            panel.Children.Add(section);
        }

        return panel;
    }

    private FrameworkElement? BuildTrace(DashboardModel.Snapshot snapshot) =>
        Ui.TraceRows(
            snapshot.Trace,
            _selectedSet,
            AppSettings.Store.GetBool("tokenbar.trace.detailed", false));

    private FrameworkElement BuildStreaks(DashboardModel.Snapshot snapshot)
    {
        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        row.Children.Add(Metric("{0}d".Localized(stats.Streaks.Current), "current".Localized()));
        row.Children.Add(Metric("{0}d".Localized(stats.Streaks.Longest), "longest".Localized()));
        row.Children.Add(Metric(
            CostSurfaceProjection.BestDayText(
                stats.BestDay, snapshot.CostAuthoritative), "best day".Localized()));
        return row;
    }

    // ── Models lens ──────────────────────────────────────────────────────

    private UIElement BuildModels(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        var report = snapshot.Models;
        var entries = SelectedModelEntries(snapshot);
        var subtitleParts = new List<string>();
        if (report is not null)
        {
            subtitleParts.Add(CostSurfaceProjection.ModelsSubtitle(
                entries, snapshot.CostAuthoritative));
            if (snapshot.CostAuthoritative && report.PricingUpdatedAt is { } ts)
            {
                subtitleParts.Add("Prices updated {0}".Localized(Format.RelativeTime(ts)));
            }
        }

        // Token-kind legend header, the macOS Models card top row.
        var content = new StackPanel { Spacing = 10 };
        var legend = new WrapRow();
        (string Label, string Color)[] kinds =
        [
            ("Input".Localized(), Ui.TokenKinds[0].Color),
            ("Output".Localized(), Ui.TokenKinds[1].Color),
            ("Cache read".Localized(), Ui.TokenKinds[2].Color),
            ("Cache write".Localized(), Ui.TokenKinds[3].Color),
            ("Reasoning".Localized(), Ui.TokenKinds[4].Color),
        ];
        foreach (var (label, color) in kinds)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 8,
                Height = 8,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Ui.BrushFromHex(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            item.Children.Add(Ui.Text(label, 10, 0.75));
            legend.Children.Add(item);
        }

        content.Children.Add(legend);
        content.Children.Add(BuildModelRows(snapshot, maxRows: null));
        stack.Children.Add(Ui.Card("Models".Localized(), content, string.Join(" · ", subtitleParts)));
        return stack;
    }

    /// <summary>Model rows for the current client selection, folded to one row
    /// per <c>(client, model)</c> first (<see cref="ModelReportFold.ModelLevelEntries"/>)
    /// — tokscale groups by <c>(client, provider, model)</c>, so a model used
    /// through two providers would otherwise show twice, inflate the "N
    /// models" count, and let the larger provider-split component win
    /// "Favorite model" on Stats instead of the model itself.</summary>
    private List<ModelReportEntry> SelectedModelEntries(DashboardModel.Snapshot snapshot) =>
        (snapshot.Models?.ModelLevelEntries() ?? [])
            .Where(e => _selectedSet.Contains(ClientRegistry.CanonicalClient(e.Client)))
            .ToList();

    private FrameworkElement BuildModelRows(DashboardModel.Snapshot snapshot, int? maxRows)
    {
        var panel = new StackPanel { Spacing = 8 };
        var entries = CostSurfaceProjection.OrderModels(
            SelectedModelEntries(snapshot), snapshot.CostAuthoritative).ToList();
        if (maxRows is { } cap)
        {
            entries = entries.Take(cap).ToList();
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No model data yet.".Localized()));
            return panel;
        }

        var colors = new ModelColorMap(
            snapshot.Models, snapshot.CostAuthoritative);
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 3 };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var (discHost, discGlow) = GlowingDisc(colors.Color(entry.Provider, entry.Model));
            name.Children.Add(discHost);
            name.Children.Add(Ui.Text(entry.Model, 11));
            // Right column, macOS style: tokens over cost.
            var trailing = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var tokensText = Ui.Text(Format.CompactTokens(entry.Total), 11, 0.9);
            tokensText.HorizontalAlignment = HorizontalAlignment.Right;
            // Token-aware: a model the engine could not price reads "—", not
            // "$0.00" beside its own token count.
            var costText = Ui.Text(
                CostSurfaceProjection.CostText(
                    entry.Total, entry.Cost, snapshot.CostAuthoritative), 10, 0.65);
            costText.HorizontalAlignment = HorizontalAlignment.Right;
            trailing.Children.Add(tokensText);
            trailing.Children.Add(costText);
            block.Children.Add(Ui.Row(name, trailing));
            var tokenBar = TokenKindBar(entry);
            var barHost = new Grid { Height = 4 };
            // White alpha brightens each segment; the accent edge reads as a halo.
            var barGlow = new Border
            {
                Background = new SolidColorBrush(HoverGlowFill),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1.75 },
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };
            barHost.Children.Add(tokenBar);
            barHost.Children.Add(barGlow);
            block.Children.Add(barHost);
            var captured = entry;
            // One row target covers the disc and bar so their hover state cannot drift.
            AttachHoverOutline(block, hovered =>
            {
                if (hovered)
                {
                    var accent = HoverOutlineBrush();
                    barGlow.BorderBrush = accent;
                    discGlow.Stroke = accent;
                }

                barGlow.Opacity = hovered ? 1 : 0;
                discGlow.Opacity = hovered ? 1 : 0;
            });
            HoverTip.AttachRich(
                block,
                () => ModelTip(captured, colors, snapshot.CostAuthoritative));
            panel.Children.Add(block);
        }

        return panel;
    }

    // Hover brightening for the model bar and its disc. Tuned down from the
    // first attempt, which read as washed-out rather than lit; white over a
    // saturated fill loses colour faster than it gains brightness.
    private static readonly Color HoverGlowFill = Color.FromArgb(48, 255, 255, 255);

    /// <summary>A provider disc plus its hover glow. The fixed host keeps the
    /// disc's arranged size while the glow scales out, so lighting one up never
    /// moves the row. Used by the Models lens and by the per-model rows inside
    /// an expanded Daily entry, which must light up the same way.</summary>
    private static (Grid Host, Ellipse Glow) GlowingDisc(string hex, double size = 8)
    {
        var disc = Ui.Disc(hex, size);
        var glow = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(HoverGlowFill),
            Stroke = new SolidColorBrush(Colors.Transparent),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = new ScaleTransform { ScaleX = 1.35, ScaleY = 1.35 },
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        var host = new Grid
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(disc);
        host.Children.Add(glow);
        return (host, glow);
    }

    // Keep hover state attached to the target; visual changes do not alter
    // measured or arranged bounds while the tooltip follows the pointer.
    private static void AttachHoverOutline(
        FrameworkElement target, Action<bool> setVisible)
    {
        // A panel with no Background is hit-tested only where its children are,
        // so the gap between a row's left and right columns would fire
        // PointerExited while the pointer is still inside the lit row. Every
        // hover target routes through here, so one guard covers them all.
        if (target is Panel panel && panel.Background is null)
        {
            panel.Background = new SolidColorBrush(Colors.Transparent);
        }

        target.PointerEntered += (_, _) => setVisible(true);
        target.PointerExited += (_, _) => setVisible(false);
        target.Unloaded += (_, _) => setVisible(false);
    }

    private Brush HoverOutlineBrush() =>
        (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    // ── Daily lens ───────────────────────────────────────────────────────

    /// <summary>One model stripe inside an expanded Daily or Monthly card:
    /// provider-tinted disc, model and client name, tokens and cost, with the
    /// disc and the row outline lighting together on hover so the rich tooltip
    /// points at an unambiguous row. Shared so the two lenses cannot drift —
    /// the hover/glow/tooltip wiring is the fiddly part, and a copy of it is
    /// exactly where a divergence would hide.</summary>
    private FrameworkElement ModelStripeRow(
        ContributionClient client, ModelColorMap colors, bool authoritative)
    {
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var (subDiscHost, subDiscGlow) =
            GlowingDisc(colors.Color(client.ProviderId, client.ModelId), 6);
        name.Children.Add(subDiscHost);
        name.Children.Add(Ui.Text(
            $"{client.ModelId} · {ClientRegistry.ShortName(client.Client)}", 10, 0.85));
        // Token-aware, like the Models row: this sub-row's hover card is the
        // same ModelTip (attached below), which reads "—" for an unpriced model,
        // and a row must not say "$0.00" while its own tooltip says "—".
        var row = Ui.Row(
            name,
            Ui.Text(
                $"{Format.CompactTokens(client.Tokens.Total)} · "
                    + CostSurfaceProjection.CostText(
                        client.Tokens.Total, client.Cost, authoritative),
                10,
                0.7));

        // Highlight the sub-row itself, not the card enclosing it: the tooltip
        // describes this model, and lighting the whole card points at the
        // wrong thing.
        var rowGlow = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Opacity = 0,
        };
        var rowHost = new Grid();
        rowHost.Children.Add(row);
        rowHost.Children.Add(rowGlow);
        AttachHoverOutline(rowHost, hovered =>
        {
            if (hovered)
            {
                var accent = HoverOutlineBrush();
                subDiscGlow.Stroke = accent;
                rowGlow.BorderBrush = accent;
            }

            subDiscGlow.Opacity = hovered ? 1 : 0;
            rowGlow.Opacity = hovered ? 0.55 : 0;
        });
        HoverTip.AttachRich(rowHost, () => ModelTip(client, colors, authoritative));
        return rowHost;
    }

    private UIElement BuildDaily(DashboardModel.Snapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 6 };
        var colors = new ModelColorMap(
            snapshot.Models, snapshot.CostAuthoritative);
        var days = DailyRows.Build(snapshot.Graph, _selectedClients);
        if (days.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No active days.".Localized()));
        }

        foreach (var selectedDay in days)
        {
            var block = new StackPanel { Spacing = 4 };
            var summary = DrillDownSummary.Text(selectedDay, snapshot.CostAuthoritative);
            var head = Ui.Row(
                Ui.Text(Format.MonthDay(selectedDay.Date), 12, bold: true),
                Ui.Text(summary, 11, 0.8));
            block.Children.Add(head);

            if (_expandedDay == selectedDay.Date)
            {
                foreach (var client in CostSurfaceProjection.OrderContributionClients(
                    selectedDay.Clients, snapshot.CostAuthoritative))
                {
                    block.Children.Add(ModelStripeRow(
                        client, colors, snapshot.CostAuthoritative));
                }
            }

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = block,
            };
            var date = selectedDay.Date;
            card.Tapped += (_, _) =>
            {
                _expandedDay = _expandedDay == date ? null : date;
                RenderContent(animated: false);
            };

            panel.Children.Add(card);
        }

        return panel;
    }

    // ── Monthly lens ─────────────────────────────────────────────────────

    /// <summary>"Monthly" lens: one card per calendar month, most recent
    /// first, expanding to the model stripes folded across that month's days.
    /// Structurally a sibling of BuildDaily rather than a generalisation of it
    /// — macOS keeps DailyView and MonthlyView separate too — but the stripe
    /// row itself is shared so the drill-downs cannot drift.</summary>
    private UIElement BuildMonthly(DashboardModel.Snapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 6 };
        var colors = new ModelColorMap(snapshot.Models, snapshot.CostAuthoritative);
        var months = MonthlyRows.Build(snapshot.Graph, _selectedClients);
        if (months.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No usage in this range.".Localized()));
        }

        foreach (var month in months)
        {
            var block = new StackPanel { Spacing = 4 };
            var summary = DrillDownSummary.Text(month, snapshot.CostAuthoritative);
            block.Children.Add(Ui.Row(
                Ui.Text(Format.MonthYear(month.Month), 12, bold: true),
                Ui.Text(summary, 11, 0.8)));

            if (_expandedMonth == month.Month)
            {
                foreach (var client in CostSurfaceProjection.OrderContributionClients(
                    month.Clients, snapshot.CostAuthoritative))
                {
                    block.Children.Add(ModelStripeRow(
                        client, colors, snapshot.CostAuthoritative));
                }
            }

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = block,
            };
            var key = month.Month;
            card.Tapped += (_, _) =>
            {
                _expandedMonth = _expandedMonth == key ? null : key;
                RenderContent(animated: false);
            };

            panel.Children.Add(card);
        }

        return panel;
    }

    // ── Hourly lens ──────────────────────────────────────────────────────

    private UIElement BuildHourly(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (snapshot.Hourly is not { } hourly)
        {
            // Round 11's P2 finding, same defect class as BuildLimits: a null
            // Hourly used to render as "Loading" unconditionally, so a lane
            // that asked and threw (HourlyOutcome == Failed) looked identical
            // to a cold-start read still in flight.
            var message = snapshot.HourlyOutcome == WindowEquivalence.FetchOutcome.Failed
                ? "Hourly usage could not be read. It will be retried.".Localized()
                : "Loading hourly data…".Localized();
            stack.Children.Add(Ui.Card("Hourly".Localized(), Ui.Dim(message)));
            return stack;
        }

        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var timelineBtn = LensPill("Timeline".Localized(), !_hourlyProfileMode);
        var profileBtn = LensPill("Profile".Localized(), _hourlyProfileMode);
        timelineBtn.Click += (_, _) => { _hourlyProfileMode = false; RenderContent(false); };
        profileBtn.Click += (_, _) => { _hourlyProfileMode = true; RenderContent(false); };
        toggle.Children.Add(timelineBtn);
        toggle.Children.Add(profileBtn);
        stack.Children.Add(toggle);

        if (_hourlyProfileMode)
        {
            // 24-hour rhythm profile.
            var byHour = new long[24];
            foreach (var entry in hourly.Entries)
            {
                // "YYYY-MM-DD HH:00" local slots.
                if (entry.Hour.Length >= 13 &&
                    int.TryParse(entry.Hour.AsSpan(11, 2), out var h) && h is >= 0 and < 24)
                {
                    byHour[h] += entry.Total;
                }
            }

            var max = Math.Max(1, byHour.Max());
            var panel = new StackPanel { Spacing = 3 };
            for (var h = 0; h < 24; h++)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Text($"{h:D2}:00", 10, 0.7));
                var bar = Ui.ShareBar(byHour[h] / (double)max, h == DateTime.Now.Hour ? "#22c55e" : "#3b82f6");
                bar.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var count = Ui.Text(Format.CompactTokens(byHour[h]), 10, 0.7);
                Grid.SetColumn(count, 2);
                row.Children.Add(count);
                panel.Children.Add(row);
            }

            stack.Children.Add(Ui.Card("24h profile".Localized(), panel));
        }
        else
        {
            var entries = hourly.Entries.AsEnumerable().Reverse().ToList(); // newest first
            // Invariant: the engine's entry.Hour keys are Gregorian "yyyy-MM-dd
            // HH:00"; a non-Gregorian ambient calendar (th-TH Buddhist, etc.)
            // would render a different year here and the current-hour highlight
            // would never match.
            var currentSlot = DateTime.Now.ToString(
                "yyyy-MM-dd HH:00", System.Globalization.CultureInfo.InvariantCulture);
            var panel = new StackPanel { Spacing = 5 };
            foreach (var entry in entries.Take(_hourlyWindow))
            {
                var label = Ui.Text(entry.Hour, 11, entry.Hour == currentSlot ? 1.0 : 0.8);
                if (entry.Hour == currentSlot)
                {
                    label.Foreground = Ui.BrushFromHex("#22c55e");
                }

                panel.Children.Add(Ui.Row(
                    label,
                    Ui.Text(
                        $"{Format.CompactTokens(entry.Total)} · "
                            + CostSurfaceProjection.HourlyCost(
                                entry.Cost, snapshot.CostAuthoritative),
                        11,
                        0.75)));
            }

            if (entries.Count > _hourlyWindow)
            {
                var more = LensPill("Show more ({0} left)".Localized(entries.Count - _hourlyWindow), false);
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Click += (_, _) => { _hourlyWindow += 48; RenderContent(false); };
                panel.Children.Add(more);
            }

            stack.Children.Add(Ui.Card("Timeline".Localized(), panel, "{0} slots".Localized(hourly.Entries.Count)));
        }

        return stack;
    }

    // ── Stats lens ───────────────────────────────────────────────────────

    private UIElement BuildStats(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildUsageChartCard(snapshot));

        var stats = _selectedStats ?? new UsageStats(snapshot.Graph, _selectedSet);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var favorite = CostSurfaceProjection.FavoriteModel(
            SelectedModelEntries(snapshot), snapshot.CostAuthoritative);
        (string Value, string Label)[] metrics =
        [
            (CostSurfaceProjection.CostText(
                stats.TotalCost, snapshot.CostAuthoritative), "total spend".Localized()),
            (Format.CompactTokens(stats.TotalTokens), "tokens".Localized()),
            ($"{stats.ActiveDays}", "active days".Localized()),
            (CostSurfaceProjection.CostText(
                stats.AveragePerDay, snapshot.CostAuthoritative), "avg/day".Localized()),
            (CostSurfaceProjection.BestDayText(
                stats.BestDay, snapshot.CostAuthoritative), "best day".Localized()),
        ];
        for (var i = 0; i < metrics.Length; i++)
        {
            var cell = Metric(metrics[i].Value, metrics[i].Label);
            Grid.SetColumn(cell, i % 3);
            Grid.SetRow(cell, i / 3);
            grid.Children.Add(cell);
        }

        // Favorite model gets a full-width row — long model ids don't fit a
        // third of the card.
        grid.RowDefinitions.Add(new RowDefinition());
        var favoriteCell = Metric(favorite?.Model ?? "—", "favorite model".Localized());
        Grid.SetRow(favoriteCell, 2);
        Grid.SetColumnSpan(favoriteCell, 3);
        grid.Children.Add(favoriteCell);

        stack.Children.Add(Ui.Card("Stats".Localized(), grid));
        stack.Children.Add(Ui.Card("Streaks".Localized(), BuildStreaks(snapshot)));
        return stack;
    }

    // ── Agents lens ──────────────────────────────────────────────────────

    private UIElement BuildAgents(DashboardModel.Snapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (snapshot.Agents is not { } agents)
        {
            // Same fix as BuildHourly, same reason: Failed and NotAttempted
            // both used to render "Loading agent data…".
            var message = snapshot.AgentsOutcome == WindowEquivalence.FetchOutcome.Failed
                ? "Agent usage could not be read. It will be retried.".Localized()
                : "Loading agent data…".Localized();
            stack.Children.Add(Ui.Card("Agents".Localized(), Ui.Dim(message)));
            return stack;
        }

        var panel = new StackPanel { Spacing = 8 };
        var entries = CostSurfaceProjection.OrderAgents(
            agents.Entries, snapshot.CostAuthoritative);
        var maxCost = CostSurfaceProjection.AgentScale(
            entries, snapshot.CostAuthoritative);
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 3 };
            block.Children.Add(Ui.Row(
                Ui.Text(entry.Agent, 11, bold: true),
                Ui.Text(
                    "{0} msgs".Localized(entry.Messages)
                        + $" · {Format.CompactTokens(entry.Total)} · "
                        + CostSurfaceProjection.CostText(
                            entry.Cost, snapshot.CostAuthoritative),
                    10,
                    0.75)));
            block.Children.Add(Ui.ShareBar(
                CostSurfaceProjection.AgentBarValue(
                    entry, snapshot.CostAuthoritative) / maxCost,
                "#3b82f6"));
            block.Children.Add(Ui.Dim(string.Join(", ",
                entry.Clients.Select(ClientRegistry.ShortName)), 9));
            panel.Children.Add(block);
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(Ui.Dim("No sub-agent usage recorded.".Localized()));
        }

        stack.Children.Add(Ui.Card(
            CostSurfaceProjection.AgentsTitle(snapshot.CostAuthoritative),
            panel,
            "{0} messages".Localized(agents.TotalMessages)));
        return stack;
    }

    // ── shared pieces ────────────────────────────────────────────────────

    private static StackPanel Metric(string value, string label)
    {
        var cell = new StackPanel();
        cell.Children.Add(Ui.Text(value, 16, bold: true));
        cell.Children.Add(Ui.Dim(label, 10));
        return cell;
    }

    private static Button LensPill(string text, bool active) => new()
    {
        Content = text,
        FontSize = 10,
        Padding = new Thickness(8, 3, 8, 3),
        Opacity = active ? 1.0 : 0.6,
        FontWeight = active
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
    };

    // ── rich tooltips (macOS hover-card layouts; explicit light foreground
    //    because the tip card is always dark) ─────────────────────────────

    private static TextBlock TipText(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
    };

    private static Grid TipRow(UIElement left, string right, double opacity = 0.75)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        var value = TipText(right, 11, opacity);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static StackPanel TipLabel(string hex, string text, bool square = false)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (square)
        {
            label.Children.Add(new Rectangle
            {
                Width = 8,
                Height = 8,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Ui.BrushFromHex(hex),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            label.Children.Add(Ui.Disc(hex, 7));
        }

        label.Children.Add(TipText(text, 11, 0.95));
        return label;
    }

    /// <summary>Whole-day chart tooltip: date, totals row, then one dotted
    /// line per segment — the macOS bar tooltip.</summary>
    private UIElement DayTip(DayBar bar, bool costAuthoritative)
    {
        var panel = new StackPanel { Spacing = 5, MinWidth = 200 };
        panel.Children.Add(TipText(Format.MonthDay(bar.Date), 12, bold: true));
        panel.Children.Add(TipRow(
            TipText("{0} tokens".Localized(Format.ExactTokens(bar.TotalTokens)), 11, 0.9),
            CostSurfaceProjection.DayTipCost(bar.TotalCost, costAuthoritative), 0.9));
        var ordered = CostSurfaceProjection.OrderDaySegments(
            bar.Segments, costAuthoritative, _chartMetric);
        foreach (var seg in ordered)
        {
            panel.Children.Add(TipRow(
                TipLabel(seg.Color, seg.Label),
                $"{Format.CompactTokens(seg.Tokens)} · "
                    + CostSurfaceProjection.DayTipCost(
                        seg.Cost, costAuthoritative)));
        }

        return panel;
    }

    /// <summary>Model-row tooltip: header disc + name, source line, totals,
    /// then per-kind colored rows with share percentages — the macOS
    /// ModelBreakdownCard hover.</summary>
    private static UIElement ModelTip(
        ContributionClient entry,
        ModelColorMap colors,
        bool costAuthoritative) =>
        ModelTip(new ModelReportEntry(
            entry.Client,
            entry.ModelId,
            entry.ProviderId,
            entry.Tokens.Input,
            entry.Tokens.Output,
            entry.Tokens.CacheRead,
            entry.Tokens.CacheWrite,
            entry.Tokens.Reasoning,
            entry.Tokens.Total,
            entry.Messages,
            entry.Cost),
            colors,
            costAuthoritative,
            includeZeroKinds: true);

    private static UIElement ModelTip(
        ModelReportEntry entry,
        ModelColorMap colors,
        bool costAuthoritative,
        bool includeZeroKinds = false)
    {
        long[] values =
            [entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.Reasoning];
        // Same saturating aggregate TokenBreakdown.Total uses: a corrupt lane
        // clamped to long.MaxValue must not throw out of a tooltip, and
        // Enumerable.Sum over long is checked.
        var total = Math.Max(
            1, values.Aggregate(0L, (running, v) => running.SaturatingAdd(v)));
        var panel = new StackPanel { Spacing = 5, MinWidth = 200 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(Ui.Disc(colors.Color(entry.Provider, entry.Model), 7));
        head.Children.Add(TipText(entry.Model, 12, bold: true));
        panel.Children.Add(head);
        panel.Children.Add(TipText(
            $"{ClientRegistry.ShortName(entry.Client)} · {entry.Provider}", 10, 0.6));
        panel.Children.Add(TipRow(
            TipText("{0} tokens".Localized(Format.CompactTokens(entry.Total)), 11, 0.9),
            CostSurfaceProjection.ModelTipCost(
                entry.Total, entry.Cost, costAuthoritative), 0.9));
        (string Label, string Color)[] kinds =
        [
            ("Input".Localized(), Ui.TokenKinds[0].Color),
            ("Output".Localized(), Ui.TokenKinds[1].Color),
            ("Cache read".Localized(), Ui.TokenKinds[2].Color),
            ("Cache write".Localized(), Ui.TokenKinds[3].Color),
            ("Reasoning".Localized(), Ui.TokenKinds[4].Color),
        ];
        for (var i = 0; i < values.Length; i++)
        {
            if (includeZeroKinds || values[i] > 0)
            {
                panel.Children.Add(TipRow(
                    TipLabel(kinds[i].Color, kinds[i].Label, square: true),
                    $"{Format.CompactTokens(values[i])} · {100.0 * values[i] / total:F0}%"));
            }
        }

        return panel;
    }

    private static Grid TokenKindBar(ModelReportEntry entry)
    {
        long[] values =
            [entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.Reasoning];
        var weights = values.Select(v => Math.Sqrt(v)).ToArray();
        var total = weights.Sum();
        var bar = new Grid { Height = 4, ColumnSpacing = 1 };
        if (total <= 0)
        {
            return bar;
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[i] / total, GridUnitType.Star),
            });
            var seg = new Rectangle
            {
                Fill = Ui.BrushFromHex(Ui.TokenKinds[i].Color),
                RadiusX = 1,
                RadiusY = 1,
            };
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(seg);
        }

        return bar;
    }

    internal const string PaceOrange = "#ff9500"; // macOS Color.orange

    /// <summary>Render one complete quota window row from Core's precomputed
    /// display values. The responsive footer is built once and only toggles
    /// visibility when the actual row width crosses its measured threshold.</summary>
    internal static FrameworkElement QuotaRow(
        UsageWindow window, UsagePaceRowPresentation row, bool classic)
    {
        var root = new StackPanel { Spacing = 3 };
        // Derive the countdown from the structured timestamp so it follows the
        // UI language; window.ResetText is the engine's English compatibility
        // field and only stands in when the timestamp will not parse. Same
        // precedence as macOS AgentLimitsCard.
        var resetText = UsagePace.ResetTextOr(window.ResetsAt, window.ResetText, DateTimeOffset.Now);
        var headerTrailing = Ui.Text(
            classic ? resetText ?? row.AmountText : resetText ?? "",
            10, 0.6);
        // Display only. The raw Label is what QuotaResolver matches a persisted
        // legacy selection against, so it must never be translated on that path.
        root.Children.Add(Ui.Row(
            Ui.Text(window.Label.Localized(), 11, bold: true), headerTrailing));
        root.Children.Add(GaugeBar(
            row.FillPercent,
            row.RemainingPercent,
            classic ? null : row.MarkerPercent,
            classic ? null : row.ExpectedUsedPercent,
            row.IsHistoricalDeficit));

        TextBlock AmountLabel() => Ui.Text(row.AmountText, 10, 0.75);
        TextBlock PaceLabel(string text)
        {
            var label = Ui.Text(text, 10, row.IsHistoricalDeficit ? 1.0 : 0.7);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.TextAlignment = TextAlignment.Right;
            if (row.IsHistoricalDeficit)
            {
                label.Foreground = Ui.BrushFromHex(PaceOrange);
            }

            return label;
        }

        if (classic)
        {
            if (resetText is not null)
            {
                root.Children.Add(AmountLabel());
            }

            return root;
        }

        if (string.IsNullOrEmpty(row.ProjectionText))
        {
            if (string.IsNullOrEmpty(row.PaceText))
            {
                root.Children.Add(AmountLabel());
            }
            else
            {
                root.Children.Add(Ui.Row(AmountLabel(), PaceLabel(row.PaceText)));
            }

            return root;
        }

        var wideAmount = AmountLabel();
        var wideDetails = PaceLabel(string.Join(" · ",
            new[] { row.PaceText, row.ProjectionText }
                .Where(static text => !string.IsNullOrEmpty(text))));
        var wideFooter = Ui.Row(wideAmount, wideDetails);

        var narrowFooter = new StackPanel { Spacing = 1 };
        var narrowFirst = string.IsNullOrEmpty(row.PaceText)
            ? (FrameworkElement)AmountLabel()
            : Ui.Row(AmountLabel(), PaceLabel(row.PaceText));
        narrowFooter.Children.Add(narrowFirst);

        var narrowProjection = PaceLabel(row.ProjectionText);
        narrowProjection.TextWrapping = TextWrapping.Wrap;
        narrowProjection.TextTrimming = TextTrimming.None;
        narrowProjection.HorizontalAlignment = HorizontalAlignment.Right;
        narrowProjection.TextAlignment = TextAlignment.Right;
        var projectionRow = new Grid();
        projectionRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        projectionRow.Children.Add(narrowProjection);
        narrowFooter.Children.Add(projectionRow);

        narrowFooter.Visibility = Visibility.Collapsed;
        root.Children.Add(wideFooter);
        root.Children.Add(narrowFooter);

        wideAmount.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        wideDetails.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        var requiredWidth = wideAmount.DesiredSize.Width
            + wideDetails.DesiredSize.Width + 8;
        var wideVisible = true;
        root.SizeChanged += (_, _) =>
        {
            var width = root.ActualWidth;
            if (!double.IsFinite(width) || width <= 0)
            {
                return;
            }

            var nextWide = wideVisible
                ? width >= requiredWidth
                : width > requiredWidth + 8;
            if (nextWide == wideVisible)
            {
                return;
            }

            wideVisible = nextWide;
            wideFooter.Visibility = nextWide ? Visibility.Visible : Visibility.Collapsed;
            narrowFooter.Visibility = nextWide ? Visibility.Collapsed : Visibility.Visible;
        };

        return root;
    }

    /// <summary>The quota bar: fills by used or remaining per the setting,
    /// colors by remaining either way (macOS gaugeColor), and carries the
    /// precomputed pace marker on the same axis. Internal: the settings
    /// window's preview column renders through the same bar.</summary>
    internal static FrameworkElement GaugeBar(
        double fillPercent,
        double remainingForColor,
        double? markerPercent,
        double? expectedUsedPercent,
        bool historicalDeficit)
    {
        var fill = double.IsFinite(fillPercent)
            ? Math.Clamp(fillPercent, 0, 100) : 0;
        var remaining = double.IsFinite(remainingForColor)
            ? Math.Clamp(remainingForColor, 0, 100) : 100;
        var color = remaining <= 10 ? "#ef4444"
            : remaining <= 25 ? "#f59e0b" : "#22c55e";
        var track = new Grid
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(fill, GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - fill, GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = Ui.BrushFromHex(color),
            CornerRadius = new CornerRadius(2.5),
        });
        if (markerPercent is not { } markerValue || !double.IsFinite(markerValue))
        {
            return track;
        }

        // The shared row owns the axis; this renderer only places the tick.
        var markerPosition = Math.Clamp(markerValue, 0, 100);
        var holder = new Grid { Height = 9 };
        track.VerticalAlignment = VerticalAlignment.Center;
        holder.Children.Add(track);
        var lanes = new Grid();
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(markerPosition, GridUnitType.Star),
        });
        lanes.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - markerPosition, GridUnitType.Star),
        });
        var marker = new Rectangle
        {
            Width = 1.5,
            RadiusX = 0.75,
            RadiusY = 0.75,
            Fill = historicalDeficit ? Ui.BrushFromHex(PaceOrange)
                : new SolidColorBrush(Color.FromArgb(150, 160, 160, 160)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -0.75, 0),
        };
        Grid.SetColumn(marker, 0);
        lanes.Children.Add(marker);
        holder.Children.Add(lanes);
        if (expectedUsedPercent is { } expected && double.IsFinite(expected))
        {
            var expectedUsed = Math.Clamp(expected, 0, 100);
            HoverTip.Attach(marker, () =>
            "Expected {0}% used by now".Localized(expectedUsed.ToString(
                    "F0", System.Globalization.CultureInfo.CurrentCulture)));
        }

        return holder;
    }
}
