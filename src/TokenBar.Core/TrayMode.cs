using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>What the tray shows, mirroring the macOS TrayMode (itself a port
/// of the Tauri computeTrayTitle). On Windows there is no menu-bar text, so
/// a non-hidden mode's title is drawn INTO the icon and the pictorial icon
/// styles take over only in Hidden ("icon only") mode.</summary>
public enum TrayMode
{
    TodayTokens,
    TodayCost,
    TotalTokens,
    TotalCost,
    TokensPerMin,
    QuotaLeft,
    Hidden,
}

public static class TrayModes
{
    public const string StorageKey = "tokenbar.tray.mode";

    public static readonly IReadOnlyList<TrayMode> All =
    [
        TrayMode.TodayTokens, TrayMode.TodayCost, TrayMode.TotalTokens,
        TrayMode.TotalCost, TrayMode.TokensPerMin, TrayMode.QuotaLeft,
        TrayMode.Hidden,
    ];

    public static TrayMode Parse(string? raw) => raw switch
    {
        "today_cost" => TrayMode.TodayCost,
        "total_tokens" => TrayMode.TotalTokens,
        "total_cost" => TrayMode.TotalCost,
        "tokens_per_min" => TrayMode.TokensPerMin,
        "quota_left" => TrayMode.QuotaLeft,
        "hidden" => TrayMode.Hidden,
        _ => TrayMode.TodayTokens,
    };

    public static string RawValue(this TrayMode mode) => mode switch
    {
        TrayMode.TodayCost => "today_cost",
        TrayMode.TotalTokens => "total_tokens",
        TrayMode.TotalCost => "total_cost",
        TrayMode.TokensPerMin => "tokens_per_min",
        TrayMode.QuotaLeft => "quota_left",
        TrayMode.Hidden => "hidden",
        _ => "today_tokens",
    };

    /// <summary>Settings-UI copy (macOS TrayMode.label).</summary>
    public static string Label(this TrayMode mode) => mode switch
    {
        TrayMode.TodayTokens => "Today's tokens (50M)".Localized(),
        TrayMode.TodayCost => "Today's cost ($5.20)".Localized(),
        TrayMode.TotalTokens => "Total tokens (1.5B)".Localized(),
        TrayMode.TotalCost => "Total cost ($889)".Localized(),
        TrayMode.TokensPerMin => "Tokens / min (12.4K/m)".Localized(),
        TrayMode.QuotaLeft => "Quota left (57%)".Localized(),
        _ => "Icon only".Localized(),
    };

    /// <summary>Short mode name for the tray tooltip's "layer b" of the
    /// removed-native-text substitution.</summary>
    public static string ShortLabel(this TrayMode mode) => mode switch
    {
        TrayMode.TodayTokens => "Today's tokens".Localized(),
        TrayMode.TodayCost => "Today's cost".Localized(),
        TrayMode.TotalTokens => "Total tokens".Localized(),
        TrayMode.TotalCost => "Total cost".Localized(),
        TrayMode.TokensPerMin => "Tokens / min".Localized(),
        TrayMode.QuotaLeft => "Quota left".Localized(),
        _ => "Syrtis".Localized(),
    };

    /// <summary>The icon-sized short form of a title (parity table #1's
    /// "truncated $5.20"). Money past $100 goes compact ($4637.49 → $4.6K,
    /// $889.13 → $889) so a wide "$" plus four digits don't crush into a
    /// 16px square; smaller amounts keep their cents. Token counts drop the
    /// decimal run once there are two integer digits (12.3K → 12K, 2% error
    /// for a big font win), keeping 1.5B where the fraction is the value.
    /// The tooltip always carries the exact string.</summary>
    public static string IconTitle(string title)
    {
        if (title.StartsWith('$') && double.TryParse(
            title.AsSpan(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var usd))
        {
            return Math.Abs(usd) >= 100
                ? "$" + Format.CompactTokens((long)Math.Round(usd))
                : title;
        }

        var dot = title.IndexOf('.');
        if (dot < 0)
        {
            return title;
        }

        var intDigits = 0;
        for (var i = dot - 1; i >= 0 && char.IsAsciiDigit(title[i]); i--)
        {
            intDigits++;
        }

        if (intDigits < 2)
        {
            return title;
        }

        var end = dot + 1;
        while (end < title.Length && char.IsAsciiDigit(title[end]))
        {
            end++;
        }

        return title[..dot] + title[end..];
    }

    /// <summary>The tray title for this mode ("" = icon only), formatted from
    /// already-filtered usage totals plus the visible live rate. Quota rounding
    /// remains faithful to Swift's away-from-zero behavior.</summary>
    public static string Title(
        this TrayMode mode, TrayTotals? totals, double? tokensPerMin,
        double? quotaRemaining = null)
    {
        if (mode == TrayMode.QuotaLeft)
        {
            return quotaRemaining is { } quota
                ? $"{(int)Math.Round(Math.Clamp(quota, 0, 100), MidpointRounding.AwayFromZero)}%"
                : "—%";
        }

        if (mode == TrayMode.Hidden || totals is null)
        {
            return "";
        }

        return mode switch
        {
            TrayMode.TodayTokens => Format.CompactTokens(totals.Value.TodayTokens),
            TrayMode.TodayCost => Format.Usd(totals.Value.TodayCost),
            TrayMode.TotalTokens => Format.CompactTokens(totals.Value.TotalTokens),
            TrayMode.TotalCost => Format.Usd(totals.Value.TotalCost),
            TrayMode.TokensPerMin => tokensPerMin is { } rate
                ? $"{Format.CompactTokens((long)Math.Round(Math.Max(0, rate), MidpointRounding.AwayFromZero))}/m"
                : "—/m",
            _ => "",
        };
    }
}
