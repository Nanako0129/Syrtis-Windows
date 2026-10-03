using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TokenBar.Core;
using TokenBar.Interop;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

/// <summary>Drag-to-reorder for the multi-client Agent-limits card (macOS
/// <c>AgentLimitsCard</c> :644-707): a grip on each primary card's header, a
/// drop line on the edge the card will land on, and the result written to
/// <see cref="ClientRegistry.TabOrderKey"/>, whose change re-renders the
/// flyout. Extra accounts follow their primary and are never dragged. The
/// order decisions live in <see cref="LimitsCardOrder"/>.</summary>
internal sealed class LimitsDrag
{
    /// <summary>How far the drop line sits outside the card, into the
    /// card stack's 10px gap — macOS offsets its line 6pt into a 12pt gap.</summary>
    private const double DropLineOffset = 5;

    /// <summary>The dragged card's opacity, as on macOS.</summary>
    private const double DraggedOpacity = 0.5;

    /// <summary>The drag under way, if any. The dashboard re-renders on every
    /// model update (the 10 s fast refresh among them) and on keyboard
    /// shortcuts, and rebuilding this panel takes the grip — and its pointer
    /// capture — away mid-drag; macOS keeps its drag in view state that
    /// survives a refresh. DashboardView.RenderContent, which every rebuild
    /// goes through, holds while this is set and catches up on
    /// <see cref="Finished"/>.</summary>
    private static LimitsDrag? _active;

    public static bool InProgress => _active is not null;

    public static event Action? Finished;

    /// <summary>Ends a drag without saving it — for the flyout hiding, where
    /// WinUI is not relied on to report the lost capture.</summary>
    public static void CancelActive() => _active?.End(commit: false);

    private readonly Panel _panel;
    private readonly List<string> _visible;
    private readonly Dictionary<string, (Grid Host, Rectangle Top, Rectangle Bottom)> _cards = [];
    private string? _dragId;
    private string? _overId;

    public LimitsDrag(Panel panel, IReadOnlyList<AgentUsageSnapshot> orderedAgents)
    {
        _panel = panel;
        _visible = [.. orderedAgents.Where(static a => a.Account.AccountKey is null).Select(static a => a.ClientId)];
    }

    /// <summary>Wraps a primary card so it can show a drop line; an extra
    /// account's card is returned as is.</summary>
    public FrameworkElement Host(AgentUsageSnapshot agent, FrameworkElement section)
    {
        if (agent.Account.AccountKey is not null)
        {
            return section;
        }

        var host = new Grid();
        host.Children.Add(section);
        var top = DropLine(VerticalAlignment.Top, new Thickness(0, -DropLineOffset, 0, 0));
        var bottom = DropLine(VerticalAlignment.Bottom, new Thickness(0, 0, 0, -DropLineOffset));
        host.Children.Add(top);
        host.Children.Add(bottom);
        _cards[agent.ClientId] = (host, top, bottom);
        return host;
    }

    public FrameworkElement Grip(string id)
    {
        var grip = Ui.Text("⠿", 11, 0.5);
        grip.VerticalAlignment = VerticalAlignment.Center;
        HoverTip.Attach(grip, () => "Drag to reorder".Localized());
        grip.PointerPressed += (_, e) =>
        {
            // Primary button only, as macOS DragGesture.
            if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed && grip.CapturePointer(e.Pointer))
            {
                _dragId = id;
                _active = this;
                if (_cards.TryGetValue(id, out var card))
                {
                    card.Host.Opacity = DraggedOpacity;
                }
            }

            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragId == id)
            {
                Over(CardAt(e.GetCurrentPoint(_panel).Position.Y));
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            End(commit: true);
            grip.ReleasePointerCapture(e.Pointer);
        };
        grip.PointerCaptureLost += (_, _) => End(commit: false);
        return grip;
    }

    private static Rectangle DropLine(VerticalAlignment edge, Thickness margin) => new()
    {
        Height = 2,
        VerticalAlignment = edge,
        Margin = margin,
        Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private string? CardAt(double y)
    {
        foreach (var (id, card) in _cards)
        {
            var top = card.Host.TransformToVisual(_panel).TransformPoint(default).Y;
            if (y >= top && y < top + card.Host.ActualHeight)
            {
                return id;
            }
        }

        return null;
    }

    private void Over(string? target)
    {
        _overId = target == _dragId ? null : target;
        foreach (var (id, card) in _cards)
        {
            var on = id == _overId;
            var below = on && LimitsCardOrder.DropsBelow(_visible, _dragId!, id);
            card.Top.Visibility = on && !below ? Visibility.Visible : Visibility.Collapsed;
            card.Bottom.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void End(bool commit)
    {
        if (_dragId is not { } from)
        {
            return;
        }

        var to = _overId;
        Over(null);
        _dragId = null;
        foreach (var card in _cards.Values)
        {
            card.Host.Opacity = 1;
        }

        try
        {
            if (commit && to is not null)
            {
                var raw = AppSettings.Store.GetString(ClientRegistry.TabOrderKey) ?? "";
                AppSettings.Store.SetString(
                    ClientRegistry.TabOrderKey, LimitsCardOrder.Dropped(raw, _visible, from, to));
            }
        }
        finally
        {
            // Always, or a throwing Changed subscriber would leave every later
            // render held and the flyout frozen.
            _active = null;
            Finished?.Invoke();
        }
    }
}
