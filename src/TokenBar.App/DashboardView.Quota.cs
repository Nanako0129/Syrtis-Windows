using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Windows.UI;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>
/// The Quota lens: the strip card (past windows) and the heatmap (when the
/// allowance goes), ported from <c>QuotaHistoryStripCard.swift</c> and
/// <c>QuotaHeatmapCard.swift</c>.
/// <para>
/// Every state choice and every string comes from <see cref="QuotaLensText"/>,
/// <see cref="QuotaLabels"/> and (5d-2) <see cref="WindowEquivalenceText"/>;
/// nothing here re-decides a branch, because this file is compiled by no test
/// project.
/// </para>
/// <para>
/// The <c>≈ token / $</c> equivalence lines are keyed by
/// <see cref="QuotaWindowIdentity"/> and looked up per window/slot; a missing
/// key (the fetch has not landed, or the window has no equivalence at all)
/// renders through <see cref="WindowEquivalenceText.NoFigureReason"/> on the
/// heatmap and simply draws no line on the strip — the same "absent, not
/// blank" contract the two cards' own state machinery already uses.
/// </para>
/// </summary>
public sealed partial class DashboardView
{
    // ── Visual parameters, from QuotaHeatmapCard.swift:32-44 and
    // QuotaHistoryStripCard.swift:20-26. Named so a round of visual feedback
    // edits one line.
    private const double HeatmapRowHeight = 13;
    private const double HeatmapCellGap = 1;
    private const double HeatmapLabelWidth = 18;
    /// <summary>Faintest and strongest fill for a slot that carries anything.
    /// An occupied slot never drops to invisible: "a little" and "nothing" are
    /// different answers and the grid has to keep them apart.</summary>
    private const double HeatmapFloorOpacity = 0.14;
    private const double HeatmapPeakOpacity = 0.92;
    /// <summary>Below this share of the peak a slot is drawn at the floor
    /// rather than proportionally, so a grid with one dominant hour still shows
    /// its quiet activity instead of 167 blank cells.</summary>
    private const double HeatmapFloorShare = 0.06;
    /// <summary>An empty slot still gets a plate, so the grid reads as a grid
    /// rather than as floating squares.</summary>
    private const double HeatmapEmptyOpacity = 0.05;

    private const double StripHeight = 26;
    private const double StripBarGap = 1.5;
    /// <summary>A recorded cycle always draws something. A cycle that consumed
    /// 1% is not the same as one that was never recorded, and a bar rounding to
    /// zero height would make them identical.</summary>
    private const double StripMinimumInk = 1;
    private const double StripLatestOpacity = 0.86;
    /// <summary>The newest cycle is the one being asked about; the rest are
    /// context, so they recede.</summary>
    private const double StripOlderOpacity = 0.32;

    // ── Subscription trend card, from SubscriptionTrendCard.swift:58-80.
    private const double TrendChartHeight = 78;
    /// <summary>Wider than a hairline: whitespace between columns is what stops
    /// fourteen filled bars reading as one mass.</summary>
    private const double TrendColumnGap = 4;
    /// <summary>A day with usage always draws at least this, so a
    /// quiet-but-worked day stays distinguishable from an idle one.</summary>
    private const double TrendMinimumInk = 1.5;
    /// <summary>Resting fill for every band. Below the usage chart's 0.86 because
    /// these columns are wide and that alpha over this area is a solid block;
    /// above the strip's 0.32 because hue is data here — each band IS a
    /// subscription, and low alpha collapses brand hues into each other.</summary>
    private const double TrendBarOpacity = 0.50;
    /// <summary>Hover lifts the whole column to the strip card's own 0.95, so
    /// "the one you point at comes forward" is the same gesture with the same
    /// number.</summary>
    private const double TrendBarHoverOpacity = 0.95;
    /// <summary>The legend draws this many bands and then an overflow count:
    /// without the count a fifth subscription draws a band no legend entry
    /// accounts for.</summary>
    private const int TrendLegendCount = 4;

    private const string HeatmapWindowKey = "tokenbar.heatmap.window";

    private string _heatmapWindow =
        AppSettings.Store.GetString(HeatmapWindowKey) ?? string.Empty;

    private ChartMetric _trendMetric =
        AppSettings.Store.GetString(SubscriptionTrendText.MetricKey) == "tokens"
            ? ChartMetric.Tokens : ChartMetric.Cost;

    private UIElement BuildQuota(DashboardModel.Snapshot snapshot)
    {
        // The one snapshot-to-parameters unpack this view still does: three
        // reads (Confirmed, _model?.Year, the fields named below), no
        // decision. Everything QuotaLensProjection.Build's seven sites used
        // to decide inline now happens once, in a file a test project
        // compiles — see QuotaLensProjection's own class doc comment.
        var model = QuotaLensProjection.Build(
            snapshot.QuotaHistory,
            snapshot.Quota,
            snapshot.Graph,
            snapshot.WindowUsage,
            snapshot.WindowUsageOutcome,
            snapshot.QuotaHistoryOutcome,
            UsageAttribution.Confirmed(AppSettings.Store),
            _model?.Year,
            new QuotaLensProjection.Selection(
                _activeClientTab, _windowCardTab, _historyShownWindow, _historyShownCount));

        // A client tab asks about one subscription, so it gets that
        // subscription's own three cards rather than the all-clients four.
        if (_activeClientTab != ClientRegistry.OverviewTab)
        {
            return BuildClientQuota(snapshot, model.Client!);
        }

        var stack = new StackPanel { Spacing = 10 };
        // First: this is the one card that answers "on which subscription", and
        // the two below it answer "where has the allowance gone".
        stack.Children.Add(BuildSubscriptionTrendCard(model.Trend, model.TrendPastYearSelected));
        stack.Children.Add(BuildQuotaStripCard(
            model.Overview.Summaries, model.Overview.Outcome, model.Overview.Equivalences));
        stack.Children.Add(BuildQuotaHeatmapCard(
            model.Overview.Windows, model.Overview.Grids, model.Overview.Outcome, model.Overview.Equivalences));
        // The Agent-limits card closes the lens on macOS, below the heatmap.
        // The same builder the Overview uses, deliberately: this card answers
        // "where does the allowance stand right now" while the two above answer
        // "where has it gone", and a second implementation of the first
        // question would be free to disagree with the first one.
        stack.Children.Add(Ui.Card("Agent limits".Localized(), BuildLimits(snapshot)));
        return stack;
    }

    // ── 每日・依訂閱 ──────────────────────────────────────────────────────

