using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>Port of the macOS #418 clear/keep assertions
/// (TrayAnimator.swift:252-279 applyQuotaRemaining) against
/// <see cref="QuotaSelectionPolicy.ResolveReading"/>, the Core function
/// TrayFeed.ResolveRemaining and SettingsWindow's preview both call.</summary>
public class QuotaReadingTests
{
    private static readonly HashSet<string> NoneHidden = [];

    private static AgentUsageSnapshot Healthy(string clientId, string updatedAt, double remaining) => new(
        clientId, "fixture", updatedAt,
        new[] { new UsageWindow("Session", 100 - remaining, remaining, CardId: "session.v1") });

    private static AgentUsageSnapshot ErrorOnly(string clientId, string updatedAt) => new(
        clientId, "fixture", updatedAt, Array.Empty<UsageWindow>(), Error: "network");

    [Fact]
    public void ResolveReading_NoOuterPayloadKeepsTheMatchingCachedScalar()
    {
        var reading = QuotaSelectionPolicy.ResolveReading(
            payload: null, QuotaResolver.Auto, NoneHidden,
            cachedSelection: QuotaResolver.Auto, cachedRemaining: 42);

        Assert.Equal(42, reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Unchanged, reading.CacheWrite);
    }

    // With no payload the cached scalar is reused only for the selection it
    // was resolved for: switching source on an offline start must show no
    // reading, not the previous source's percentage.
    [Fact]
    public void ResolveReading_NoOuterPayloadDropsACachedScalarFromAnotherSelection()
    {
        var reading = QuotaSelectionPolicy.ResolveReading(
            payload: null, "codex|session.v1", NoneHidden,
            cachedSelection: QuotaResolver.Auto, cachedRemaining: 42);

        Assert.Null(reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Unchanged, reading.CacheWrite);
    }

    [Fact]
    public void ResolveReading_APresentPayloadWithAPickWritesValueSelectionAndResolvedAt()
    {
        var payload = new AgentUsagePayload(
            "now", new[] { Healthy("codex", "2026-07-10T12:00:00.000Z", 80) });

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, "codex|session.v1", NoneHidden, cachedSelection: null, cachedRemaining: null);

