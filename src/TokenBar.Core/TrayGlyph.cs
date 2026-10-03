namespace TokenBar.Core;

/// <summary>What a quota gauge icon draws.</summary>
public enum GaugeAppearance
{
    /// <summary>A live reading under the coloring policy.</summary>
    Live,

    /// <summary>A reading older than <see cref="QuotaStaleness.StaleAfter"/>,
    /// in the opaque stale grey.</summary>
    Stale,

    /// <summary>No reading: the full shape faded, with a "no signal"
    /// slash.</summary>
    NoReading,
}

/// <summary>The tray glyph decisions the renderer draws from, kept here so
/// they can be asserted (the GDI+ renderer compiles under no test project).
/// Port of macOS TrayIcons.swift:85-145 (#419) and MenuBarTextColor.swift
/// <c>applyingStale</c> (#420).</summary>
public static class TrayGlyph
{
    /// <summary>macOS <c>noReadingAlpha</c>: the faded shape's opacity.</summary>
    public const double NoReadingAlpha = 0.3;

    /// <summary>macOS <c>noReadingSlashWidth</c>, on the 16-point grid.</summary>
    public const double NoReadingSlashWidth = 1.5;

    /// <summary>macOS <c>noReadingSlashGap</c>: the wider band cut out of
    /// the faded shape around the slash.</summary>
    public const double NoReadingSlashGap = 3.4;

    /// <summary>The slash's opacity over the cut-out band.</summary>
    public const double NoReadingSlashAlpha = 0.9;

    /// <summary>Slash endpoints on the bottom-left-origin 16-grid
    /// (TrayIcons.swift:145-146).</summary>
    public static readonly (double X, double Y) NoReadingSlashFrom = (2.5, 13.5);
    public static readonly (double X, double Y) NoReadingSlashTo = (13.5, 2.5);

    /// <summary>macOS <c>TrayIcons.image</c>: with no reading the
    /// no-reading glyph is drawn whatever the stamp says (there is nothing to
    /// be stale); a reading is grey once stale, live otherwise. Before #419 a
    /// missing reading drew a full gauge, which is what a full allowance
    /// looks like.</summary>
    public static GaugeAppearance Gauge(double? remaining, bool stale) =>
        remaining is null ? GaugeAppearance.NoReading
        : stale ? GaugeAppearance.Stale
        : GaugeAppearance.Live;

    /// <summary>macOS <c>MenuBarTextColor.applyingStale</c>, called with
    /// <c>stale: mode == .quotaLeft &amp;&amp; readingIsStaleNow</c>
    /// (AppDelegate.swift:517): only a quota-left value with a reading takes
    /// the stale grey; every other value keeps its automatic or custom
    /// colour, since only a quota value is a verdict that can go stale.</summary>
    public static bool TitleIsStale(TrayMode mode, double? remaining, bool stale) =>
        mode == TrayMode.QuotaLeft && stale && remaining is not null;
}
