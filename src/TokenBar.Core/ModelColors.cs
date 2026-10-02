using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

// Provider-based model coloring, port of TokenBarCore/ModelColors.swift
// (originally src/lib/modelColors.ts, itself ported from tokscale's TUI).
// Each provider has a base color; models within a provider are ranked and
// tinted toward white by rank. Cost ranking is used only for authoritative data.
// Colors are hex strings so this stays UI-framework-free.

public static class ModelColors
{
    /// <summary>Provider base colors (rank-0 / darkest).</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderBase =
        new Dictionary<string, string>
        {
            ["anthropic"] = "#da7756",
            ["openai"] = "#3b82f6",
            ["google"] = "#06b6d4",
            ["cursor"] = "#a855f7",
            ["deepseek"] = "#4d6bfe",
            ["xai"] = "#1f2937",
            ["meta"] = "#0866ff",
            ["mistral"] = "#fa520f",
            ["unknown"] = "#888888",
        };

    // Tint factors toward white by rank — matches tokscale's shade_from_base.
    private static readonly double[] Factors = [0.0, 0.11, 0.22, 0.33, 0.44, 0.56, 0.67];

    /// <summary>Infer a provider key from a model name, mirroring
    /// get_provider_from_model.</summary>
    public static string ProviderFromModel(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("claude") || m.Contains("sonnet") || m.Contains("opus") || m.Contains("haiku"))
        {
            return "anthropic";
        }

        if (m.Contains("gpt") || m.StartsWith("o1", StringComparison.Ordinal) ||
            m.StartsWith("o3", StringComparison.Ordinal) || m.Contains("codex") ||
            m.Contains("text-embedding") || m.Contains("dall-e") || m.Contains("whisper") ||
            m.Contains("tts"))
        {
            return "openai";
        }

        if (m.Contains("gemini")) { return "google"; }
        if (m.Contains("deepseek")) { return "deepseek"; }
        if (m.Contains("grok")) { return "xai"; }
        if (m.Contains("llama")) { return "meta"; }
        if (m.Contains("mixtral") || m.Contains("mistral")) { return "mistral"; }
        if (m == "auto" || m.Contains("cursor") || m.Contains("composer")) { return "cursor"; }
        return "unknown";
    }

    /// <summary>Resolve the provider color key: fall back to inferring from
    /// the model when the provider id is empty or a merged list
    /// ("litellm, openai").</summary>
    public static string ProviderColorKey(string? providerId, string modelId)
    {
        var p = (providerId ?? "").ToLowerInvariant();
        if (p.Length == 0 || p.Contains(", ")) { return ProviderFromModel(modelId); }
        if (p.Contains("anthropic")) { return "anthropic"; }
        if (p.Contains("openai")) { return "openai"; }
        if (p.Contains("google") || p.Contains("gemini")) { return "google"; }
        if (p.Contains("deepseek")) { return "deepseek"; }
        if (p.Contains("xai") || p.Contains("grok")) { return "xai"; }
        if (p.Contains("meta") || p.Contains("llama")) { return "meta"; }
        if (p.Contains("mistral")) { return "mistral"; }
        if (p.Contains("cursor")) { return "cursor"; }
        if (ProviderBase.ContainsKey(p)) { return p; }
        return ProviderFromModel(modelId);
    }

    /// <summary>Lerp a base hex color toward white by rank.</summary>
    public static string ShadeFromBase(string hex, int rank)
    {
        var baseHex = hex.StartsWith('#') ? hex[1..] : hex;
        if (baseHex.Length != 6 ||
            !int.TryParse(baseHex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !int.TryParse(baseHex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !int.TryParse(baseHex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return hex;
        }

        var f = Factors[Math.Clamp(rank, 0, Factors.Length - 1)];
        // Swift's .rounded() is round-half-away-from-zero; inputs are >= 0 here.
        int Lerp(int c) => (int)Math.Round(c + (255.0 - c) * f, MidpointRounding.AwayFromZero);
        return $"#{Lerp(r):x2}{Lerp(g):x2}{Lerp(b):x2}";
    }
}

/// <summary>(providerId, modelId) → hex color resolver (port of
/// ModelColorMap). Models are cost-ranked within each provider color group;
/// rank drives the shade. Falls back to a rank-0 base shade for models not
/// seen at build time.</summary>
public sealed class ModelColorMap
{
    private readonly Dictionary<string, string> _colorByKeyModel = [];

    public ModelColorMap(
        IEnumerable<(string Provider, string Model, double Cost)> entries,
        bool costAuthoritative = true)
    {
        // provider key → model → summed cost
        var byProvider = new Dictionary<string, Dictionary<string, double>>();
        foreach (var e in entries)
        {
            var key = ModelColors.ProviderColorKey(e.Provider, e.Model);
            var cost = double.IsFinite(e.Cost) ? e.Cost : 0;
            if (!byProvider.TryGetValue(key, out var models))
            {
                models = [];
                byProvider[key] = models;
            }

            // Grouped on both sides so a raw entry and a raw lookup still meet.
            var model = ModelGrouping.GroupId(e.Model);
            models[model] = models.GetValueOrDefault(model) + cost;
        }

        foreach (var (providerKey, models) in byProvider)
        {
            var baseColor = ModelColors.ProviderBase.GetValueOrDefault(
                providerKey, ModelColors.ProviderBase["unknown"]);
            var ranked = costAuthoritative
                ? models
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                : models.OrderBy(kv => kv.Key, StringComparer.Ordinal);
            var rank = 0;
            foreach (var (model, _) in ranked)
            {
                _colorByKeyModel[$"{providerKey} {model}"] = ModelColors.ShadeFromBase(baseColor, rank);
                rank++;
            }
        }
    }

    public ModelColorMap(ModelReport? report, bool costAuthoritative = true)
        : this(
            (report?.Entries ?? []).Select(e => (e.Provider, e.Model, e.Cost)),
            costAuthoritative)
    {
    }

    public string Color(string? providerId, string modelId)
    {
        var key = ModelColors.ProviderColorKey(providerId, modelId);
        // The table is built from model-report rows, which the engine has
        // already grouped, while graph and window callers pass raw ids;
        // looking up the grouped id lets a raw grok-4.6-build find the
        // grok-4.6 shade.
        if (_colorByKeyModel.TryGetValue($"{key} {ModelGrouping.GroupId(modelId)}", out var hit))
        {
            return hit;
        }

        var baseColor = ModelColors.ProviderBase.GetValueOrDefault(
            key, ModelColors.ProviderBase["unknown"]);
        return ModelColors.ShadeFromBase(baseColor, 0);
    }
}
