using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Whether a call to <see cref="QuotaSelectionPolicy.ResolveReading"/>
/// should persist the pair (<c>tokenbar.quota.lastRemaining</c> +
/// <c>tokenbar.quota.lastSelection</c> + <c>tokenbar.quota.lastResolvedAt</c>),
/// clear it, or leave it as-is. Port of macOS applyQuotaRemaining
/// (TrayAnimator.swift:252-279), which is the only writer of that pair.</summary>
public enum QuotaCacheWrite
{
    /// <summary>Payload missing (outer FFI failure) or every AUTO candidate is
    /// hidden: the cached scalar is shown (or suppressed, for the hidden
    /// case) but the persisted pair is left untouched.</summary>
    Unchanged,

    /// <summary>A payload resolved a fresh value: write value + selection +
    /// resolved-at together.</summary>
    Write,

    /// <summary>A payload arrived but the selection could not resolve from it
    /// (terminal failure or a selection the payload does not contain), and it
    /// is not the all-hidden case: clear all three keys.</summary>
    Clear,
}

/// <summary>One resolution of the tray's quota reading, shared by the tray
/// icon and the Settings preview so neither duplicates the branching in
/// <see cref="ResolveReading"/>. <see cref="PickedClientId"/>/
/// <see cref="PickedCardId"/> are set only for <see cref="QuotaCacheWrite.Write"/>
/// (diagnostic detail for the tray's DevLog line; nothing was picked in the
/// other cases).</summary>
public readonly record struct QuotaReading(
    double? Remaining,
    string EffectiveSelection,
    DateTimeOffset? ResolvedAt,
    string? PickedClientId,
    string? PickedCardId,
    QuotaCacheWrite CacheWrite);

public static class QuotaSelectionPolicy
{
    public static string EffectiveSelection(
        AgentUsagePayload? payload,
        string persistedSelection) =>
        QuotaResolver.CanonicalSelection(payload, persistedSelection);

    public static string? MigrationToPersist(
        AgentUsagePayload? payload,
        string persistedSelection)
    {
        var canonical = EffectiveSelection(payload, persistedSelection);
        return canonical == QuotaResolver.Auto || canonical == persistedSelection
            ? null
            : canonical;
    }

    public static QuotaPick? Resolve(
        AgentUsagePayload? payload,
        string persistedSelection,
        IReadOnlySet<string>? excluding = null)
    {
        var selection = EffectiveSelection(payload, persistedSelection);
        return QuotaResolver.Resolve(payload, selection, excluding);
    }

    /// <summary>Returns a last-good reading only when it belongs to the
    /// current effective selection. The tray deliberately keeps one pair, not
    /// a multi-selection cache.</summary>
    public static double? MatchingLastGoodRemaining(
        string effectiveSelection,
        string? lastGoodSelection,
        double? lastGoodRemaining) =>
        lastGoodSelection == effectiveSelection ? lastGoodRemaining : null;

    /// <summary>When the selected reading was fetched: the <c>updatedAt</c> of
    /// the snapshot <see cref="Resolve"/> picked. Rust's same-binding
    /// last_good fallback keeps the original fetch's updated_at, so an
    /// explicit selection served from it reports its real age. Port of
    /// macOS QuotaSelectionPolicy.swift:64-77 (<c>resolvedAt</c>) — macOS
    /// matches the picked snapshot by clientId+accountKey; Windows's
    /// AgentUsageSnapshot has no AccountKey field yet (always null on this
    /// platform, see QuotaSummary.cs). <see cref="QuotaPick"/> carries the
    /// picked snapshot itself (not just its ClientId), so this reads that
    /// snapshot directly rather than re-searching the payload by ClientId —
    /// a re-search would pick the wrong snapshot's updatedAt if two ever
    /// shared a ClientId. Null when nothing resolves or its updatedAt
    /// doesn't parse.</summary>
    public static DateTimeOffset? ResolvedAt(
        AgentUsagePayload payload,
        string persistedSelection,
        IReadOnlySet<string>? excluding = null) =>
        Resolve(payload, persistedSelection, excluding) is { } pick ? ResolvedAt(pick) : null;

    /// <summary>Same as the overload above, against an already-resolved pick
    /// (avoids re-resolving when the caller has one in hand, e.g.
    /// <see cref="ResolveReading"/>).</summary>
    private static DateTimeOffset? ResolvedAt(QuotaPick pick) =>
        DateTimeOffset.TryParse(
            pick.Agent.UpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    /// <summary>The tray/Settings-preview quota resolution, shared so both
    /// callers implement the same rule once. Port of macOS
    /// applyQuotaRemaining (TrayAnimator.swift:252-279):
    /// <list type="bullet">
    /// <item>no outer payload (FFI failure / cold start) → the cached scalar,
    /// only when its selection still matches; pair untouched.</item>
    /// <item>payload present and the selection resolves → a fresh value and
    /// its resolved-at; write the pair.</item>
    /// <item>payload present, no pick, but every AUTO candidate is merely
    /// hidden → suppress the display only; pair untouched (Windows-specific,
    /// unchanged from before this reading was introduced).</item>
    /// <item>payload present, no pick, not all-hidden (terminal failure or an
    /// explicit selection the payload can't resolve) → clear the pair. This
    /// also covers an all-transient-errored AUTO payload at startup — AUTO
    /// skips every agent with an error, so that's indistinguishable from "no
    /// candidates" here. Intentional macOS #418 parity (maintainer-confirmed
    /// after being told this exact consequence: its "keep the cached scalar
    /// through an Auto outage" design was rejected); Auto all-down is
    /// tracked separately as macOS #419.</item>
    /// </list></summary>
    public static QuotaReading ResolveReading(
        AgentUsagePayload? payload,
        string persistedSelection,
        IReadOnlySet<string> excluding,
        string? cachedSelection,
        double? cachedRemaining)
    {
        var selection = EffectiveSelection(payload, persistedSelection);
        if (payload is null)
        {
            return new QuotaReading(
                MatchingLastGoodRemaining(selection, cachedSelection, cachedRemaining),
                selection, null, null, null, QuotaCacheWrite.Unchanged);
        }

        // Port of macOS QuotaSelectionPolicy.swift:49-54's `remaining.isFinite`
        // guard: an explicit selection's window isn't filtered for finiteness
        // the way AutoCandidate filters its candidates (QuotaResolver.cs), so
        // a NaN/Infinity RemainingPercent must fall through to Clear rather
        // than Write — SetDouble(NaN) throws under System.Text.Json.
        if (QuotaResolver.Resolve(payload, selection, excluding) is { } pick
            && double.IsFinite(pick.Window.RemainingPercent))
        {
            var remaining = Math.Clamp(pick.Window.RemainingPercent, 0, 100);
            return new QuotaReading(
                remaining, selection, ResolvedAt(pick), pick.ClientId, pick.Window.CardId,
                QuotaCacheWrite.Write);
        }

        if (QuotaResolver.ExcludedAllCandidates(payload, selection, excluding))
        {
            return new QuotaReading(null, selection, null, null, null, QuotaCacheWrite.Unchanged);
        }

        return new QuotaReading(null, selection, null, null, null, QuotaCacheWrite.Clear);
    }
}
