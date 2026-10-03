using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using Windows.UI;

namespace TokenBar.App;

/// <summary>
/// Instant, styled hover tooltip — the native ToolTip's fixed ~1s delay and
/// plain chrome are nowhere near the macOS onContinuousHover cards, and WinUI
/// exposes no delay knob. A Popup follows the pointer, placed by
/// <see cref="TooltipPlacement"/> inside the window's registered scroll
/// viewport (macOS PopoverTooltipPlacement). Content is arbitrary UI (the macOS
/// tooltips carry colored discs and metric rows, not just text).
///
/// One host (Popup + Border) is kept PER XamlRoot. A single shared Popup cannot
/// be re-pointed across the flyout's and the settings window's XamlRoots — WinUI
/// forbids reassigning a rooted Popup's XamlRoot, which threw inside the pointer
/// handler and (with no global handler) crashed the app — so each window's root
/// gets its own host.
/// </summary>
public static class HoverTip
{
    private sealed record Host(Popup Popup, Border Card);

    private static readonly Dictionary<XamlRoot, Host> _hosts = [];

    public static void Attach(FrameworkElement target, Func<string> content) =>
        AttachRich(target, () => new TextBlock
        {
            Text = content(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
        });

    public static void AttachRich(FrameworkElement target, Func<UIElement> build)
    {
        target.PointerEntered += (_, e) => Show(target, build(), e);
        target.PointerMoved += (_, e) => Move(target, e);
        target.PointerExited += (_, _) => HideFor(target);
        target.Unloaded += (_, _) => HideFor(target);
    }

    /// <summary>Programmatic path for custom hit-tested surfaces such as the
    /// D3D contribution graph. <paramref name="rootPosition"/> is expressed in
    /// the target's XamlRoot coordinate space.</summary>
    public static void ShowAt(
        FrameworkElement target, UIElement content, Windows.Foundation.Point rootPosition)
    {
        if (target.XamlRoot is not { } root)
        {
            return;
        }

        var host = EnsureHost(root);
        host.Card.Child = content;
        Position(host, target, root, rootPosition);
        host.Popup.IsOpen = true;
    }

    /// <summary>Move an already-open programmatic tooltip without rebuilding
    /// its content. No-op when this XamlRoot has no active hover card.</summary>
    public static bool MoveAt(FrameworkElement target, Windows.Foundation.Point rootPosition)
    {
        if (target.XamlRoot is { } root
            && _hosts.TryGetValue(root, out var host)
            && host.Popup.IsOpen)
        {
            Position(host, target, root, rootPosition);
            return true;
        }

        return false;
    }

    public static void HideFor(FrameworkElement target)
    {
        if (target.XamlRoot is { } root && _hosts.TryGetValue(root, out var host))
        {
            host.Popup.IsOpen = false;
            return;
        }

        // XamlRoot already gone (e.g. Unloaded): close any open tooltip so a
        // stray card can't linger.
        foreach (var h in _hosts.Values)
        {
            h.Popup.IsOpen = false;
        }
    }

    private static Host EnsureHost(XamlRoot root)
    {
        if (_hosts.TryGetValue(root, out var existing))
        {
            return existing;
        }

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 30, 30, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 300,
            IsHitTestVisible = false,
        };
        var host = new Host(
            new Popup
            {
                Child = card,
                IsHitTestVisible = false,
                XamlRoot = root,
            },
            card);
        _hosts[root] = host;
        return host;
    }

    private static void Show(FrameworkElement target, UIElement content, PointerRoutedEventArgs e)
    {
        if (target.XamlRoot is not { } root)
        {
            return;
        }

        ShowAt(target, content, e.GetCurrentPoint(root.Content).Position);
    }

    private static void Move(FrameworkElement target, PointerRoutedEventArgs e)
    {
        if (target.XamlRoot is { } root
            && _hosts.TryGetValue(root, out var host)
            && host.Popup.IsOpen)
        {
            Position(host, target, root, e.GetCurrentPoint(root.Content).Position);
        }
    }

    /// <summary>Per window, the visible scroll area in root coordinates, so a
    /// tooltip stays inside it and off the footer. The flyout registers its
    /// cards scroller; a window that registers nothing places tooltips inside
    /// its whole client area.</summary>
    private static readonly Dictionary<XamlRoot, Func<Box?>> _viewports = [];

    public static void RegisterViewport(XamlRoot root, Func<Box?> viewport) =>
        _viewports[root] = viewport;

    /// <summary><paramref name="element"/>'s bounds in its root's coordinates,
    /// or null when it is not in the tree.</summary>
    public static Box? BoundsInRoot(FrameworkElement element)
    {
        if (element.XamlRoot?.Content is not UIElement rootContent)
        {
            return null;
        }

        try
        {
            var origin = element.TransformToVisual(rootContent).TransformPoint(default);
            return new Box(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
        }
        catch (InvalidOperationException)
        {
            // Detached while a pointer event was queued (Graph3DPanel guards
            // the same transform for the same reason); place against the
            // window instead.
            return null;
        }
    }

    private static void Position(
        Host host, FrameworkElement target, XamlRoot root, Windows.Foundation.Point rootPosition)
    {
        host.Card.Measure(new Windows.Foundation.Size(300, double.PositiveInfinity));
        var size = host.Card.DesiredSize;
        var window = new Box(0, 0, root.Size.Width, root.Size.Height);
        var origin = TooltipPlacement.Origin(
            rootPosition.X, rootPosition.Y, size.Width, size.Height,
            BoundsInRoot(target) ?? window,
            _viewports.TryGetValue(root, out var viewport) ? viewport() : null,
            window);
        if (origin is not { } at)
        {
            return;
        }

        host.Popup.HorizontalOffset = at.X;
        host.Popup.VerticalOffset = at.Y;
    }

    /// <summary>True when <paramref name="popup"/> is one of HoverTip's tooltip
    /// popups, so light-dismiss logic (e.g. the flyout's Esc handler) can ignore
    /// it and only yield to a real transient such as a MenuFlyout.</summary>
    public static bool IsHoverPopup(Popup popup)
    {
        foreach (var host in _hosts.Values)
        {
            if (ReferenceEquals(host.Popup, popup))
            {
                return true;
            }
        }

        return false;
    }
}
