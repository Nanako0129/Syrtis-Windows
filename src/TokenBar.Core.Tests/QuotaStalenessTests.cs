using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>Port of the macOS #418 staleness assertions (TrayAnimator.swift
/// readingIsStale / applyQuotaRemaining). The renderer half (grey fill,
/// no-reading ignores the flag) lives in TrayIconRenderer.RenderGauge, which
/// compiles only under the WinUI (net10.0-windows) target and is not linked
/// into this cross-platform project. It has no automated test — CI runs only
/// TokenBar.Core.Tests, which cannot see this file at all — and is checked
/// visually on x64 instead. See TrayIconRenderer.cs's
/// StaleFillDark/StaleFillLight and the `stale && remaining is not null`
/// guard for that logic.</summary>
public class QuotaStalenessTests
{
    private static readonly HashSet<string> NoneHidden = [];

    private static AgentUsagePayload PayloadWithUpdatedAt(DateTimeOffset updatedAt) => new(
        "now",
        new[]
        {
            new AgentUsageSnapshot(
                "codex", "fixture", updatedAt.ToString("o"),
                new[] { new UsageWindow("Session", 20, 80, CardId: "session.v1") }),
        });

    [Fact]
    public void IsStale_ThirtyMinuteBoundaryIsExclusive()
    {
        var now = DateTimeOffset.UtcNow;
        var exactlyAtBoundary = now - QuotaStaleness.StaleAfter;
        Assert.False(QuotaStaleness.IsStale(exactlyAtBoundary, now));

        var oneSecondPast = exactlyAtBoundary - TimeSpan.FromSeconds(1);
        Assert.True(QuotaStaleness.IsStale(oneSecondPast, now));
    }

    [Fact]
    public void IsStale_UnknownAgeIsNeverStale()
    {
        Assert.False(QuotaStaleness.IsStale(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReadingIsStale_ColdStartAgesByThePersistedStamp()
    {
        var now = DateTimeOffset.UtcNow;
        var staleStamp = now - QuotaStaleness.StaleAfter - TimeSpan.FromMinutes(1);
        Assert.True(QuotaStaleness.ReadingIsStale(
            payload: null, QuotaResolver.Auto, NoneHidden, staleStamp, now));

        var freshStamp = now - TimeSpan.FromMinutes(1);
        Assert.False(QuotaStaleness.ReadingIsStale(
            payload: null, QuotaResolver.Auto, NoneHidden, freshStamp, now));
    }

    [Fact]
    public void ReadingIsStale_APresentPayloadsOwnTimeWinsOverAnOldStamp()
    {
        var now = DateTimeOffset.UtcNow;
        var freshPayload = PayloadWithUpdatedAt(now - TimeSpan.FromMinutes(1));
        var veryOldStamp = now - TimeSpan.FromDays(1);

        // The stamp alone would report stale; the payload's own resolve must
        // win (macOS TrayAnimator.swift:306-309: payload.map { ... } ?? stamp).
        Assert.False(QuotaStaleness.ReadingIsStale(
            freshPayload, QuotaResolver.Auto, NoneHidden, veryOldStamp, now));

        var stalePayload = PayloadWithUpdatedAt(now - QuotaStaleness.StaleAfter - TimeSpan.FromMinutes(1));
        var freshStamp = now - TimeSpan.FromMinutes(1);
        Assert.True(QuotaStaleness.ReadingIsStale(
            stalePayload, QuotaResolver.Auto, NoneHidden, freshStamp, now));
    }

    // With a payload, the age comes only from that payload's resolve. When it
    // resolves nothing, the age is unknown and the reading is not stale; the
    // persisted stamp must not stand in (macOS TrayAnimator.swift:306-309,
    // `payload.map { resolvedAt } ?? stamp` never reaches the stamp once a
    // payload exists).
    [Fact]
    public void ReadingIsStale_APresentPayloadThatResolvesNothingIgnoresTheStamp()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = PayloadWithUpdatedAt(now - TimeSpan.FromMinutes(1));
        var veryOldStamp = now - TimeSpan.FromDays(1);

        Assert.False(QuotaStaleness.ReadingIsStale(
            payload, "missing-client|no.such.card", NoneHidden, veryOldStamp, now));
    }
}
