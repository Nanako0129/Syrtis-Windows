using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>How old the gauge's quota reading may be before it draws grey
/// instead of in its gauge colour. Port of macOS TrayAnimator.swift:285-313
/// (readingIsStale, quotaStaleAfter = 30 min, chosen by the maintainer
/// 2026-09-27). Shared by the tray icon and the Settings preview.</summary>
public static class QuotaStaleness
{
    /// <summary>Tens of minutes, not a small multiple of the 300 s poll.
    /// Named constant per macOS TrayAnimator.swift:287-290.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Stamp key beside <c>tokenbar.quota.lastRemaining</c> +
    /// <c>tokenbar.quota.lastSelection</c> (TrayFeed.ResolveRemaining is the
    /// only writer); shared here so the read side has one implementation
    /// too.</summary>
    public const string LastResolvedAtKey = "tokenbar.quota.lastResolvedAt";

    private static readonly double MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly double MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>The persisted stamp, parsed. Shared by TrayFeed (cold start /
    /// no-payload staleness) and the Settings preview so neither duplicates
    /// the GetDouble→IsFinite→FromUnixTimeMilliseconds chain.</summary>
    public static DateTimeOffset? PersistedResolvedAt(SettingsStore store)
    {
        // A finite value can still lie outside what DateTimeOffset represents
        // (a hand-edited 1e300), and FromUnixTimeMilliseconds throws there.
        // Out of range is treated as unknown age, like a missing stamp.
        var ms = store.GetDouble(LastResolvedAtKey, double.NaN);
        return double.IsFinite(ms) && ms >= MinUnixMs && ms <= MaxUnixMs
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;
    }

    /// <summary>Exclusive boundary (macOS TrayAnimator.swift:312: <c>&gt;
    /// quotaStaleAfter</c>, not <c>&gt;=</c>) — a reading exactly 30 minutes
    /// old is not yet stale. Unknown age (<paramref name="resolvedAt"/> null)
    /// is never stale.</summary>
    public static bool IsStale(DateTimeOffset? resolvedAt, DateTimeOffset now) =>
        resolvedAt is { } t && now - t > StaleAfter;

    /// <summary>Age source per macOS TrayAnimator.swift:298-313: with a
    /// payload, the age comes from the same resolve that produced the shown
    /// value (so value and age can't come from different writers); without
    /// one, from the stamp persisted beside the cached scalar
    /// (<paramref name="persistedResolvedAt"/>). Unknown age is not
    /// stale.</summary>
    public static bool ReadingIsStale(
        AgentUsagePayload? payload,
        string persistedSelection,
        IReadOnlySet<string> excluding,
        DateTimeOffset? persistedResolvedAt,
        DateTimeOffset now)
    {
        var resolvedAt = payload is not null
            ? QuotaSelectionPolicy.ResolvedAt(payload, persistedSelection, excluding)
            : persistedResolvedAt;
        return IsStale(resolvedAt, now);
    }
}
