using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Gauge icon styles for the tray (macOS QuotaIconStyle; the
/// cat/parrot animation styles live in the same settings key and arrive
/// with the tray animator).</summary>
internal enum QuotaIconStyle
{
    Bars,
    Ring,
    Popsicle,
}

/// <summary>When the gauge icons pick up the gauge color (macOS
/// IconColoring — the battery-icon "color only when it matters" default).</summary>
internal enum IconColoring
{
    WarningOnly,
    Always,
    Never,
}

/// <summary>
/// GDI+ port of the macOS TrayIcons drawing, coordinate-for-coordinate: the
/// routines draw on the same bottom-left-origin 16-grid via a flipped
/// transform, rendered at 2x (32px) so the shell's 100–200% DPI scaling
/// stays crisp. Windows has no tray text, so non-hidden modes render their
/// short value INTO the icon instead (parity table #1).
/// </summary>
internal static class TrayIconRenderer
{
    private const int Scale = 2;
    private const int Size = 16 * Scale;

    public static Color GaugeColor(double remaining) =>
        remaining <= 10 ? Color.FromArgb(240, 69, 69)
        : remaining <= 25 ? Color.FromArgb(245, 158, 10)
        : Color.FromArgb(33, 196, 94);

    public static IconColoring ParseColoring(string? raw) => raw switch
    {
        "always" => IconColoring.Always,
        "never" => IconColoring.Never,
        _ => IconColoring.WarningOnly,
    };

    public static QuotaIconStyle? ParseGaugeStyle(string? raw) => raw switch
    {
        "bars" => QuotaIconStyle.Bars,
        "ring" => QuotaIconStyle.Ring,
        "popsicle" => QuotaIconStyle.Popsicle,
        _ => null, // cat/parrot (animator) or unknown
    };

    /// <summary>Parse a MenuBarTextColor-normalized "#RRGGBB" hex string
    /// (TokenBar.Core has no System.Drawing reference, so colors cross that
    /// boundary as strings). Null on anything that is not exactly that shape
    /// — callers fall back to the automatic color, same as macOS's
    /// <c>Color(hex:)</c> failing to construct.</summary>
    public static Color? ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return null;
        }

        try
        {
            return Color.FromArgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The color a drawn value should ink with, folding the "Font
    /// color" setting over an already-decided automatic color: Automatic
    /// mode (or a Custom mode with an invalid stored hex) keeps
    /// <paramref name="automatic"/> unchanged. <paramref name="levelRemaining"/>
    /// is the remaining-quota percentage that decides the level, or null for
    /// a non-quota mode / unavailable quota — both resolve to the Normal
    /// color, matching macOS's <c>mode == .quotaLeft ? quotaRemaining : nil</c>.</summary>
    public static Color? ResolveInk(SettingsStore store, Color? automatic, double? levelRemaining)
    {
        var level = MenuBarTextColor.LevelFor(levelRemaining);
        var levelHex = store.GetString(level.StorageKeyFor(), level.DefaultHexFor())
            ?? level.DefaultHexFor();
        var modeRaw = store.GetString(MenuBarTextColor.StorageKey);
        return MenuBarTextColor.Resolve(modeRaw, levelHex) is { } hex
            ? ParseHex(hex) ?? automatic
            : automatic;
    }

    private static Color Ink(double? remaining, bool dark, IconColoring coloring)
    {
        var mono = dark ? Color.White : Color.Black;
        if (remaining is not { } r)
        {
            return mono;
        }

        return coloring switch
        {
            IconColoring.Never => mono,
            IconColoring.Always => GaugeColor(r),
            _ => r <= 25 ? GaugeColor(r) : mono,
        };
    }

    /// <summary>A pictorial gauge icon (Hidden mode's "icon only").</summary>
    public static Bitmap RenderGauge(
        QuotaIconStyle style, double? remaining, bool dark, IconColoring coloring)
    {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The macOS routines assume a bottom-left origin; flip once so the
        // geometry ports verbatim.
        g.TranslateTransform(0, Size);
        g.ScaleTransform(Scale, -Scale);
        var mono = dark ? Color.White : Color.Black;
        var fill = Ink(remaining, dark, coloring);
        var level = remaining ?? 100; // no data draws full, macOS TrayIcons.image
        switch (style)
        {
            case QuotaIconStyle.Bars:
                DrawBars(g, level, mono, fill);
                break;
            case QuotaIconStyle.Ring:
                DrawRing(g, level, mono, fill);
                break;
            default:
                DrawPopsicle(g, level, mono, fill);
                break;
        }

        return bmp;
    }

    /// <summary>The mode's short value drawn as the icon itself (57%, 12.3K,
    /// $5.20) — the CPU-meter convention the parity table picked, since the
    /// notification area has no text. A trailing unit wraps to its own line
    /// so the digits get the full width, and typographic measurement drops
    /// DrawString's side bearings; both buy the font size the 16px square
    /// desperately needs.</summary>
    public static Bitmap RenderTitle(string title, Color? color, bool dark)
    {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ink = color ?? (dark ? Color.White : Color.Black);
        using var brush = new SolidBrush(ink);
        var lines = SplitForIcon(title);
        const float pad = 0.5f;
        if (lines.Length == 1)
        {
            DrawFitted(g, lines[0], brush,
                new RectangleF(pad, pad, Size - 2 * pad, Size - 2 * pad));
        }
        else
        {
            // The digits own the top ~64%; the "$"/unit line takes the rest
            // (they overlap a hair so there's no dead gap between them).
            var bodyH = Size * 0.64f;
            DrawFitted(g, lines[0], brush,
                new RectangleF(pad, 0, Size - 2 * pad, bodyH));
            DrawFitted(g, lines[1], brush,
                new RectangleF(pad, bodyH - 1, Size - 2 * pad, Size - bodyH + 1 - pad));
        }

        return bmp;
    }

    /// <summary>Draw text as large as a box allows: convert to a path,
    /// measure the true glyph bounds (no line-gap/descent slack that
    /// GetHeight would waste), and scale it to fill.</summary>
    private static void DrawFitted(Graphics g, string text, Brush brush, RectangleF box)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        // A large nominal em; the path is scaled to the box afterward, so
        // the absolute size only sets measurement precision.
        path.AddString(text, family, (int)FontStyle.Bold, 100f,
            PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        using var m = new Matrix();
        m.Translate(
            box.X + (box.Width - bounds.Width * scale) / 2,
            box.Y + (box.Height - bounds.Height * scale) / 2);
        m.Scale(scale, scale);
        m.Translate(-bounds.X, -bounds.Y);
        path.Transform(m);
        g.FillPath(brush, path);
    }

    /// <summary>Give the digits a whole line to themselves: the "$" prefix
    /// and any trailing unit (K/M/B/%, /m) drop to a second line, where
    /// small text still reads. "$889" becomes 889 over $; "$4.6K" becomes
    /// 4.6 over $K; "12K" becomes 12 over K. A pure number stays one line.</summary>
    private static string[] SplitForIcon(string title)
    {
        if (title.Length < 3)
        {
            return [title];
        }

        var prefix = "";
        var body = title;
        if (body.StartsWith('$'))
        {
            prefix = "$";
            body = body[1..];
        }

        // Trailing run of non-digits (unit letters, %, /m).
        var suffixLen = 0;
        while (suffixLen < body.Length && !char.IsAsciiDigit(body[^(suffixLen + 1)]))
        {
            suffixLen++;
        }

        var suffix = body[(body.Length - suffixLen)..];
        body = body[..(body.Length - suffixLen)];
        if (body.Length == 0 || (prefix.Length == 0 && suffix.Length == 0))
        {
            return [title]; // no digits to isolate, or nothing to peel off
        }

        return [body, prefix + suffix];
    }

    // ── macOS TrayIcons geometry, 16-grid ───────────────────────────────

    private static void DrawBars(Graphics g, double remaining, Color mono, Color fill)
    {
        double[] heights = [5, 8, 11, 14];
        var fillX = 16.0 * Math.Clamp(remaining, 0, 100) / 100;
        for (var i = 0; i < heights.Length; i++)
        {
            var x = i * 4.0;
            var h = heights[i];
            using var path = RoundedRect(x, 1, 3, h, 1.2);
            using (var outline = new SolidBrush(Color.FromArgb(77, mono)))
            {
                g.FillPath(outline, path);
            }

            var visible = Math.Max(0, Math.Min(3, fillX - x));
            if (visible <= 0)
            {
                continue;
            }

            var state = g.Save();
            g.SetClip(new RectangleF((float)x, 1f, (float)visible, (float)h));
            using (var ink = new SolidBrush(fill))
            {
                g.FillPath(ink, path);
            }

            g.Restore(state);
        }
    }

    private static void DrawRing(Graphics g, double remaining, Color mono, Color fill)
    {
        var box = new RectangleF(8f - 5.6f, 8f - 5.6f, 11.2f, 11.2f);
        using (var track = new Pen(Color.FromArgb(71, mono), 2.6f))
        {
            g.DrawEllipse(track, box);
        }

        var sweep = 360.0 * Math.Clamp(remaining, 0, 100) / 100;
        if (sweep <= 1)
        {
            return;
        }

        using var arc = new Pen(fill, 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        // Flipped space: a negative sweep from 90° matches the macOS
        // clockwise-from-noon arc.
        g.DrawArc(arc, box, 90f, (float)-sweep);
    }

    private static void DrawPopsicle(Graphics g, double remaining, Color mono, Color ice)
    {
        var r = Math.Clamp(remaining, 0, 100) / 100;
        var melt = 1 - r;

        // stick (same ink, lighter layer)
        using (var stickBrush = new SolidBrush(Color.FromArgb(140, mono)))
        using (var stick = RoundedRect(7.25, 1.5, 1.5, 10.5, 0.75))
        {
            g.FillPath(stickBrush, stick);
        }

        // runoff puddle
        if (melt > 0.3)
        {
            var pw = (float)(3 + 9 * (melt - 0.3) / 0.7);
            using var puddle = new SolidBrush(Color.FromArgb(89, ice));
            g.FillEllipse(puddle, 8f - pw / 2, 0.5f, pw, 1.4f);
        }

        if (remaining <= 3)
        {
            return;
        }

        var topY = 5.0 + 10.0 * r;
        var w = 2.6 + 5.4 * r;
        var halfW = w / 2;
        var capR = Math.Min(1.8, (topY - 5.0) * 0.32);
        var dip = 0.5 + 0.9 * melt;
        var shoulder = 0.12 * melt;

        using var body = new GraphicsPath();
        body.AddBezier(
            P(8 - halfW, 5.6),
            P(8 - halfW * 0.5, 5.6 - dip),
            P(8 + halfW * 0.5, 5.6 - dip),
            P(8 + halfW, 5.6));
        body.AddBezier(
            P(8 + halfW, 5.6),
            P(8 + halfW + 0.15, 5.6 + (topY - 5.6) * 0.5),
            P(8 + halfW - shoulder + 0.1, topY - capR),
            P(8 + halfW - shoulder - capR * 0.3, topY - capR * 0.2));
        body.AddBezier(
            P(8 + halfW - shoulder - capR * 0.3, topY - capR * 0.2),
            P(8 + halfW * 0.45, topY + capR * 0.3 - 0.25 * melt),
            P(8 - halfW * 0.45, topY + capR * 0.3 - 0.25 * melt),
            P(8 - halfW + shoulder + capR * 0.3, topY - capR * 0.2));
        body.AddBezier(
            P(8 - halfW + shoulder + capR * 0.3, topY - capR * 0.2),
            P(8 - halfW + shoulder - 0.1, topY - capR),
            P(8 - halfW - 0.15, 5.6 + (topY - 5.6) * 0.5),
            P(8 - halfW, 5.6));
        body.CloseFigure();
        using (var iceBrush = new SolidBrush(ice))
        {
            g.FillPath(iceBrush, body);
        }

        // two classic face grooves; macOS punches them out with
        // destinationOut — GDI+ approximates by repainting the groove at the
        // complementary alpha, which reads the same at 16px.
        if (remaining > 45)
        {
            var presence = Math.Min(1, (r - 0.45) / 0.4);
            var inset = w / 3.2;
            var gBottom = 6.6;
            var gTop = topY - capR - 0.9;
            if (gTop > gBottom + 0.8)
            {
                var state = g.Save();
                // SourceCopy replaces coverage-scaled pixels outright, so an
                // antialiased edge of this ~1px slot would end far more
                // transparent than macOS's destinationOut; crisp edges keep
                // the groove width honest.
                g.SmoothingMode = SmoothingMode.None;
                g.CompositingMode = CompositingMode.SourceCopy;
                var alpha = (int)(255 * (1 - 0.85 * presence));
                using var groove = new SolidBrush(Color.FromArgb(alpha, ice));
                foreach (var gx in new[] { 8 - inset, 8 + inset })
                {
                    using var slot = RoundedRect(gx - 0.33, gBottom, 0.66, gTop - gBottom, 0.33);
                    g.FillPath(groove, slot);
                }

                g.Restore(state);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingMode = CompositingMode.SourceOver;
            }
        }

        // hanging drip + falling droplet
        if (melt > 0.15)
        {
            using var drip = new SolidBrush(Color.FromArgb(178, ice));
            g.FillEllipse(drip, 8.5f, (float)(5.6 - dip - 1.5), 1.0f, 1.5f);
        }

        if (melt > 0.5)
        {
            using var droplet = new SolidBrush(Color.FromArgb(115, ice));
            g.FillEllipse(droplet, 6.7f, 2.8f, 0.85f, 1.1f);
        }
    }

    private static PointF P(double x, double y) => new((float)x, (float)y);

    private static GraphicsPath RoundedRect(double x, double y, double w, double h, double r)
    {
        var path = new GraphicsPath();
        var d = (float)(r * 2);
        var rect = new RectangleF((float)x, (float)y, (float)w, (float)h);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.CloseFigure();
        return path;
    }
}
