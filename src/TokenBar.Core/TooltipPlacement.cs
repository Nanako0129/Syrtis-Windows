namespace TokenBar.Core;

/// <summary>An axis-aligned rectangle in one coordinate space (the hover
/// tooltip works in its window's root coordinates).</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Box Inset(double d) => new(X + d, Y + d, Width - 2 * d, Height - 2 * d);

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>Where a hover tooltip goes, ported from macOS
/// <c>PopoverTooltipPlacement.offset</c> (Views/Cards.swift:15-118): centred on
/// the pointer and clamped inside the hovered element and the visible scroll
/// viewport, then above or below the pointer by region, so it dodges the
/// pointer and never sits under the footer.</summary>
public static class TooltipPlacement
{
    /// <summary>Kept clear of the viewport's edges (macOS edgeInset).</summary>
    public const double EdgeInset = 4;

    /// <summary>Between the pointer and the tooltip (macOS cursorGap).</summary>
    public const double CursorGap = 12;

    /// <summary>Above this fraction of the hovered element's height the tooltip
    /// prefers to sit above the pointer, below it under (macOS preferAboveRatio,
    /// the chart's old 0.45 dodge).</summary>
    public const double PreferAboveRatio = 0.45;

    /// <summary>The tooltip's top-left corner, or null when it cannot be placed.
    /// <paramref name="viewport"/> is the visible scroll area. When it is null
    /// or does not contain the pointer (a stale pre-resize frame, or a pointer
    /// over chrome outside the scroll area), <paramref name="window"/> stands
    /// in. macOS falls back to the hovered element here instead; on Windows the
    /// same placement also serves small chrome targets such as tab buttons,
    /// where a tooltip clamped inside the button would cover it.</summary>
    public static (double X, double Y)? Origin(
        double pointerX, double pointerY,
        double tipWidth, double tipHeight,
        Box container, Box? viewport, Box window)
    {
        if (tipWidth <= 0 || tipHeight <= 0)
        {
            return null;
        }

        // Tiny slack for float edges, as macOS.
        const double slack = 2;
        var area = viewport is { IsEmpty: false } v && v.Inset(-slack).Contains(pointerX, pointerY)
            ? v
            : window;
        if (area.IsEmpty)
        {
            return null;
        }

        var visible = area.Inset(EdgeInset);
        if (visible.IsEmpty)
        {
            return null;
        }

        // Horizontally within the hovered element and the visible area; an
        // element narrower than the tooltip (or outside the area) leaves just
        // the area.
        var horizontalMin = Math.Max(container.X, visible.X);
        var horizontalMax = Math.Min(container.Right, visible.Right);
        if (horizontalMax - horizontalMin < tipWidth)
        {
            (horizontalMin, horizontalMax) = (visible.X, visible.Right);
        }

        var maxX = horizontalMax - tipWidth;
        var originX = maxX >= horizontalMin
            ? Math.Min(Math.Max(pointerX - tipWidth / 2, horizontalMin), maxX)
            : horizontalMin;

        var minY = visible.Y;
        var maxY = visible.Bottom - tipHeight;
        var belowY = pointerY + CursorGap;
        var aboveY = pointerY - tipHeight - CursorGap;
        // The region dodge reads the hovered element, not the whole viewport:
        // "below while the viewport has room" would always win on a tall
        // flyout and feel like a sticky follow.
        var preferBelow = container.Height > 0
            ? pointerY - container.Y < container.Height * PreferAboveRatio
            : true;
        double originY;
        if (tipHeight >= visible.Height)
        {
            originY = minY;
        }
        else if (preferBelow)
        {
            originY = belowY <= maxY ? Math.Max(belowY, minY)
                : aboveY >= minY ? Math.Min(aboveY, maxY)
                : Clamped(belowY, aboveY, pointerY, minY, maxY, visible);
        }
        else
        {
            originY = aboveY >= minY ? Math.Min(aboveY, maxY)
                : belowY <= maxY ? Math.Max(belowY, minY)
                : Clamped(belowY, aboveY, pointerY, minY, maxY, visible);
        }

        return (originX, originY);
    }

    private static double Clamped(
        double belowY, double aboveY, double pointerY, double minY, double maxY, Box visible)
    {
        var preferred = visible.Bottom - pointerY >= pointerY - visible.Y ? belowY : aboveY;
        return Math.Min(Math.Max(preferred, minY), maxY);
    }
}
