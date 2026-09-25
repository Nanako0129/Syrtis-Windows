namespace TokenBar.Core;

/// <summary>Tray "Font color" setting (macOS MenuBarTextColor/QuotaColorLevel
/// port). Windows has no tray title text, so this colors the value
/// TrayIconRenderer draws into the icon bitmap instead of an NSStatusItem
/// title. Colors stay hex strings, same reason as <see cref="ModelColors"/>:
/// this project has no System.Drawing/WinUI reference, so parsing a hex
/// string into a real color happens at the App layer.</summary>
public enum TrayTextColorMode
{
    Automatic,
    Custom,
}

/// <summary>Which color a drawn value picks up, by its own remaining-quota
/// percentage. Non-quota tray modes and an unavailable quota resolve to
/// <see cref="Normal"/> (mirrors macOS: <c>quotaRemaining.map(...) ?? .normal</c>).</summary>
public enum QuotaColorLevel
{
    Normal,
    Warning,
    Critical,
}

public static class MenuBarTextColor
{
    public const string StorageKey = "tokenbar.tray.textColor.mode";
    public const string CustomColorKey = "tokenbar.tray.textColor.hex";
    public const string WarningColorKey = "tokenbar.tray.textColor.warning.hex";
    public const string CriticalColorKey = "tokenbar.tray.textColor.critical.hex";

    public const string DefaultHex = "#21C55E";
    public const string WarningDefaultHex = "#F59E0B";
    public const string CriticalDefaultHex = "#EF4444";

    /// <summary>16-color preset grid, same values as the macOS popover.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Presets =
    [
        ("Black", "#000000"), ("Dark gray", "#52525B"),
        ("Light gray", "#A1A1AA"), ("White", "#FFFFFF"),
        ("Red", "#EF4444"), ("Orange", "#F97316"),
        ("Amber", "#F59E0B"), ("Yellow", "#FACC15"),
        ("Lime", "#84CC16"), ("Green", "#21C55E"),
        ("Emerald", "#10B981"), ("Cyan", "#06B6D4"),
        ("Blue", "#3B82F6"), ("Indigo", "#6366F1"),
        ("Purple", "#A855F7"), ("Pink", "#EC4899"),
    ];

    public static TrayTextColorMode ParseMode(string? raw) =>
        raw == "custom" ? TrayTextColorMode.Custom : TrayTextColorMode.Automatic;

    public static string RawValue(this TrayTextColorMode mode) =>
        mode == TrayTextColorMode.Custom ? "custom" : "automatic";

    public static string Label(this TrayTextColorMode mode) => mode switch
    {
        TrayTextColorMode.Custom => "Custom".Localized(),
        _ => "Automatic".Localized(),
    };

    /// <summary>Boundaries match macOS QuotaColorLevel exactly: 10 and 25 are
    /// each the top of their own band (≤10 critical, ≤25 warning).</summary>
    public static QuotaColorLevel LevelFor(double? remaining) => remaining switch
    {
        null => QuotaColorLevel.Normal,
        <= 10 => QuotaColorLevel.Critical,
        <= 25 => QuotaColorLevel.Warning,
        _ => QuotaColorLevel.Normal,
    };

    public static string StorageKeyFor(this QuotaColorLevel level) => level switch
    {
        QuotaColorLevel.Warning => WarningColorKey,
        QuotaColorLevel.Critical => CriticalColorKey,
        _ => CustomColorKey,
    };

    public static string DefaultHexFor(this QuotaColorLevel level) => level switch
    {
        QuotaColorLevel.Warning => WarningDefaultHex,
        QuotaColorLevel.Critical => CriticalDefaultHex,
        _ => DefaultHex,
    };

    public static string Label(this QuotaColorLevel level) => level switch
    {
        QuotaColorLevel.Warning => "Low".Localized(),
        QuotaColorLevel.Critical => "Very low".Localized(),
        _ => "Normal".Localized(),
    };

    public static string Hint(this QuotaColorLevel level) => level switch
    {
        QuotaColorLevel.Warning => "More than 10% and up to 25% remaining".Localized(),
        QuotaColorLevel.Critical => "10% or less remaining".Localized(),
        _ => "More than 25% remaining, other text, or unavailable quota".Localized(),
    };

    /// <summary>Validate and canonicalize editable hex text: 6 hex digits,
    /// an optional leading "#", into "#RRGGBB" uppercase. Anything else
    /// (wrong length, non-hex characters) is invalid.</summary>
    public static string? NormalizeHex(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var trimmed = input.Trim();
        var digits = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (digits.Length != 6)
        {
            return null;
        }

        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return "#" + digits.ToUpperInvariant();
    }

    /// <summary>The drawn value's hex color: null means "use the automatic
    /// color" — either the mode is Automatic, or it is Custom with a hex that
    /// no longer normalizes (a hand-edited settings file), which macOS also
    /// falls back from rather than drawing garbage.</summary>
    public static string? Resolve(string? modeRaw, string? levelHex) =>
        ParseMode(modeRaw) == TrayTextColorMode.Custom ? NormalizeHex(levelHex) : null;
}