    /// <summary>Daily usage stacked by the subscription the user declared it
    /// against. Not a restyled copy of the Overview chart: that one buckets by
    /// CLIENT — which tool ran the work — and the two answers routinely invert.
    /// <para>Every branch and every string is <see cref="SubscriptionTrendText"/>'s;
    /// this view decides nothing — <paramref name="trend"/> and
    /// <paramref name="pastYearSelected"/> both come from
    /// <see cref="QuotaLensProjection"/>, whose own doc comment on
    /// <c>BuildTrend</c> covers the January-crossing-into-December case this
    /// card used to get wrong.</para></summary>
    private FrameworkElement BuildSubscriptionTrendCard(SubscriptionTrend trend, bool pastYearSelected)
    {
        var body = new StackPanel { Spacing = 4 };
        var state = SubscriptionTrendText.State(trend, _trendMetric, pastYearSelected);
        if (state == SubscriptionTrendState.Chart)
        {
            body.Children.Add(TrendChart(trend));
            body.Children.Add(TrendAxis(trend));
            body.Children.Add(TrendLegend(trend));
            if (SubscriptionTrendText.UndeclaredHint(trend) is { } hint)
            {
                var line = Ui.Dim(hint, 9);
                line.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(line);
            }
        }
        else
        {
            body.Children.Add(Ui.Dim(SubscriptionTrendText.EmptyBody(state, _trendMetric)));
        }

        return Ui.Card(
            SubscriptionTrendText.Title(),
            body,
            SubscriptionTrendText.Subtitle(trend),
            TrendMetricToggle());
    }

