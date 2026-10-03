namespace TokenBar.Core;

/// <summary>How much token traffic the animated tray icons (cat, parrot) are
/// scaled for. Port of macOS AnimationPace.swift:14-61: the live rate is
/// divided by <see cref="Multiplier"/> before it reaches
/// <see cref="TrayAnimationSpeed"/>, so one curve serves a light user and a
/// user running many agents at once. The plan names in
/// <see cref="Detail"/> are examples only; nothing reads a subscription.</summary>
public enum AnimationPace
{
    Light,
    Moderate,
    Heavy,
}

public static class AnimationPaces
{
    /// <summary>Same key and raw values as macOS
    /// (<c>AnimationPace.storageKey</c>, <c>rawValue</c>).</summary>
    public const string StorageKey = "tokenbar.tray.animationPace";

    public const AnimationPace Default = AnimationPace.Moderate;

    public static readonly IReadOnlyList<AnimationPace> All =
        [AnimationPace.Light, AnimationPace.Moderate, AnimationPace.Heavy];

    public static AnimationPace Parse(string? raw) => raw switch
    {
        "light" => AnimationPace.Light,
        "moderate" => AnimationPace.Moderate,
        "heavy" => AnimationPace.Heavy,
        _ => Default,
    };

    public static AnimationPace Current(SettingsStore store) =>
        Parse(store.GetString(StorageKey));

    public static string RawValue(this AnimationPace pace) => pace switch
    {
        AnimationPace.Light => "light",
        AnimationPace.Heavy => "heavy",
        _ => "moderate",
    };

    /// <summary>macOS <c>multiplier</c>: Light is Claude Pro's fifth of
    /// Max 5x, Heavy is Max 20x's 16.67/5 of it.</summary>
    public static double Multiplier(this AnimationPace pace) => pace switch
    {
        AnimationPace.Light => 0.2,
        AnimationPace.Heavy => 16.67 / 5,
        _ => 1,
    };

    /// <summary>The rate the speed curve sees.</summary>
    public static double Scaled(this AnimationPace pace, double tokensPerMinute) =>
        tokensPerMinute / pace.Multiplier();

    /// <summary>Own keys, as macOS: bare "Light" is an appearance word in
    /// other strings, and a shared key takes the other translation.</summary>
    public static string Label(this AnimationPace pace) => pace switch
    {
        AnimationPace.Light => "Light pace".Localized(),
        AnimationPace.Heavy => "Heavy pace".Localized(),
        _ => "Moderate pace".Localized(),
    };

    public static string Detail(this AnimationPace pace) => pace switch
    {
        AnimationPace.Light => "Occasional use, or a plan like Claude Pro".Localized(),
        AnimationPace.Heavy =>
            "Lots of agents at once, such as Claude Code's ultracode, or a plan like Claude Max 20x"
                .Localized(),
        _ => "A few agents at once, or a plan like Claude Max 5x".Localized(),
    };
}

/// <summary>Frame interval for the animated tray icons. Port of macOS
/// TrayAnimator.swift:408-433 (<c>animationLoad</c> and
/// <c>animationIntervalMilliseconds</c>): idle below 50K tokens/min, top
/// speed at 3M, and a log-scale ramp between so every decade of rate changes
/// the speed by the same factor.</summary>
public static class TrayAnimationSpeed
{
    public const double FloorTokensPerMinute = 50_000;
    public const double CapTokensPerMinute = 3_000_000;
    public const double IdleFps = 2;
    public const double TopFps = 40;

    /// <summary>macOS <c>animationLoad</c>: the rate clamped to [0, cap],
    /// in units of 10K tokens/min.</summary>
    public static double Load(double tokensPerMinute) =>
        Math.Min(Math.Max(0, tokensPerMinute), CapTokensPerMinute) / 10_000.0;

    /// <summary>macOS <c>animationIntervalMilliseconds(load:)</c>, including
    /// its truncation to whole milliseconds.</summary>
    public static int IntervalMilliseconds(double load)
    {
        var tokensPerMinute = load * 10_000.0;
        if (!(tokensPerMinute > FloorTokensPerMinute))
        {
            return (int)(1000.0 / IdleFps);
        }

        var span = Math.Log(CapTokensPerMinute / FloorTokensPerMinute);
        var t = Math.Min(1, Math.Log(tokensPerMinute / FloorTokensPerMinute) / span);
        var fps = IdleFps * Math.Pow(TopFps / IdleFps, t);
        return (int)(1000.0 / fps);
    }

    /// <summary>The interval for a raw live rate under a pace: what the
    /// animator applies every frame.</summary>
    public static int IntervalMilliseconds(double? tokensPerMinute, AnimationPace pace) =>
        IntervalMilliseconds(Load(pace.Scaled(tokensPerMinute ?? 0)));
}