        Assert.Equal(80, reading.Remaining);
        Assert.Equal("codex|session.v1", reading.EffectiveSelection);
        Assert.Equal(QuotaCacheWrite.Write, reading.CacheWrite);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-10T12:00:00.000Z"),
            reading.ResolvedAt);
    }

    [Fact]
    public void ResolveReading_PresentPayloadWithNoPickClearsValueAndCache()
    {
        // Explicit selection naming a card the payload doesn't have — not
        // the all-hidden case (NoneHidden is empty).
        var payload = new AgentUsagePayload(
            "now", new[] { Healthy("codex", "2026-07-10T12:00:00.000Z", 80) });

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, "codex|missing.v1", NoneHidden,
            cachedSelection: "codex|missing.v1", cachedRemaining: 50);

        Assert.Null(reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Clear, reading.CacheWrite);
    }

    [Fact]
    public void ResolveReading_NonFiniteExplicitPickClearsInsteadOfWriting()
    {
        // AutoCandidate filters non-finite RemainingPercent (QuotaResolver.cs),
        // but an explicit pick's window isn't filtered the same way — port of
        // macOS QuotaSelectionPolicy.swift:49-54's `remaining.isFinite` guard.
        // Without it this would try to SetDouble(NaN), which System.Text.Json
        // throws on.
        var snapshot = new AgentUsageSnapshot(
            "codex", "fixture", "2026-07-10T12:00:00.000Z",
            new[] { new UsageWindow("Session", double.NaN, double.NaN, CardId: "session.v1") });
        var payload = new AgentUsagePayload("now", new[] { snapshot });

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, "codex|session.v1", NoneHidden,
            cachedSelection: "codex|session.v1", cachedRemaining: 50);

        Assert.Null(reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Clear, reading.CacheWrite);
    }

    [Fact]
    public void ResolveReading_ErrorOnlyExplicitSelectionClears()
    {
        var payload = new AgentUsagePayload(
            "now", new[] { ErrorOnly("codex", "2026-07-10T12:00:00.000Z") });

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, "codex|session.v1", NoneHidden,
            cachedSelection: "codex|session.v1", cachedRemaining: 50);

        Assert.Null(reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Clear, reading.CacheWrite);
    }

    [Fact]
    public void ResolveReading_AllTransientErroredAutoPayloadClearsValueAndCache()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                ErrorOnly("codex", "2026-07-10T12:00:00.000Z"),
                ErrorOnly("claude", "2026-07-10T12:00:00.000Z"),
            });

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, QuotaResolver.Auto, NoneHidden,
            cachedSelection: QuotaResolver.Auto, cachedRemaining: 77);

        Assert.Null(reading.Remaining);
        Assert.Equal(QuotaCacheWrite.Clear, reading.CacheWrite);
    }

    [Fact]
    public void ResolveReading_AllHiddenSuppressesDisplayOnlyAndKeepsThePair()
    {
        var payload = new AgentUsagePayload(
            "now",
            new[]
            {
                Healthy("codex", "2026-07-10T12:00:00.000Z", 80),
                Healthy("claude", "2026-07-10T12:00:00.000Z", 60),
            });
        var allHidden = new HashSet<string> { "codex", "claude" };

        var reading = QuotaSelectionPolicy.ResolveReading(
            payload, QuotaResolver.Auto, allHidden,
            cachedSelection: QuotaResolver.Auto, cachedRemaining: 60);

        Assert.Null(reading.Remaining); // display suppressed
        Assert.Equal(QuotaCacheWrite.Unchanged, reading.CacheWrite); // pair untouched
    }

    [Fact]
    public void ResolvedAt_NullWhenNothingResolves()
    {
        // Explicit selection naming a card the payload doesn't have — no pick
        // at all, so ResolvedAt never gets a snapshot to read an updatedAt
        // from.
        var payload = new AgentUsagePayload(
            "now", new[] { Healthy("codex", "2026-07-10T12:00:00.000Z", 80) });

        Assert.Null(QuotaSelectionPolicy.ResolvedAt(payload, "missing|session.v1", NoneHidden));
    }

    [Fact]
    public void ResolvedAt_ReadsThePickedSnapshotNotTheFirstSharingItsClientId()
    {
        // Two snapshots share a ClientId (a future multi-account payload).
        // AUTO picks the tighter window, which lives on the SECOND snapshot;
        // re-searching payload.Agents by ClientId (the old implementation)
        // would find the FIRST one instead and report its updatedAt — wrong.
        // QuotaPick now carries the Agent reference itself, so ResolvedAt
        // must read the second snapshot's updatedAt.
        var first = new AgentUsageSnapshot(
            "codex", "fixture", "2020-01-01T00:00:00.000Z",
            new[] { new UsageWindow("Session", 10, 90, CardId: "session.v1") });
        var second = new AgentUsageSnapshot(
            "codex", "fixture", "2026-07-10T12:00:00.000Z",
            new[] { new UsageWindow("Weekly", 90, 10, CardId: "weekly.v1") });
        var payload = new AgentUsagePayload("now", new[] { first, second });

        Assert.Equal(
            DateTimeOffset.Parse("2026-07-10T12:00:00.000Z"),
            QuotaSelectionPolicy.ResolvedAt(payload, QuotaResolver.Auto, NoneHidden));
    }

    // An explicit pick of a non-primary account: the reading, its age and the
    // cached selection all belong to that account, not to the primary card
    // that shares its ClientId and card id.
    [Fact]
    public void ResolveReading_ExplicitOtherAccountReadsThatAccountsSnapshot()
    {
        var primary = new AgentUsageSnapshot(
            "claude", "oauth", "2020-01-01T00:00:00.000Z",
            new[] { new UsageWindow("Session", 20, 80, CardId: "session.v1") });
        var desktop = new AgentUsageSnapshot(
            "claude", "oauth", "2026-07-10T12:00:00.000Z",
            new[] { new UsageWindow("Session", 70, 30, CardId: "session.v1") },
            AccountKey: "claude-desktop");
        var selection = QuotaResolver.Selection("claude", "session.v1", "claude-desktop");

        var reading = QuotaSelectionPolicy.ResolveReading(
            new AgentUsagePayload("now", new[] { primary, desktop }), selection, NoneHidden,
            cachedSelection: null, cachedRemaining: null);

        Assert.Equal(QuotaCacheWrite.Write, reading.CacheWrite);
        Assert.Equal(30, reading.Remaining);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T12:00:00.000Z"), reading.ResolvedAt);
        Assert.Equal(selection, reading.EffectiveSelection);
        Assert.True(reading.PickedOtherAccount);
    }

    // That account signs out (its card leaves the payload) while the primary
    // keeps the same card id: the pick must not fall back to the primary's
    // reading; the pair is cleared.
    [Fact]
    public void ResolveReading_ExplicitOtherAccountGoneClearsInsteadOfShowingThePrimary()
    {
        var primary = new AgentUsageSnapshot(
            "claude", "oauth", "2026-07-10T12:00:00.000Z",
            new[] { new UsageWindow("Session", 20, 80, CardId: "session.v1") });
        var selection = QuotaResolver.Selection("claude", "session.v1", "claude-desktop");

        var reading = QuotaSelectionPolicy.ResolveReading(
            new AgentUsagePayload("now", new[] { primary }), selection, NoneHidden,
            cachedSelection: selection, cachedRemaining: 30);

        Assert.Equal(QuotaCacheWrite.Clear, reading.CacheWrite);
        Assert.Null(reading.Remaining);
    }
}
