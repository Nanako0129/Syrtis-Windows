using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Whether a locally recorded model belongs to a window's declared model scope.
/// Port of TokenBarCore/ModelScope.swift.
///
/// This is a JOIN BETWEEN TWO NAMING SYSTEMS, and saying so is the point. The
/// scope arrives as the provider's display-name slug — <c>fable</c>, from a
/// limit entry whose <c>scope.model.display_name</c> is "Fable" — while the
/// model on a message is the canonical id the local transcript carried,
/// <c>claude-fable-5</c>. Nothing guarantees the two agree, and the engine
/// cannot close the gap for us: the live <c>oauth/usage</c> payload reports
/// <c>scope.model.id: null</c>, so the display name is the only identity the
/// provider actually sends.
///
/// A join like this fails silently by default — the bars simply stop matching
/// the curve, which is the one thing the card exists to explain. So the rule is
/// deliberately narrow and stated once; its failure is made visible by the
/// caller reporting an empty scoped result rather than drawing zero usage.
/// </summary>
public static class ModelScope
{
    /// <summary>Lowercase alphanumeric runs. The same shape <c>claude_slug</c>
    /// produces on the Rust side, so <c>Fable</c> and <c>claude-fable-5</c>
    /// decompose comparably.</summary>
    internal static List<string> Tokens(string value)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>True when every token of the scope appears in the model id.
    ///
    /// Subset rather than equality, because the two systems name at different
    /// granularities: the provider says "Fable" where the transcript says
    /// <c>claude-fable-5</c>. Subset rather than substring, because a substring
    /// test is not a rule anyone can state — it matches on accidents of spelling
    /// and cannot be reasoned about when a new model appears.
    ///
    /// Over-matching is bounded by the engine, not by this function: a scope
    /// naming every model (<c>all-models</c>, or a name ending in it) never
    /// reaches the wire. Under-matching — a display name carrying a word the id
    /// does not — is the failure this cannot rule out, and is exactly why the
    /// caller reports an empty scoped result instead of zero usage.</summary>
    public static bool Covers(string scope, string modelId)
    {
        var wanted = Tokens(scope);
        if (wanted.Count == 0)
        {
            return false;
        }

        var present = new HashSet<string>(Tokens(modelId), StringComparer.Ordinal);
        return wanted.All(present.Contains);
    }

    /// <summary>The model scope of the window a client's card or history
    /// series names, or null when the window is not scoped (or not in the
    /// payload). The ONE place a consumer gets a scope from: the window card,
    /// the history rows and the equivalence estimate each hold only a
    /// <c>(client, account, window key)</c>, and re-deriving the scope from
    /// that key at each site is three parsers for one fact, which is how they
    /// drift. The scope itself is decided in Rust
    /// (<c>append_claude_scoped_windows</c>) and only looked up here.
    /// <para>
    /// Port of macOS <c>WindowCardLoader.modelScope(payload:cardId:)</c>
    /// (WindowCardLoader.swift:384-396). macOS is handed a card id; the
    /// Windows callers hold a window key (a card tab's
    /// <see cref="QuotaWindowIdentity.WindowKey"/>, a history series' key),
    /// so a window matches on either its <see cref="UsageWindow.CardId"/> or
    /// its <c>PaceStatus.WindowKey</c>. For a scoped window the two are the
    /// same string (<c>weekly_scoped.{slug}.v1</c>, or the flat lane key it
    /// succeeds; Rust sets both from one value).
    /// </para>
    /// <para>
    /// The account matters: a flat-successor key (<c>sonnet.weekly.v1</c>)
    /// is unscoped on an account still sent the flat field and scoped on one
    /// sent only the <c>limits[]</c> entry, so the same key can carry
    /// different scopes. When <paramref name="accountScope"/> names a
    /// snapshot's <c>HistoryScope</c>, only that snapshot answers. Otherwise
    /// (no live scope to join by) this falls back to macOS's rule, the
    /// client's first snapshot, which on macOS is the only rule.
    /// </para></summary>
    public static string? Of(
        AgentUsagePayload? payload, string? clientId, string? accountScope, string? windowKey)
    {
        if (payload is null || clientId is null || windowKey is null)
        {
            return null;
        }

        var agents = payload.Agents.Where(agent => agent.ClientId == clientId).ToList();
        var owned = accountScope is null
            ? []
            : agents.Where(agent => agent.HistoryReadScope?.Scope == accountScope).ToList();
        var candidates = owned.Count > 0 ? owned : agents.Take(1).ToList();
        return candidates
            .SelectMany(agent => agent.RawCardWindows)
            .FirstOrDefault(window =>
                window.CardId == windowKey || window.PaceStatus.WindowKey == windowKey)
            ?.ModelScope;
    }
}
