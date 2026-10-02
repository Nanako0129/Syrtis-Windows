namespace TokenBar.Core;

/// <summary>Display-grouping identity for model ids that arrive raw. Port of
/// macOS TokenBarCore/ModelGrouping.swift (TokenBar df7f683f, 667e7fc4).
///
/// The engine's model, monthly and hourly reports already group through
/// <c>normalize_model_for_grouping</c>, but the graph payload keys each row by
/// the raw <c>canonical_model_id</c>, so surfaces that group graph rows by
/// model apply the same fold themselves or they disagree with the Models view.
/// This mirrors the engine's one built-in rule (<c>builtin_grouping</c> in
/// <c>vendor/tokscale-core/src/model_alias.rs</c>): Grok Build keys turn usage
/// by <c>grok-&lt;version&gt;-build</c> while the session names
/// <c>grok-&lt;version&gt;</c>.
///
/// Presentation only. Anything that matches a model id — quota scopes and
/// attribution records — keeps the raw id. <c>ModelColorMap</c> groups on
/// both sides, construction and lookup, so a raw entry and a raw lookup still
/// meet. <c>Fixtures/model-grouping-cases.json</c> (shared with macOS) is
/// checked against this function and against the engine (tb_core_ffi test).
/// That binds the two on those inputs only: an engine rule that widens beyond
/// the table, or a user alias map once the app installs one, would not be
/// caught by it.</summary>
public static class ModelGrouping
{
    /// <summary><c>grok-&lt;version&gt;-build</c> → <c>grok-&lt;version&gt;</c>,
    /// where the version is one or more dot-separated runs of ASCII digits.
    /// Every other id is returned unchanged. Expects the lowercase
    /// <c>canonical_model_id</c> spelling the graph payload carries.</summary>
    public static string GroupId(string canonicalId)
    {
        // Longer than "grok--build" so the prefix and suffix cannot overlap
        // ("grok-build") and the version between them is non-empty.
        if (canonicalId.Length <= "grok--build".Length
            || !canonicalId.StartsWith("grok-", StringComparison.Ordinal)
            || !canonicalId.EndsWith("-build", StringComparison.Ordinal))
        {
            return canonicalId;
        }

        var version = canonicalId["grok-".Length..^"-build".Length];
        var isVersion = version.Split('.').All(part =>
            part.Length > 0 && part.All(c => c is >= '0' and <= '9'));
        return isVersion ? $"grok-{version}" : canonicalId;
    }
}
