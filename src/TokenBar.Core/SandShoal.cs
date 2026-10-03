using System.Diagnostics.CodeAnalysis;

namespace TokenBar.Core;

/// <summary>The "Sand shoal" tray style. Port of macOS TrayAnimator.swift
/// "MARK: Sand" (:440-520). Usage shows as how much sand falls, not as how
/// fast the loop plays: each level is its own frame set, all drawn for
/// <see cref="Fps"/>, and the level is picked from the pace-scaled tokens/min.</summary>
public static class SandShoal
{
    /// <summary>Same settings value as macOS (<c>sandStyle</c>).</summary>
    public const string Style = "sand";

    public const int Levels = 4;

    /// <summary>The rate the frames are drawn for (<c>sandFPS</c>); every
    /// level plays at this rate, whatever the usage.</summary>
    public const double Fps = 24.0;

    /// <summary>Level thresholds in tokens/min, applied to the pace-scaled
    /// rate: idle below 50K, then 300K and 1.5M (<c>sandThresholds</c>).</summary>
    public static readonly IReadOnlyList<double> Thresholds = [50_000, 300_000, 1_500_000];

    /// <summary>A level must be passed by this factor before it changes
    /// (<c>sandHysteresis</c>), so a rate hovering at a threshold does not
    /// swap 144-frame sets every poll.</summary>
    public const double Hysteresis = 1.2;

    public static string Label => "Sand shoal".Localized();

    /// <summary>Every style that animates (the others are gauges): macOS
    /// <c>animatedStyles</c>. One list for the tray and Settings. True implies a
    /// non-null style, which callers that pass it on rely on.</summary>
    public static bool IsAnimated([NotNullWhen(true)] string? style) =>
        style is "cat" or "parrot" or Style;

    public static TimeSpan FrameInterval => TimeSpan.FromSeconds(1.0 / Fps);

    public static int Level(double tokensPerMinute) =>
        Thresholds.Count(t => tokensPerMinute >= t);

    /// <summary>macOS <c>sandLevel(tokensPerMinute:current:)</c>: moving up
    /// needs the rate to still clear the higher level after dividing by
    /// <see cref="Hysteresis"/>; moving down needs it below after multiplying.</summary>
    public static int Level(double tokensPerMinute, int? current)
    {
        var raw = Level(tokensPerMinute);
        if (current is not { } now || raw == now)
        {
            return raw;
        }

        return raw > now
            ? Math.Max(now, Level(tokensPerMinute / Hysteresis))
            : Math.Min(now, Level(tokensPerMinute * Hysteresis));
    }

    /// <summary>Frame-set key, "sand&lt;level&gt;|dark|light" (the animator's
    /// "&lt;style&gt;|&lt;theme&gt;" with the level folded into the style).</summary>
    public static string FrameKey(int level, bool dark) =>
        $"{Style}{level}|{(dark ? "dark" : "light")}";

    /// <summary>The Assets directory for a frame-set style such as "sand2",
    /// or null for any other style.</summary>
    public static string? AssetDirectory(string style, bool dark) =>
        style.Length == Style.Length + 1 && style.StartsWith(Style, StringComparison.Ordinal)
            && style[^1] - '0' is >= 0 and < Levels
            ? $"anim-{style}{(dark ? "" : "-light")}"
            : null;

    /// <summary>The eight frame sets to load now: all of them the first time
    /// the sand style is used, none for any other style or once loaded
    /// (macOS <c>loadSandFramesIfNeeded</c>; 1,152 frames are wasted on
    /// everyone who never picks it).</summary>
    public static IReadOnlyList<string> SetsToLoad(string? style, bool alreadyLoaded) =>
        style != Style || alreadyLoaded
            ? []
            : Enumerable.Range(0, Levels)
                .SelectMany(l => new[] { FrameKey(l, true), FrameKey(l, false) })
                .ToList();
}
