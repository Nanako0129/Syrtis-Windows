using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>Port of the macOS #418 staleness assertions (TrayAnimator.swift
/// readingIsStale / applyQuotaRemaining). Which glyph the renderer draws
/// (live, stale grey, or no reading whatever the stamp) is TrayGlyph.Gauge,
/// covered by TrayGlyphTests. The pixels live in TrayIconRenderer, which
/// compiles only under the WinUI (net10.0-windows) target and is not linked
/// into this cross-platform project, so they are checked visually on x64.</summary>
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

    // The tray adopts every fetched payload, including the flyout's (#214),
    // so a payload where the selected agent failed must not reset the
    // reading's age, or a provider blocked for 30 minutes would never grey.
    // A same-binding transient failure comes back from Rust as the last-good
    // snapshot with its original updated_at plus an error
    // (agent_usage.rs last_good_same_binding_fallback_preserves_clean_snapshot_without_enrichment);
    // a terminal failure, or one with no last-good, is an error snapshot
    // stamped now with no windows.
    [Fact]
    public void AdoptingAFailedPayloadKeepsTheReadingsAge()
    {
        var now = DateTimeOffset.UtcNow;
        var fetchedAt = now - QuotaStaleness.StaleAfter - TimeSpan.FromMinutes(1);
        const string selection = "copilot|premium.v1";
        var goodCopilot = new AgentUsageSnapshot(
            "copilot", "oauth", fetchedAt.ToString("o"),
            new[] { new UsageWindow("Premium", 60, 40, CardId: "premium.v1") });
        var good = new AgentUsagePayload("now", new[] { goodCopilot });
        var transient = new AgentUsagePayload(
            "now", new[] { goodCopilot with { Error = "rate limited" } });
        var terminal = new AgentUsagePayload(
            "now",
            new[]
            {
                new AgentUsageSnapshot(
                    "copilot", "oauth", now.ToString("o"), Array.Empty<UsageWindow>(),
                    Error: "signed out"),
            });

        var before = QuotaSelectionPolicy.ResolveReading(good, selection, NoneHidden, null, null);
        var afterTransient = QuotaSelectionPolicy.ResolveReading(
            transient, selection, NoneHidden, before.EffectiveSelection, before.Remaining);
        var afterTerminal = QuotaSelectionPolicy.ResolveReading(
            terminal, selection, NoneHidden, before.EffectiveSelection, before.Remaining);

        Assert.Equal(fetchedAt.ToUnixTimeMilliseconds(), before.ResolvedAt?.ToUnixTimeMilliseconds());
        Assert.Equal(before.ResolvedAt, afterTransient.ResolvedAt);
        // A fresh stamp cannot stand in for the payload's own age.
        Assert.True(QuotaStaleness.ReadingIsStale(transient, selection, NoneHidden, now, now));

        // Terminal: no reading at all (#418 parity), never a fresh-looking one.
        Assert.Equal(QuotaCacheWrite.Clear, afterTerminal.CacheWrite);
        Assert.Null(afterTerminal.Remaining);
        Assert.Null(afterTerminal.ResolvedAt);
    }

    // A finite stamp outside DateTimeOffset's range (a hand-edited 1e300)
    // must read as unknown age rather than throw inside the tray update.
    [Theory]
    [InlineData("1e300", false)]
    [InlineData("-1e300", false)]
    [InlineData("1790000000000", true)]
    public void PersistedResolvedAt_OutOfRangeStampIsUnknownNotAThrow(string raw, bool expectParsed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tb-quota-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"" + QuotaStaleness.LastResolvedAtKey + "\": " + raw + "}");
            var parsed = QuotaStaleness.PersistedResolvedAt(new SettingsStore(file));
            Assert.Equal(expectParsed, parsed is not null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
