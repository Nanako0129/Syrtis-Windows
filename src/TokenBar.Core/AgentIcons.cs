namespace TokenBar.Core;

/// <summary>Whether a client's brand icon is a single-color glyph that gets
/// tinted white over a brand-colour disc ("mono"), or a full-colour mark that
/// fills the disc as-is ("full").</summary>
public enum AgentIconKind
{
    Mono,
    Full,
}

/// <summary>Resolved icon for one client id: which asset id to load, how to
/// render it, and its two brand-specific adjustments (backdrop colour for a
/// full mark with no opaque background of its own, and an inset scale for a
/// mark whose art reaches the edge of its square canvas).</summary>
public readonly record struct AgentIconInfo(
    string IconId, AgentIconKind Kind, string? BackgroundHex, double InsetScale);

/// <summary>Client id → brand-icon resolution, ported from macOS
/// AgentIconView.swift's monoIds/fullIds/iconAliases/backgroundFills/
/// insetScale tables. Pure data and lookup only: asset loading and rendering
/// belong to the WinUI layer (TokenBar.App), which has no counterpart here.
/// </summary>
public static class AgentIcons
{
    // AgentIconView.swift:14-16 — glyphs tinted white over the brand disc.
    private static readonly HashSet<string> MonoIds =
        ["claude", "gemini", "opencode", "copilot", "qwen"];

    // AgentIconView.swift:17-24 — full-colour marks that fill the disc as-is.
    private static readonly HashSet<string> FullIds =
    [
        "codex", "droid", "kilocode", "synthetic", "codebuff",
        "antigravity", "kiro", "cursor", "warp", "amp", "pi", "kimi",
        "cline", "jcode", "micode", "gjc", "grok",
        "hermes", "roocode", "mux", "crush", "goose", "zed", "trae", "openclaw",
    ];

    // AgentIconView.swift:29-33 — clients that share another client's icon.
    private static readonly Dictionary<string, string> IconAliases = new()
    {
        ["antigravity-cli"] = "antigravity",
        ["kilo"] = "kilocode",
        ["grok-bot"] = "grok",
    };

    // AgentIconView.swift:40-45 — full marks with no opaque background of
    // their own, backed by a solid disc in the colour the brand mark expects.
    private static readonly Dictionary<string, string> BackgroundFills = new()
    {
        ["cline"] = "#ffffff",
        ["hermes"] = "#ffffff",
        ["mux"] = "#000000",
        ["amp"] = "#000000",
    };

    // AgentIconView.swift:50-52 — full marks whose art reaches the edge of
    // its square canvas and would be clipped by the circular mask at 100%.
    private static readonly Dictionary<string, double> InsetScales = new()
    {
        ["cline"] = 0.82,
    };

    /// <summary>Every asset id that should have a shipped icon file
    /// (agent-icons/&lt;id&gt;.svg or .png), for asset-existence tests.</summary>
    public static IReadOnlyCollection<string> AssetIds => [.. MonoIds, .. FullIds];

    /// <summary>Resolve the id whose agent-icons/&lt;id&gt; asset should
    /// render for a client (AgentIconView.swift:55-57).</summary>
    public static string ResolveIconId(string clientId) =>
        IconAliases.GetValueOrDefault(clientId, clientId);

    /// <summary>Resolve how (and whether) a client renders a brand icon.
    /// Null means the client has no brand icon; the caller falls back to
    /// its plain brand-colour disc.</summary>
    public static AgentIconInfo? Resolve(string clientId)
    {
        var iconId = ResolveIconId(clientId);
        if (FullIds.Contains(iconId))
        {
            return new AgentIconInfo(
                iconId, AgentIconKind.Full,
                BackgroundFills.GetValueOrDefault(iconId),
                InsetScales.GetValueOrDefault(iconId, 1.0));
        }

        if (MonoIds.Contains(iconId))
        {
            return new AgentIconInfo(iconId, AgentIconKind.Mono, null, 1.0);
        }

        return null;
    }
}
