using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>Covers <see cref="AgentUsageSnapshot.IsSetupPlaceholder"/> and
/// <see cref="AgentUsagePayload.ConfiguredClientIds"/> — the Windows port of
/// macOS <c>isSetupPlaceholder</c> / <c>configuredClientIds</c>
/// (AgentUsage.swift :574-598, :788-792). These decide which snapshots earn a
/// quota-only tab without any local usage.</summary>
public class AgentUsageConfiguredClientsTests
{
    private static AgentUsageSnapshot Snapshot(
        string clientId, string source, string? error = null) =>
        new(clientId, source, "2026-09-25T00:00:00.000Z", Windows: [], Error: error);

    [Theory]
    [InlineData("unconfigured")]
    [InlineData("keychain-consent")]
    [InlineData("keychain-denied")]
    public void SetupPlaceholderSourcesAreFlagged(string source) =>
        Assert.True(Snapshot("codex", source).IsSetupPlaceholder);

    [Theory]
    [InlineData("oauth")]
    [InlineData("api-key")]
    [InlineData("cli")]
    public void NonPlaceholderSourcesAreNotFlagged(string source) =>
        Assert.False(Snapshot("codex", source).IsSetupPlaceholder);

    [Fact]
    public void TransientErrorSnapshotStaysConfigured()
    {
        // An error-only snapshot (oauth source, an Error message from a
        // transient failure) is not a setup placeholder — it reports "this
        // account IS configured, but the last fetch failed" and must keep
        // its tab.
        var snapshot = Snapshot("codex", "oauth", error: "Codex usage request failed.");
        Assert.False(snapshot.IsSetupPlaceholder);
    }

    [Fact]
    public void ConfiguredClientIdsExcludesPlaceholdersAndDedupes()
    {
        var payload = new AgentUsagePayload(
            "2026-09-25T00:00:00.000Z",
            Agents:
            [
                Snapshot("codex", "unconfigured"),
                Snapshot("antigravity", "unconfigured"),
                Snapshot("claude", "oauth"),
                Snapshot("copilot", "oauth"),
                // A duplicate clientId (shouldn't happen in a real payload,
                // but ConfiguredClientIds must not double-count it).
                Snapshot("claude", "oauth"),
            ]);

        Assert.Equal(["claude", "copilot"], payload.ConfiguredClientIds);
    }

    [Fact]
    public void ConfiguredClientIdsKeepsATransientErrorCardButDropsAKeychainPrompt()
    {
        var payload = new AgentUsagePayload(
            "2026-09-25T00:00:00.000Z",
            Agents:
            [
                Snapshot("codex", "oauth", error: "Codex usage request failed. Retrying automatically."),
                Snapshot("claude", "keychain-consent"),
            ]);

        Assert.Equal(["codex"], payload.ConfiguredClientIds);
    }
}
