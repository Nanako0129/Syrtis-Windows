using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Every decision the attribution onboarding card makes (port of macOS
/// <c>AttributionOnboardingCard.swift</c>). The card itself is drawn in
/// <c>DashboardView.Quota.cs</c>, which no test project compiles, so nothing
/// that decides lives there.
/// <para>
/// Like the Discord intro, <see cref="DismissedKey"/> records an ANSWER ("Not
/// now"), never the fact of having been shown: a user who has not decided keeps
/// seeing the card, because it is a standing invitation, not a one-time
/// interruption (AttributionOnboardingCard.swift:4-9).
/// </para>
/// </summary>
internal static class AttributionOnboardingCard
{
    internal const string DismissedKey = "tokenbar.usage.attribution.onboardingDismissed";

    /// <summary>Proposal lines beyond this fold into "and N more" rather than
    /// growing the card without bound (AttributionOnboardingCard.swift:18-20).</summary>
    internal const int MaxVisibleLines = 4;

    /// <summary>The card has to stand out from the cards around it: in macOS's
    /// first round it was a plain card and the maintainer did not notice it. An
    /// accent wash and an accent border, both kept low enough that it still
    /// reads as part of the panel (AttributionOnboardingCard.swift:22-27). The
    /// values are macOS's, applied to the Windows accent colour.</summary>
    internal const double AccentFill = 0.10;

    internal const double AccentStroke = 0.55;

    internal static class Copy
    {
        internal const string Title = "Attribute usage to subscriptions";

        internal const string Subtitle =
            "Quota history shows no tokens or API-equivalent value until usage is attributed.";

        /// <summary>source client · provider → target</summary>
        internal const string SuggestionLine = "{0} · {1} → {2}";

        internal const string MoreCount = "and {0} more";

        internal const string UnsuggestedHint = "Without a suggestion: {0} — set them in Settings.";

        internal const string NotNow = "Not now";

        internal const string SetUpManually = "Set up manually…";

        internal const string ApplySuggestions = "Apply suggestions";
    }

    /// <summary>The gates that need no dashboard data. A confirmed table this
    /// codec cannot read (a newer build's format, a foreign value) is not
    /// "nothing confirmed": it counts as configured, because inviting that user
    /// would end in a write the codec refuses (AttributionOnboardingCard.swift
    /// :43-52). Non-user runtimes never show it: macOS's
    /// <c>BuildIdentity.isNonUserRuntime</c> maps to the run modes
    /// <see cref="DiscordPresence.TestArguments"/> already names on Windows
    /// (no separate demo/selftest flags exist here).</summary>
    internal static bool MayShow(
        UsageAttribution.Table confirmed, bool dismissed, IEnumerable<string> arguments) =>
        confirmed.IsWritable && confirmed.Records.Count == 0 && !dismissed
        && !arguments.Any(DiscordPresence.TestArguments.Contains);

    internal static bool MayShow(SettingsStore store, IEnumerable<string> arguments) =>
        MayShow(
            UsageAttribution.Confirmed(store),
            store.GetBool(DismissedKey, false),
            arguments);

    /// <summary>With the data in hand: something to offer. With nothing
    /// confirmed, every attributable row is either a proposal or counted as
    /// unsuggested (AttributionOnboardingCard.swift:69-75).</summary>
    internal static bool IsVisible(
        bool mayShow, UsageAttributionSettings.OnboardingSummary? summary) =>
        mayShow && summary is not null
        && (summary.Records.Count > 0 || summary.UnsuggestedCount > 0);

    /// <summary>The proposals for this data, or null before it has loaded.
    /// Computed from the surface's OWN inputs (the model report and the quota
    /// payload the dashboard already polls), never from the stored suggestions
    /// table, which only Settings fills (AttributionOnboardingCard.swift:77-87).
    /// <c>confirmed</c> is empty by construction: the card only shows while
    /// nothing is confirmed.</summary>
    internal static UsageAttributionSettings.OnboardingSummary? Summary(
        ModelReport? report, AgentUsagePayload? agentUsage) =>
        report is null || agentUsage is null
            ? null
            : UsageAttributionSettings.OnboardingSummaryFor(
                report.Entries,
                [],
                UsageAttributionSettings.SubscriptionClients(agentUsage),
                UsageAttributionSettings.RoutedSubscriptionsFrom(agentUsage));

    /// <summary>The summary to draw, or null when the card is not on screen.</summary>
    internal static UsageAttributionSettings.OnboardingSummary? Shown(
        SettingsStore store, ModelReport? report, AgentUsagePayload? agentUsage,
        IEnumerable<string> arguments)
    {
        var may = MayShow(store, arguments);
        var summary = may ? Summary(report, agentUsage) : null;
        return IsVisible(may, summary) ? summary : null;
    }

    /// <summary>Records the answer "Not now" (AttributionOnboardingCard.swift:100-102).</summary>
    internal static void MarkDismissed(SettingsStore store) => store.SetBool(DismissedKey, true);

    /// <summary>Failure message for the card, or null on success. The shared
    /// <see cref="UsageAttributionSettings.Accept"/> decides what is written.</summary>
    internal static string? Apply(
        SettingsStore store, UsageAttributionSettings.OnboardingSummary summary) =>
        UsageAttributionSettings.Accept(store, summary.Records) is { } failure
            ? UsageAttributionPage.FailureMessage(failure)
            : null;

    /// <summary>One proposal line. The record's own state IS the proposed
    /// target (AcceptanceRecords already resolved it), so this only renders it
    /// (AttributionOnboardingCard.swift:104-120).</summary>
    internal static string SuggestionLine(UsageAttribution.Record record)
    {
        var target = record.State.Kind switch
        {
            UsageAttribution.StateKind.Assigned =>
                ClientRegistry.Style(record.State.Target!).DisplayName,
            UsageAttribution.StateKind.Excluded =>
                UsageAttributionPage.Copy.Excluded.Localized(),
            _ => UsageAttributionPage.Copy.Unassigned.Localized(),
        };
        return Copy.SuggestionLine.Localized(
            ClientRegistry.Style(record.Client).DisplayName,
            UsageAttributionPage.ProviderLabel(record.Provider),
            target);
    }

    /// <summary>The proposal lines drawn: at most <see cref="MaxVisibleLines"/>.</summary>
    internal static IReadOnlyList<string> VisibleLines(
        UsageAttributionSettings.OnboardingSummary summary) =>
        [.. summary.Records.Take(MaxVisibleLines).Select(SuggestionLine)];

    /// <summary>"and N more" for the folded remainder, or null when none fold.</summary>
    internal static string? MoreLine(UsageAttributionSettings.OnboardingSummary summary) =>
        summary.Records.Count > MaxVisibleLines
            ? Copy.MoreCount.Localized(summary.Records.Count - MaxVisibleLines)
            : null;

    internal static string? UnsuggestedLine(UsageAttributionSettings.OnboardingSummary summary) =>
        summary.UnsuggestedCount > 0
            ? Copy.UnsuggestedHint.Localized(summary.UnsuggestedCount)
            : null;
}
