using System.Globalization;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>The Hourly lens's two modes, persisted with macOS's raw values.</summary>
public enum HourlyMode
{
    Timeline,
    Profile,
}

/// <summary>Every decision the Hourly lens makes, after macOS HourlyView.swift:
/// which slots the timeline lists, the 24-hour profile fold, the windowing
/// button and the card's title and subtitle. DashboardView.xaml.cs compiles
/// under no test project, so they live here.</summary>
public static class HourlyLens
{
    /// <summary>macOS <c>@AppStorage("tokenbar.hourly.mode")</c>.</summary>
    public const string ModeKey = "tokenbar.hourly.mode";

    /// <summary>Timeline rows shown at first and added per "Show more"
    /// (HourlyView.swift:30-31).</summary>
    public const int TimelineInitial = 200;
    public const int TimelineStep = 200;

    public static HourlyMode ParseMode(string? raw) =>
        raw == "profile" ? HourlyMode.Profile : HourlyMode.Timeline;

    public static string RawValue(HourlyMode mode) =>
        mode == HourlyMode.Profile ? "profile" : "timeline";

    public static string Title(HourlyMode mode) =>
        (mode == HourlyMode.Profile ? "Hourly rhythm" : "Hourly usage").Localized();

    /// <summary>Slots with any usage, newest first. A slot with neither tokens
    /// nor cost is dropped, as macOS does.</summary>
    public static IReadOnlyList<HourlyReportEntry> Timeline(IEnumerable<HourlyReportEntry> entries) =>
        [.. entries
            .Where(e => e.Total > 0 || e.Cost > 0)
            .OrderByDescending(e => e.Hour, StringComparer.Ordinal)];

    /// <summary>Every slot folded onto its hour of day ("YYYY-MM-DD HH:00" →
    /// HH); a slot whose key carries no valid hour is skipped.</summary>
    public static (long Tokens, double Cost)[] Profile(IEnumerable<HourlyReportEntry> entries)
    {
        var buckets = new (long Tokens, double Cost)[24];
        foreach (var e in entries)
        {
            if (e.Hour.Length >= 13
                && int.TryParse(e.Hour.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                && h is >= 0 and < 24)
            {
                buckets[h] = (buckets[h].Tokens.SaturatingAdd(e.Total), buckets[h].Cost + e.Cost);
            }
        }

        return buckets;
    }

    /// <summary>The windowing button, or null when every row is shown:
    /// "Show {next} more · {shown} of {total}".</summary>
    public static string? ShowMore(int visible, int total) =>
        total > visible
            ? "Show {0} more · {1} of {2}".Localized(
                Math.Min(TimelineStep, total - visible), visible, total)
            : null;

    /// <summary>The card subtitle: "peak HH:00 · $" for the profile, "N hrs · $"
    /// for the timeline, "—" with no usage (HourlyView.swift:142-155).</summary>
    public static string Subtitle(
        HourlyMode mode,
        IReadOnlyList<HourlyReportEntry> timeline,
        (long Tokens, double Cost)[] profile,
        bool authoritative)
    {
        if (!HasData(mode, timeline, profile))
        {
            return "—";
        }

        if (mode == HourlyMode.Profile)
        {
            // The first hour wins a tie, as Swift's max(by:) keeps it.
            var peak = 0;
            for (var h = 1; h < profile.Length; h++)
            {
                if (profile[h].Tokens > profile[peak].Tokens)
                {
                    peak = h;
                }
            }

            return "peak {0}:00 · {1}".Localized(
                peak.ToString("D2", CultureInfo.InvariantCulture),
                CostSurfaceProjection.CostText(profile.Sum(b => b.Cost), authoritative));
        }

        return "{0} hrs · {1}".Localized(
            timeline.Count,
            CostSurfaceProjection.CostText(timeline.Sum(e => e.Cost), authoritative));
    }

    public static bool HasData(
        HourlyMode mode,
        IReadOnlyList<HourlyReportEntry> timeline,
        (long Tokens, double Cost)[] profile) =>
        mode == HourlyMode.Profile
            ? profile.Any(b => b.Tokens > 0 || b.Cost > 0)
            : timeline.Count > 0;
}