    private FrameworkElement TrendMetricToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var metric in new[] { ChartMetric.Cost, ChartMetric.Tokens })
        {
            var pill = LensPill(
                SubscriptionTrendText.MetricLabel(metric), _trendMetric == metric);
            pill.Click += (_, _) =>
            {
                _trendMetric = metric;
                AppSettings.Store.SetString(
                    SubscriptionTrendText.MetricKey,
                    metric == ChartMetric.Cost ? "cost" : "tokens");
                RenderContent(animated: false);
            };
            row.Children.Add(pill);
        }

        return row;
    }

    /// <summary>One column per calendar day, stacked bottom-up in the fold's order
    /// so the largest payer is always the base and the bands do not reshuffle
    /// between refreshes.</summary>
    private FrameworkElement TrendChart(SubscriptionTrend trend)
    {
        var metric = _trendMetric;
        var canvas = new Canvas { Height = TrendChartHeight };
        var peak = SubscriptionTrendText.Peak(trend, metric);
        var ordered = SubscriptionTrendText.Ordered(trend, metric);

        void Draw()
        {
            canvas.Children.Clear();
            var width = canvas.ActualWidth;
            if (width <= 0 || trend.Days.Count == 0 || peak <= 0)
            {
                return;
            }

            var columnWidth = Math.Max(
                1,
                (width - (TrendColumnGap * (trend.Days.Count - 1))) / trend.Days.Count);
            for (var index = 0; index < trend.Days.Count; index++)
            {
                var day = trend.Days[index];
                var dayTotal = SubscriptionTrendText.DayTotal(day, metric);
                var x = index * (columnWidth + TrendColumnGap);
                var bands = new List<(Rectangle Rect, Color Color)>();
                if (dayTotal > 0)
                {
                    var fullHeight = Math.Max(
                        TrendMinimumInk, TrendChartHeight * (dayTotal / peak));
                    var cursor = TrendChartHeight;
                    foreach (var target in ordered)
                    {
                        if (!day.ByTarget.TryGetValue(target, out var bucket))
                        {
                            continue;
                        }

                        var segment = fullHeight
                            * (SubscriptionTrendText.Value(bucket, metric) / dayTotal);
                        if (segment <= 0)
                        {
                            continue;
                        }

                        cursor -= segment;
                        var color = Ui.BrushFromHex(
                            SubscriptionTrendText.TargetColor(target)).Color;
                        var rect = new Rectangle
                        {
                            Width = columnWidth,
                            Height = segment,
                            RadiusX = Math.Min(2, segment / 2),
                            RadiusY = Math.Min(2, segment / 2),
                            Fill = new SolidColorBrush(Tint(color, TrendBarOpacity)),
                        };
                        Canvas.SetLeft(rect, x);
                        Canvas.SetTop(rect, cursor);
                        canvas.Children.Add(rect);
                        bands.Add((rect, color));
                    }
                }

                // Full-height hover column: one tooltip per day, and the only
                // thing that says which day an idle column belongs to.
                var overlay = new Rectangle
                {
                    Width = columnWidth + TrendColumnGap,
                    Height = TrendChartHeight,
                    Fill = new SolidColorBrush(Colors.Transparent),
                };
                Canvas.SetLeft(overlay, x - (TrendColumnGap / 2));
                Canvas.SetTop(overlay, 0);
                var lift = bands;
                AttachHoverOutline(overlay, isHovered =>
                {
                    foreach (var (rect, color) in lift)
                    {
                        rect.Fill = new SolidColorBrush(Tint(
                            color, isHovered ? TrendBarHoverOpacity : TrendBarOpacity));
                    }
                });
                var hoveredDay = day;
                HoverTip.AttachRich(overlay, () => TrendTip(trend, hoveredDay));
                canvas.Children.Add(overlay);
            }
        }

        canvas.SizeChanged += (_, _) => Draw();
        return canvas;
    }

    private UIElement TrendTip(SubscriptionTrend trend, SubscriptionTrend.Day day)
    {
        var metric = _trendMetric;
        var panel = new StackPanel { Spacing = 3, MinWidth = 176 };
        panel.Children.Add(TipText(Format.MonthDay(day.Date), 11, bold: true));
        if (day.IsEmpty)
        {
            panel.Children.Add(TipText(SubscriptionTrendText.NoUsageThisDay(), 9, 0.6));
            return panel;
        }

        // Largest first, matching the legend. The stack itself is
        // largest-at-the-bottom, so this list is the reverse of the column's
        // top-to-bottom order — consistency across tooltips wins.
        foreach (var target in SubscriptionTrendText.Ordered(trend, metric))
        {
            if (!day.ByTarget.TryGetValue(target, out var bucket))
            {
                continue;
            }

            panel.Children.Add(TipRow(
                TipLabel(
                    SubscriptionTrendText.TargetColor(target),
                    SubscriptionTrendText.TargetName(target)),
                SubscriptionTrendText.Amount(bucket.Tokens, bucket.Cost, metric)));
        }

        panel.Children.Add(TipRow(
            TipText(SubscriptionTrendText.TotalLabel(), 10),
            SubscriptionTrendText.Amount(day.TotalTokens, day.TotalCost, metric),
            0.9));
        return panel;
    }

    /// <summary>Ends only. At this width a label per column is unreadable, and the
    /// shape is what the card is for.</summary>
    private static FrameworkElement TrendAxis(SubscriptionTrend trend) => Ui.Row(
        Ui.Text(Format.MonthDay(trend.Days[0].Date), 9, 0.45),
        Ui.Text(Format.MonthDay(trend.Days[^1].Date), 9, 0.45));

    private FrameworkElement TrendLegend(SubscriptionTrend trend)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var ordered = SubscriptionTrendText.Ordered(trend, _trendMetric);
        foreach (var target in ordered.Take(TrendLegendCount))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(Ui.Disc(SubscriptionTrendText.TargetColor(target), 6));
            item.Children.Add(Ui.Text(SubscriptionTrendText.TargetName(target), 9, 0.7));
            row.Children.Add(item);
        }

        if (ordered.Count > TrendLegendCount)
        {
            row.Children.Add(Ui.Text($"+{ordered.Count - TrendLegendCount}", 9, 0.45));
        }

        return row;
    }

    // ── The per-client lens (5e) ─────────────────────────────────────────

    // Visual parameters, from WindowUsageCard.swift:44-50 and its draw(). Named
    // so a round of visual feedback edits one line.
    private const double WindowChartHeight = 96;
    /// <summary>The bar band is a third of the chart. The line uses the whole
    /// box, so the two overlap by design: in "used" mode early in a window the
    /// line runs low, straight through the bars. Rather than move either
    /// series, the bars recede and light one at a time under the cursor while
    /// the line stays in front.</summary>
    private const double WindowBarBand = 32;
    private const double WindowBarRestOpacity = 0.17;
    private const double WindowBarHoverOpacity = 0.62;
    /// <summary>The hatch's plate and its stripes. One weight for both
    /// no-sample stretches — the leading one and the future — because they mean
    /// the same thing: no quota reading here, so no line.</summary>
    private const double WindowHatchPlateOpacity = 0.06;
    private const double WindowHatchStrokeOpacity = 0.18;
    /// <summary>Horizontal step between stripes, in pixels. Any denser and the
    /// hatch reads as a solid block at popover width.</summary>
    private const double WindowHatchStep = 5;
    private const double WindowCurveWidth = 1.8;
    private const double WindowSampleDot = 4;

    private string _windowCardTab =
        AppSettings.Store.GetString(WindowCardText.TabKey) ?? string.Empty;

    /// <summary>Read fresh every time, not cached: it shares the store key
    /// (<see cref="WindowCardText.AsUsedKey"/>) with the Agent-limits card's
    /// own "Show as used" toggle (<c>BuildLimits</c>, which already reads the
    /// key directly), and a cached copy here drifted from that card's live
    /// reads until the control was recreated. Called once per render, not
    /// per cell — round 11's fold work (a3ea946) made the whole lens rebuild
    /// cost 68-75ms on a 100k-message synthetic payload, so one more
    /// dictionary lookup in that pass is not the part that matters.</summary>
    private static QuotaMetric _windowMetric =>
        AppSettings.Store.GetBool(WindowCardText.AsUsedKey, false)
            ? QuotaMetric.Used : QuotaMetric.Remaining;

    /// <summary>Which history row is open, keyed by the cycle's reset instant —
    /// the same value the fold uses as a row's identity.</summary>
    private long? _historyExpanded;

    /// <summary>How many history rows the reader has grown the card to, and
    /// which window they grew it on. Stored as a pair and handed to
    /// <see cref="QuotaLensProjection.Selection"/> together, because the count
    /// alone is meaningless: the projection honours it only while the window
    /// it names is still the one that resolves, and otherwise opens at
    /// <see cref="WindowHistoryText.VisibleRows"/> again. Not persisted — a
    /// list grown during one reading of the lens is not a preference.</summary>
    private string? _historyShownWindow;
    private int _historyShownCount = WindowHistoryText.VisibleRows;

    /// <summary>One subscription's own three cards: the window it is in now,
    /// where its allowance stands, and the windows before this one.</summary>
    private UIElement BuildClientQuota(
        DashboardModel.Snapshot snapshot, QuotaLensProjection.Client client)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildWindowCard(client));
        // The same builder the Overview and the all-clients lens use, filtered
        // to this client. A second implementation of "where does the allowance
        // stand right now" would be free to disagree with the first.
        stack.Children.Add(Ui.Card("Agent limits".Localized(), BuildLimits(snapshot, client.Owner)));
        // Every subscription-facing lookup, including the history card's
        // disclaimer (WindowHistoryText.Disclaimer), reads client.Owner —
        // the confirmed attribution target these rows were folded against
        // (round 19's finding: antigravity-cli's rows are declared as
        // "Antigravity", not the raw tab id's own "Antigravity CLI").
        stack.Children.Add(BuildWindowHistoryCard(snapshot, client));
        return stack;
    }

    private FrameworkElement BuildWindowCard(QuotaLensProjection.Client client)
    {
        var tabs = client.Tabs;
        var selected = client.Selected;
        var state = WindowCardText.State(selected, client.QuotaHistoryOutcome);
        var body = new StackPanel { Spacing = 4 };
        if (tabs.Count > 1)
        {
            body.Children.Add(WindowTabs(tabs, selected));
        }

        if (state != WindowCardState.Chart)
        {
            var line = Ui.Dim(WindowCardText.EmptyBody(state));
            line.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(line);
            return Ui.Card(
                WindowCardText.Title(selected),
                body,
                WindowCardText.Subtitle(state, selected, DateTimeOffset.Now));
        }

        var active = selected!.Active!;
        var start = active.StartMs!.Value;
        var end = active.ResetAtMs!.Value;
        // Clamped to the window's own end: past reset the provider's next
        // reading opens a new cycle, and a `now` beyond the axis would put the
        // hatch and the zones outside the box they are drawn in.
        var now = Math.Min(DateTimeOffset.Now.ToUnixTimeMilliseconds(), end);
        var mine = client.Mine;
        var geometry = WindowCardGeometry.Chart(start, end, now, active.Samples, mine, _windowMetric);

        var (percent, caption) = WindowCardText.Headline(geometry, _windowMetric);
        var headline = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (percent is not null)
        {
            headline.Children.Add(Ui.Text(percent, 22, bold: true));
        }

        headline.Children.Add(Ui.Dim(caption));
        body.Children.Add(headline);
        body.Children.Add(WindowChart(geometry, mine));
        body.Children.Add(WindowLegend(geometry));

        // "10% of quota ~ X tokens · $Y", live off this window's own samples —
        // the same line the History card prints pooled over past cycles, here
        // for the one currently running. Computed once by the projection
        // (QuotaLensProjection.BuildClient), the same declared/outcome
        // reasoning this comment used to carry inline — see that method's own
        // comments for why: an unclassified machine reads "classify your
        // usage" rather than "nothing was recorded", and the outcome is the
        // quota-samples fetch's own, not the card's QuotaHistoryOutcome
        // (client.QuotaHistoryOutcome, used for `state` above), because the
        // two are separate fetches. Never
        // null here: WindowCardText.State only reaches Chart when the
        // projection's own guard for LiveEquivalence (a placed active cycle)
        // already held.
        var equivalenceLine = Ui.Text(WindowEquivalenceText.Line(client.LiveEquivalence!), 9, 0.6);
        equivalenceLine.TextWrapping = TextWrapping.Wrap;
        equivalenceLine.Margin = new Thickness(0, 2, 0, 0);
        body.Children.Add(equivalenceLine);

        if (WindowCardText.UndatedNote(client.UndatedCount) is { } undated)
        {
            var note = Ui.Dim(undated, 9);
            note.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(note);
        }

        return Ui.Card(
            WindowCardText.Title(selected),
            body,
            WindowCardText.Subtitle(state, selected, DateTimeOffset.Now),
            WindowMetricToggle());
    }

    /// <summary>One sub-tab per window this client has recorded. Pills rather
    /// than a menu: a client has two or three windows, and which one you are
    /// looking at is the card's own subject.</summary>
    private FrameworkElement WindowTabs(
        IReadOnlyList<WindowCardTab> tabs, WindowCardTab? selected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var tab in tabs)
        {
            var pill = LensPill(WindowCardText.TabLabel(tab), tab == selected);
            var id = WindowId(tab.Id);
            pill.Click += (_, _) =>
            {
                _windowCardTab = id;
                AppSettings.Store.SetString(WindowCardText.TabKey, id);
                RenderContent(animated: false);
            };
            row.Children.Add(pill);
        }

        return row;
    }

    private FrameworkElement WindowMetricToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var metric in new[] { QuotaMetric.Remaining, QuotaMetric.Used })
        {
            var pill = LensPill(WindowCardText.MetricLabel(metric), _windowMetric == metric);
            pill.Click += (_, _) =>
            {
                // The Agent-limits card's own key: one preference, so the two
                // cards on this page cannot count in opposite directions.
                // No local field to set — _windowMetric reads this key live.
                AppSettings.Store.SetBool(WindowCardText.AsUsedKey, metric == QuotaMetric.Used);
                RenderContent(animated: false);
            };
            row.Children.Add(pill);
        }

        return row;
    }

    /// <summary>Three series in one box: the hatched no-sample regions, the
    /// usage bars, and the quota line with its sample dots.</summary>
    private FrameworkElement WindowChart(
        ChartGeometry geometry, IReadOnlyList<WindowMessage> mine)
    {
        var canvas = new Canvas { Height = WindowChartHeight };
        var accent = AccentColor();

        void Draw()
        {
            canvas.Children.Clear();
            var width = canvas.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            // Drawn, not left blank. A gap would say "nothing happened here";
            // the hatch says "we did not look here", which is the fact.
            foreach (var (from, to) in WindowCardText.NoSampleRegions(geometry))
            {
                canvas.Children.Add(Hatch(from * width, (to - from) * width));
            }

            // Bars and zones are produced together and share an index, so the
            // zone under the cursor lights its own bar and nothing else.
            var lit = new Rectangle?[geometry.Bars.Count];
            for (var index = 0; index < geometry.Bars.Count; index++)
            {
                var bar = geometry.Bars[index];
                var x = bar.X * width;
                var barWidth = Math.Max((bar.Width * width) - 1, 0.5);
                if (bar.IsEmpty)
                {
                    // A baseline tick, so "nothing was spent in this interval"
                    // stays distinguishable from "no data here".
                    var tick = new Rectangle
                    {
                        Width = barWidth,
                        Height = 1,
                        Fill = new SolidColorBrush(Tint(Colors.Gray, 0.28)),
                    };
                    Canvas.SetLeft(tick, x);
                    Canvas.SetTop(tick, WindowChartHeight - 1);
                    canvas.Children.Add(tick);
                    continue;
                }

                var height = Math.Max(bar.Height * WindowBarBand, 1);
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Tint(accent, WindowBarRestOpacity)),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, WindowChartHeight - height);
                canvas.Children.Add(rect);
                lit[index] = rect;
            }

            // The line last, so it is in front of the bars it explains.
            if (geometry.Curve.Count > 1)
            {
                var points = new PointCollection();
                foreach (var point in geometry.Curve)
                {
                    points.Add(new Windows.Foundation.Point(point.X * width, CurveY(point.Y)));
                }

                canvas.Children.Add(new Polyline
                {
                    Points = points,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = WindowCurveWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false,
                });
            }

            foreach (var point in geometry.SamplePoints)
            {
                var dot = new Ellipse
                {
                    Width = WindowSampleDot,
                    Height = WindowSampleDot,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 1.1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, (point.X * width) - (WindowSampleDot / 2));
                Canvas.SetTop(dot, CurveY(point.Y) - (WindowSampleDot / 2));
                canvas.Children.Add(dot);
            }

            // Hit zones on top: they tile [windowStart, now] exactly, so the
            // future is unhittable by construction rather than by intent.
            foreach (var zone in geometry.Hits)
            {
                var overlay = new Rectangle
                {
                    Width = Math.Max(zone.Width * width, 1),
                    Height = WindowChartHeight,
                    Fill = new SolidColorBrush(Colors.Transparent),
                };
                Canvas.SetLeft(overlay, zone.X * width);
                Canvas.SetTop(overlay, 0);
                var hovered = zone;
                var bar = zone.Index < lit.Length ? lit[zone.Index] : null;
                if (bar is not null)
                {
                    AttachHoverOutline(overlay, isHovered =>
                        bar.Fill = new SolidColorBrush(Tint(
                            accent, isHovered ? WindowBarHoverOpacity : WindowBarRestOpacity)));
                }

                HoverTip.AttachRich(overlay, () => WindowZoneTip(hovered, mine));
                canvas.Children.Add(overlay);
            }
        }

        canvas.SizeChanged += (_, _) => Draw();
        return canvas;
    }

    /// <summary>Fixed 0…100: rescaling would make 7% used and 63% used look the
    /// same, which is the one thing this card exists to tell apart.</summary>
    private static double CurveY(double value) =>
        10 + ((1 - (value / 100)) * (WindowChartHeight - 16));

    private static FrameworkElement Hatch(double left, double width)
    {
        var host = new Canvas
        {
            Width = width,
            Height = WindowChartHeight,
            // Scoped so the stripes cannot leak onto the bars and line beside
            // them.
            Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, width, WindowChartHeight),
            },
            IsHitTestVisible = false,
        };
        host.Children.Add(new Rectangle
        {
            Width = width,
            Height = WindowChartHeight,
            Fill = new SolidColorBrush(Tint(Colors.Gray, WindowHatchPlateOpacity)),
        });
        var stroke = new SolidColorBrush(Tint(Colors.Gray, WindowHatchStrokeOpacity));
        for (var x = -WindowChartHeight; x < width; x += WindowHatchStep)
        {
            host.Children.Add(new Line
            {
                X1 = x,
                Y1 = WindowChartHeight,
                X2 = x + WindowChartHeight,
                Y2 = 0,
                Stroke = stroke,
                StrokeThickness = 0.6,
            });
        }

        Canvas.SetLeft(host, left);
        Canvas.SetTop(host, 0);
        return host;
    }

    private UIElement WindowZoneTip(HitZone zone, IReadOnlyList<WindowMessage> mine)
    {
        var panel = new StackPanel { Spacing = 3, MinWidth = 186 };
        panel.Children.Add(TipText(
            WindowCardText.ClockRange(zone.LoMs, zone.HiMs), 11, bold: true));
        panel.Children.Add(TipText(WindowCardText.ZoneQuota(zone, _windowMetric), 9, 0.6));
        if (WindowCardText.ZoneConsumed(zone, _windowMetric) is { } consumed)
        {
            panel.Children.Add(TipText(consumed, 9));
        }

        var (tokens, money, empty) = WindowCardText.ZoneUsage(WindowCardText.InZone(mine, zone));
        if (empty is not null)
        {
            panel.Children.Add(TipText(empty, 9, 0.6));
        }
        else
        {
            panel.Children.Add(TipRow(TipText(tokens!, 9, 0.6), money!, 0.6));
        }

        return panel;
    }

    private FrameworkElement WindowLegend(ChartGeometry geometry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var accent = AccentColor();
        row.Children.Add(LegendKey(accent, 1.0, WindowCardText.QuotaKey(), line: true));
        row.Children.Add(LegendKey(accent, 0.5, WindowCardText.UsageKey(), line: false));
        // The hatch has a legend entry because it is a series, not a gap: a
        // reader has to be able to look it up.
        row.Children.Add(LegendKey(Colors.Gray, 0.3, WindowCardText.NoSampleKey(), line: false));
        row.Children.Add(Ui.Text(
            WindowCardText.Readings(geometry.SamplePoints.Count), 9, 0.45));
        return row;
    }

    private static FrameworkElement LegendKey(Color color, double opacity, string label, bool line)
    {
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        item.Children.Add(new Rectangle
        {
            Width = line ? 12 : 8,
            Height = line ? 2 : 8,
            RadiusX = line ? 1 : 2,
            RadiusY = line ? 1 : 2,
            Fill = new SolidColorBrush(Tint(color, opacity)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        item.Children.Add(Ui.Text(label, 9, 0.7));
        return item;
    }

    // ── 時間窗歷史 ────────────────────────────────────────────────────────

    private const double HistoryBarWidth = 62;
    private const double HistoryBarHeight = 4;
    private const double HistoryQuotaOpacity = 0.85;
    /// <summary>A barely-witnessed cycle draws its quota bar faint: the figure
    /// is a floor, and the bar must not read as a measurement.</summary>
    private const double HistoryThinOpacity = 0.35;

    private FrameworkElement BuildWindowHistoryCard(
        DashboardModel.Snapshot snapshot, QuotaLensProjection.Client client)
    {
        var history = client.History;
        var rows = history.DisplayRows;

        var body = new StackPanel { Spacing = 0 };
        var state = WindowHistoryText.State(rows, client.QuotaHistoryOutcome);
        if (state != WindowHistoryState.Rows)
        {
            var line = Ui.Dim(WindowHistoryText.EmptyBody(state));
            line.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(line);
            return Ui.Card(WindowHistoryText.Title(), body);
        }

        // "10% of quota ~ X tokens · $Y" — pooled over the rows actually
        // shown, above them, because a single row's ratio is dominated by
        // the 1-point reading quantisation. Gated on WindowUsageOutcome, not
        // the card-level QuotaHistoryOutcome `state` above — see
        // QuotaLensProjection.BuildHistory's own comment for why (the quota
        // samples and the message export are two separate fetches).
        var equivalenceLine = Ui.Text(WindowEquivalenceText.Line(history.Equivalence), 9, 0.6);
        equivalenceLine.TextWrapping = TextWrapping.Wrap;
        equivalenceLine.Margin = new Thickness(0, 0, 0, 6);
        body.Children.Add(equivalenceLine);

        var colors = new ModelColorMap(snapshot.Models, snapshot.CostAuthoritative);
        foreach (var row in rows)
        {
            body.Children.Add(HistoryRow(row, history.ByResetAt[row.ResetAtMs], colors));
        }

        // The grow control, above the disclaimer so it reads as belonging to
        // the list rather than to the small print. Absent once every admitted
        // cycle is drawn, which is also how the card says there is no more
        // history rather than leaving a control that would do nothing.
        if (history.Remaining > 0)
        {
            // Same control the Hourly lens's timeline grows itself with, so
            // the gesture is one the reader has already met.
            var more = LensPill(WindowHistoryText.ShowMore(history.Remaining), false);
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Margin = new Thickness(0, 6, 0, 0);
            // The window this press grows, captured from the projection's own
            // resolution rather than from _windowCardTab: the two disagree
            // exactly when the stored preference names a window this client
            // does not offer, and the count must follow the rows.
            var window = history.ShownWindow;
            // From what is DRAWN, not from the stored count — see
            // WindowHistoryText.Grown, which holds that decision because this
            // file is WinUI and no test project compiles it.
            var grown = WindowHistoryText.Grown(rows.Count);
            more.Click += (_, _) =>
            {
                _historyShownWindow = window;
                _historyShownCount = grown;
                RenderContent(animated: false);
            };
            body.Children.Add(more);
        }

        // The line that keeps the money column from reading as a bill.
        // client.Owner, not clientId — see BuildClientQuota's own comment.
        var disclaimer = Ui.Text(WindowHistoryText.Disclaimer(client.Owner), 9, 0.45);
        disclaimer.TextWrapping = TextWrapping.Wrap;
        disclaimer.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(disclaimer);
        return Ui.Card(WindowHistoryText.Title(), body, WindowHistoryText.Subtitle(rows));
    }

    private FrameworkElement HistoryRow(WindowHistoryRow row, QuotaHistoryRow historyRow, ModelColorMap colors)
    {
        var open = _historyExpanded == row.ResetAtMs;
        var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, 5, 0, 5) };

        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Background = new SolidColorBrush(Colors.Transparent), // hit-test
        };
        head.Children.Add(Ui.Text(open ? "▾" : "▸", 8, 0.45));
        head.Children.Add(Ui.Text(row.Stamp, 9, 0.6));
        head.Children.Add(HistoryBars(row, historyRow, colors));
        var percent = Ui.Text(WindowHistoryText.Percent(row), 9);
        percent.Width = 30;
        percent.TextAlignment = TextAlignment.Right;
        head.Children.Add(percent);
        head.Children.Add(Ui.Text(WindowHistoryText.Tokens(row), 9, 0.6));
        var cost = Ui.Text(WindowHistoryText.Cost(row), 9);
        cost.Width = 54;
        cost.TextAlignment = TextAlignment.Right;
        head.Children.Add(cost);
        var id = row.ResetAtMs;
        head.Tapped += (_, _) =>
        {
            _historyExpanded = open ? null : id;
            RenderContent(animated: false);
        };
        block.Children.Add(head);

        if (open)
        {
            var detail = new StackPanel { Spacing = 3, Margin = new Thickness(16, 2, 0, 0) };
            // The full interval lives here rather than in the row: it is what a
            // reader opens a row to confirm.
            detail.Children.Add(Ui.Text(row.Range, 9, 0.45));
            if (WindowHistoryText.ThinObservationNote(row) is { } note)
            {
                var line = Ui.Text(note, 9);
                line.Foreground = Ui.BrushFromHex(PaceOrange);
                line.TextWrapping = TextWrapping.Wrap;
                detail.Children.Add(line);
            }

            // The heaviest four of this subscription's models, or the line
            // that says none of this window was charged to it — never a bare
            // empty expander, which reads as a card that gave up mid-scan.
            //
            // Not distinguished from "the usage scan has not landed yet":
            // this card's collapsed row already shows 0 tokens rather than a
            // spinner while that scan is out (WindowHistoryText.Rows draws
            // every cycle regardless), so this detail view keeps the same
            // choice rather than adding a second wait state the row above it
            // does not have.
            var topModels = WindowHistoryText.TopModels(historyRow);
            if (topModels.Count == 0)
            {
                detail.Children.Add(Ui.Text(WindowHistoryText.NothingChargedNote(), 10, 0.45));
            }
            else
            {
                foreach (var model in topModels)
                {
                    detail.Children.Add(ModelDetailRow(model));
                }
            }

            // The line that explains a flat quota bar: everything else
            // recorded in the same hours, named by which attribution states
            // it actually holds.
            if (WindowHistoryText.SameHoursLine(historyRow) is { } sameHours)
            {
                var line = Ui.Text(sameHours, 9, 0.45);
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 2, 0, 0);
                detail.Children.Add(line);
            }

            block.Children.Add(detail);
        }

        return block;
    }

    private static FrameworkElement ModelDetailRow(QuotaHistoryModel model)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });

        // Ui.Text already trims to an ellipsis by default.
        grid.Children.Add(Ui.Text(model.ModelId, 10, 0.85));

        var tokens = Ui.Text(WindowHistoryText.ModelTokens(model), 10, 0.6);
        Grid.SetColumn(tokens, 1);
        grid.Children.Add(tokens);

        var cost = Ui.Text(WindowHistoryText.ModelCost(model), 10);
        cost.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(cost, 2);
        grid.Children.Add(cost);

        return grid;
    }

    /// <summary>Two bars, stacked and deliberately NOT sharing a scale. Quota
    /// and tokens are not proportional — measured on live data one window moved
    /// 1% of the allowance carrying 18.0M attributed tokens and another moved
    /// 9% carrying 22.8M — so one bar, or one axis, would draw a relationship
    /// that is not there. Adjacent and separately scaled, the mismatch between
    /// the two lengths is legible instead of hidden, and that mismatch is what
    /// this card exists to expose.</summary>
    private FrameworkElement HistoryBars(WindowHistoryRow row, QuotaHistoryRow historyRow, ModelColorMap colors)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(HistoryBar(
            row.QuotaFraction,
            AccentColor(),
            row.ThinObservation ? HistoryThinOpacity : HistoryQuotaOpacity));
        // Relative to the heaviest window on screen, segmented by model in
        // the app's shared palette. An empty strip is a real answer: this
        // window consumed allowance and nothing in it was declared as this
        // subscription's.
        stack.Children.Add(HistoryUsageBar(row, historyRow, colors));
        return stack;
    }

    private static FrameworkElement HistoryBar(double fraction, Color color, double opacity)
    {
        var host = new Grid { Width = HistoryBarWidth, Height = HistoryBarHeight };
        host.Children.Add(new Rectangle
        {
            Height = HistoryBarHeight,
            RadiusX = HistoryBarHeight / 2,
            RadiusY = HistoryBarHeight / 2,
            Fill = new SolidColorBrush(Tint(Colors.Gray, 0.2)),
        });
        if (fraction > 0)
        {
            host.Children.Add(new Rectangle
            {
                Width = Math.Max(1, HistoryBarWidth * Math.Min(1, fraction)),
                Height = HistoryBarHeight,
                RadiusX = HistoryBarHeight / 2,
                RadiusY = HistoryBarHeight / 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new SolidColorBrush(Tint(color, opacity)),
            });
        }

        return host;
    }

    /// <summary>The usage bar, coloured per model instead of one flat tint —
    /// the model-segmented bar. <see cref="HistoryBar"/> stays the plain track
    /// for the quota half above it and for every empty usage strip.</summary>
    private static FrameworkElement HistoryUsageBar(
        WindowHistoryRow row, QuotaHistoryRow historyRow, ModelColorMap colors)
    {
        var segments = WindowHistoryText.Segments(historyRow, colors);
        if (row.UsageFraction <= 0 || segments.Count == 0)
        {
            // Same flat tint the bar drew before it was model-segmented. In
            // practice this is unreachable whenever SpanTokens > 0, since the
            // span is a subset of MineTokens' wider window — but a defensive
            // fallback for the edge where they disagree should look like the
            // established bar, not invent a second visual language for it.
            return HistoryBar(row.UsageFraction, AccentColor(), 0.55);
        }

        var full = Math.Max(1, HistoryBarWidth * Math.Min(1, row.UsageFraction));
        var host = new Grid { Width = HistoryBarWidth, Height = HistoryBarHeight };
        host.Children.Add(new Rectangle
        {
            Height = HistoryBarHeight,
            RadiusX = HistoryBarHeight / 2,
            RadiusY = HistoryBarHeight / 2,
            Fill = new SolidColorBrush(Tint(Colors.Gray, 0.2)),
        });

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = full,
            Height = HistoryBarHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, full, HistoryBarHeight) },
        };
        foreach (var segment in segments)
        {
            strip.Children.Add(new Rectangle
            {
                Width = Math.Max(0, full * segment.Fraction),
                Height = HistoryBarHeight,
                Fill = Ui.BrushFromHex(segment.Color),
            });
        }

        host.Children.Add(strip);
        return host;
    }

    // ── Strip card ───────────────────────────────────────────────────────

    private FrameworkElement BuildQuotaStripCard(
        IReadOnlyList<QuotaWindowSummary> summaries, WindowEquivalence.FetchOutcome outcome,
        IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> equivalences)
    {
        var body = new StackPanel { Spacing = 10 };
        switch (QuotaLensText.StripState(summaries, outcome))
        {
            case QuotaStripState.Rows:
                foreach (var summary in summaries)
                {
                    body.Children.Add(StripRow(summary, equivalences.GetValueOrDefault(summary.Id)));
                }

                break;
            case QuotaStripState.NoCompletedWindows:
                body.Children.Add(Ui.Dim(QuotaLensText.NoCompletedWindows()));
                break;
            case QuotaStripState.Failed:
                body.Children.Add(Ui.Dim(QuotaLensText.Failed()));
                break;
            default:
                body.Children.Add(Ui.Dim(QuotaLensText.Loading()));
                break;
        }

        return Ui.Card(
            QuotaLensText.StripTitle(), body, QuotaLensText.StripSubtitle(summaries));
    }

    private FrameworkElement StripRow(QuotaWindowSummary summary, WindowEquivalence.Row? equivalence)
    {
        var block = new StackPanel { Spacing = 3 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        head.Children.Add(Ui.Disc(ClientRegistry.Style(summary.Id.ProviderId).Color, 8));
        head.Children.Add(Ui.Text(QuotaLabels.RowLabel(summary), 11));
        block.Children.Add(Ui.Row(
            head, Ui.Text(QuotaLensText.WindowCount(summary.CycleCount), 9, 0.5)));
        block.Children.Add(Strip(summary));
        block.Children.Add(Ui.Text(QuotaLensText.Headline(summary), 9, 0.5));
        if (equivalence is not null)
        {
            var line = Ui.Dim(WindowEquivalenceText.Line(equivalence), 9);
            line.TextWrapping = TextWrapping.Wrap;
            block.Children.Add(line);
        }

        return block;
    }

    /// <summary>Fixed 0…100 with the ceiling drawn as a dashed rule. Rescaling
    /// to the tallest bar would make a run of 2% cycles look like a run of 60%
    /// ones, which is the one comparison this strip exists to support.</summary>
    private FrameworkElement Strip(QuotaWindowSummary summary)
    {
        var bars = new Grid { Height = StripHeight, ColumnSpacing = StripBarGap };
        var accent = AccentColor();
        for (var index = 0; index < summary.Recent.Count; index++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            var value = summary.Recent[index];
            var latest = index == summary.Recent.Count - 1;
            var column = new Grid
            {
                Background = new SolidColorBrush(Colors.Transparent), // hit-test
            };
            column.Children.Add(new Border
            {
                Height = Math.Max(StripMinimumInk, StripHeight * Math.Min(1, value / 100)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(
                    Tint(accent, latest ? StripLatestOpacity : StripOlderOpacity)),
            });
            // The strip is oldest-to-newest, so counting back from the end is
            // what turns a bar into "how many windows ago".
            var ago = summary.Recent.Count - 1 - index;
            var peak = index < summary.RecentPeaks.Count ? summary.RecentPeaks[index] : 0;
            HoverTip.AttachRich(column, () => StripTip(summary, ago, value, peak));
            Grid.SetColumn(column, index);
            bars.Children.Add(column);
        }

        var host = new Grid();
        host.Children.Add(bars);
        host.Children.Add(new Rectangle
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Stroke = new SolidColorBrush(Tint(accent, 0.35)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
        });
        return host;
    }

    private static UIElement StripTip(
        QuotaWindowSummary summary, int windowsAgo, double usedPercent, double peakPercent)
    {
        var panel = new StackPanel { Spacing = 3, MinWidth = 168 };
        panel.Children.Add(TipText(QuotaLabels.RowLabel(summary), 11, bold: true));
        panel.Children.Add(TipText(QuotaLensText.BarAge(windowsAgo), 9, 0.6));
        panel.Children.Add(TipText(QuotaLensText.BarConsumed(usedPercent)));
        if (peakPercent >= QuotaOverviewFold.ExhaustedPercent)
        {
            var ranOut = TipText(QuotaLensText.RanOut(), 9);
            ranOut.Foreground = Ui.BrushFromHex(PaceOrange);
            panel.Children.Add(ranOut);
        }

        return panel;
    }

    // ── Heatmap card ─────────────────────────────────────────────────────

    private FrameworkElement BuildQuotaHeatmapCard(
        IReadOnlyList<QuotaHeatmapWindow> windows,
        IReadOnlyDictionary<QuotaWindowIdentity, QuotaHeatmap> grids,
        WindowEquivalence.FetchOutcome outcome,
        IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> equivalences)
    {
        // The list already excludes windows with no movement and leads with the
        // heaviest, so the fallback is simply "the first one" rather than a
        // search for one with something to draw.
        var selected = windows.FirstOrDefault(w => WindowId(w.Id) == _heatmapWindow)
            ?? windows.FirstOrDefault();
        var grid = selected is null ? null : grids.GetValueOrDefault(selected.Id);

        var equivalence = selected is null ? null : equivalences.GetValueOrDefault(selected.Id);
        var body = new StackPanel { Spacing = 4 };
        switch (QuotaLensText.HeatmapState(grid, outcome))
        {
            case QuotaHeatmapState.Grid:
                body.Children.Add(Heatmap(grid!, equivalence));
                body.Children.Add(HourAxis());
                if (QuotaLensText.Footnote(grid!) is { } footnote)
                {
                    body.Children.Add(Ui.Dim(footnote, 9));
                }

                break;
            case QuotaHeatmapState.Unplaced:
                body.Children.Add(Ui.Dim(QuotaLensText.UnplacedBody(grid!.UnplacedPercent)));
                break;
            case QuotaHeatmapState.NoMovement:
                body.Children.Add(Ui.Dim(QuotaLensText.NoMovement()));
                break;
            case QuotaHeatmapState.Failed:
                body.Children.Add(Ui.Dim(QuotaLensText.Failed()));
                break;
            default:
                body.Children.Add(Ui.Dim(QuotaLensText.Loading()));
                break;
        }

        return Ui.Card(
            QuotaLensText.HeatmapTitle(),
            body,
            QuotaLensText.HeatmapSubtitle(grid),
            windows.Count > 1 ? WindowPicker(windows, selected) : null);
    }

    /// <summary>A menu rather than a segmented control: window names are long
    /// enough that four of them do not fit across the popover, and this card is
    /// not the place to abbreviate a subscription's own label.</summary>
    private FrameworkElement WindowPicker(
        IReadOnlyList<QuotaHeatmapWindow> windows, QuotaHeatmapWindow? selected)
    {
        var flyout = new MenuFlyout();
        foreach (var window in windows)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = QuotaLabels.PickerLabel(window),
                IsChecked = window == selected,
                GroupName = HeatmapWindowKey,
            };
            var id = WindowId(window.Id);
            item.Click += (_, _) =>
            {
                _heatmapWindow = id;
                AppSettings.Store.SetString(HeatmapWindowKey, id);
                RenderContent(animated: false);
            };
            flyout.Items.Add(item);
        }

        return new Button
        {
            // A control label, not content: below the subtitle's size and well
            // below the title, so it does not read as a second heading.
            Content = selected is null ? string.Empty : QuotaLabels.PickerLabel(selected),
            FontSize = 9,
            Opacity = 0.6,
            Padding = new Thickness(6, 2, 6, 2),
            Flyout = flyout,
        };
    }

    private FrameworkElement Heatmap(QuotaHeatmap grid, WindowEquivalence.Row? equivalence)
    {
        var accent = AccentColor();
        var host = new Grid { ColumnSpacing = 0 };
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(HeatmapLabelWidth),
        });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var labels = new StackPanel { Spacing = HeatmapCellGap };
        var cells = new Grid
        {
            ColumnSpacing = HeatmapCellGap,
            RowSpacing = HeatmapCellGap,
        };
        for (var hour = 0; hour < 24; hour++)
        {
            cells.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        for (var weekday = 0; weekday < 7; weekday++)
        {
            cells.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(HeatmapRowHeight),
            });
            var label = Ui.Text(QuotaLensText.WeekdayLabels[weekday].Localized(), 8, 0.45);
            label.Height = HeatmapRowHeight;
            labels.Children.Add(label);
            for (var hour = 0; hour < 24; hour++)
            {
                var value = grid.Cells[weekday][hour];
                var cell = new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(CellFill(accent, value, grid.Peak)),
                };
                var (day, slot) = (weekday, hour);
                HoverTip.AttachRich(cell, () => SlotTip(day, slot, value, equivalence));
                Grid.SetRow(cell, weekday);
                Grid.SetColumn(cell, hour);
                cells.Children.Add(cell);
            }
        }

        Grid.SetColumn(cells, 1);
        host.Children.Add(labels);
        host.Children.Add(cells);
        return host;
    }

    private static Color CellFill(Color accent, double value, double peak)
    {
        if (value <= 0 || peak <= 0)
        {
            return Tint(Colors.Gray, HeatmapEmptyOpacity);
        }

        var share = value / peak;
        var opacity = share <= HeatmapFloorShare
            ? HeatmapFloorOpacity
            : HeatmapFloorOpacity
                + ((HeatmapPeakOpacity - HeatmapFloorOpacity)
                    * ((share - HeatmapFloorShare) / (1 - HeatmapFloorShare)));
        return Tint(accent, opacity);
    }

    private static UIElement SlotTip(int weekday, int hour, double value, WindowEquivalence.Row? equivalence)
    {
        var panel = new StackPanel { Spacing = 3, MinWidth = 186 };
        panel.Children.Add(TipText(QuotaLensText.SlotHeader(weekday, hour), 11, bold: true));
        if (value <= 0)
        {
            panel.Children.Add(TipText(QuotaLensText.SlotEmpty(), 9, 0.6));
            return panel;
        }

        panel.Children.Add(TipText(QuotaLensText.SlotSpend(value)));
        // Converted from the window's own equivalence rather than measured —
        // see WindowEquivalenceText.Slot's own doc comment for why this reads
        // the pooled history fold rather than scanning messages per slot.
        var lines = WindowEquivalenceText.Slot(value, equivalence);
        // A real figure reads as body copy (Secondary present, the money/error
        // line); the no-figure reason is the sole line and reads as dim, the
        // same tertiary weight QuotaLensText.SlotEmpty above uses.
        var primary = TipText(lines.Primary, 9, lines.Secondary is null ? 0.6 : 1.0);
        primary.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(primary);
        if (lines.Secondary is { } secondary)
        {
            var line = TipText(secondary, 9, 0.6);
            line.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(line);
        }

        return panel;
    }

    private static FrameworkElement HourAxis()
    {
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(HeatmapLabelWidth),
        });
        var axis = new Grid { ColumnSpacing = HeatmapCellGap };
        for (var hour = 0; hour < 24; hour++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        // Axis labels at 0 / 6 / 12 / 18, as macOS draws them.
        int[] marks = [0, 6, 12, 18];
        foreach (var hour in marks)
        {
            var label = Ui.Text(hour.ToString(System.Globalization.CultureInfo.InvariantCulture), 8, 0.45);
            Grid.SetColumn(label, hour);
            axis.Children.Add(label);
        }

        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        Grid.SetColumn(axis, 1);
        host.Children.Add(axis);
        return host;
    }

    /// <summary>The store's own triple, flattened for the one place a window has
    /// to survive as a settings string.</summary>
    private static string WindowId(QuotaWindowIdentity id) =>
        $"{id.ProviderId}|{id.AccountScope}|{id.WindowKey}";

    private static Color Tint(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), color.R, color.G, color.B);

    private static Color AccentColor() =>
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var value)
            && value is Color color
            ? color
            : Color.FromArgb(255, 0x3b, 0x82, 0xf6);
}
