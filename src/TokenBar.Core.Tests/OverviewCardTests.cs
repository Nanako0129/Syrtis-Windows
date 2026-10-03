using TokenBar.App;

namespace TokenBar.Core.Tests;

// The Overview's card order is a parity surface, not a local preference: a
// Windows build that sequences the same cards differently is a gap the eye
// notices before any feature list does.
//
// This test exists because the mistake has now been made twice. macOS's own
// OverviewCard comment records that a commit claiming to restore the order had
// two cards reversed and said so in its message, and that its pinned order was
// what caught it. Windows then arrived at the same error independently — the
// quota summary was built directly above the limits card, which left the usage
// chart ahead of it.
public class OverviewCardTests
{
    // Transcribed from macOS Sources/TokenBarCore/OverviewCard.swift, where
    // declaration order is render order:
    //     case quotaSummary, chart, limits, trace, models, streaks
    [Fact]
    public void RenderOrderMatchesMacOS()
    {
        Assert.Equal(
            [
                OverviewCard.QuotaSummary,
                OverviewCard.Chart,
                OverviewCard.Limits,
                OverviewCard.Trace,
                OverviewCard.Models,
                OverviewCard.Streaks,
            ],
            OverviewCards.RenderOrder);
    }

    // Every declared card must be placed. A card added to the enum and left out
    // of the order would simply never render, with nothing to say so.
    [Fact]
    public void EveryCardIsPlacedExactlyOnce()
    {
        var all = Enum.GetValues<OverviewCard>();

        Assert.Equal(all.Length, OverviewCards.RenderOrder.Length);
        Assert.Equal(all.Length, OverviewCards.RenderOrder.Distinct().Count());
        Assert.Empty(all.Except(OverviewCards.RenderOrder));
    }

    // The summary opens the lens. Stated as its own case rather than left
    // implicit in the full-order assertion, because this is the specific
    // property that was wrong and the failure message should say so.
    [Fact]
    public void QuotaSummaryComesFirst() =>
        Assert.Equal(OverviewCard.QuotaSummary, OverviewCards.RenderOrder[0]);
}

// OverviewScope (item 3): selecting a single client's tab must scope the
// Overview lens to that client — the quota summary headline and the
// live-session card both answer "across everything right now", not what a
// single-client tab asked, and the Agent-limits card must narrow to that one
// client instead of showing every agent's bars underneath a tab naming one.
public class OverviewScopeTests
{
    [Fact]
    public void TheOverviewTabItselfIsNotASingleClient() =>
        Assert.Null(OverviewScope.SingleClient(ClientRegistry.OverviewTab));

    [Fact]
    public void AClientTabIsItsOwnSingleClient() =>
        Assert.Equal("gemini", OverviewScope.SingleClient("gemini"));

    [Fact]
    public void QuotaSummaryAndTraceShowOnlyOnTheOverviewTab()
    {
        Assert.True(OverviewScope.ShowsQuotaSummary(null));
        Assert.False(OverviewScope.ShowsQuotaSummary("gemini"));
        Assert.True(OverviewScope.ShowsTrace(null));
        Assert.False(OverviewScope.ShowsTrace("gemini"));
    }

    [Fact]
    public void LimitsAreUnrestrictedOnOverviewAndScopedOnAClientTab()
    {
        Assert.Null(OverviewScope.LimitsClientId(null));
        Assert.Equal("gemini", OverviewScope.LimitsClientId("gemini"));
    }

    // A client that spends another subscription's allowance reaches BuildLimits
    // under the OWNER, because that is how the quota payload keys it. Passing
    // the raw tab id through matched nothing and the card claimed there was no
    // quota data while the quota sat in the payload. Codex review found this on
    // PR #89; nothing pinned the rule, which is why it was missed.
    [Fact]
    public void LimitsClientIdResolvesTheQuotaOwnerRatherThanTheRawTabId()
    {
        Assert.Equal("antigravity", OverviewScope.LimitsClientId("antigravity-cli"));
    }

    [Fact]
    public void LimitsClientIdLeavesAClientThatOwnsItsOwnQuotaAlone()
    {
        Assert.Equal("claude", OverviewScope.LimitsClientId("claude"));
        Assert.Null(OverviewScope.LimitsClientId(null));
    }
}

// Hiding Overview cards and the Agent-limits master switch (macOS
// OverviewCard.swift toggleable/visible; OverviewView.swift:69-90;
// QuotaView.swift:57,:116). Pure decisions only: the WinUI call sites just
// hand these a store.
public class OverviewCardVisibilityTests
{
    private static readonly OverviewCard[] AllInOrder =
    [
        OverviewCard.QuotaSummary, OverviewCard.Chart, OverviewCard.Limits,
        OverviewCard.Trace, OverviewCard.Models, OverviewCard.Streaks,
    ];

    [Fact]
    public void KeyNamesAndIdsMatchMacOS()
    {
        Assert.Equal("tokenbar.overview.hidden", OverviewCards.HiddenKey);
        Assert.Equal("tokenbar.limits.enabled", OverviewCards.LimitsEnabledKey);
        Assert.Equal(
            ["quotaSummary", "chart", "limits", "trace", "models", "streaks"],
            AllInOrder.Select(OverviewCards.Id));
    }

    [Fact]
    public void LabelsAreTitleCasedIds()
    {
        Assert.Equal("Quota Summary", OverviewCards.Label(OverviewCard.QuotaSummary));
        Assert.Equal("Streaks", OverviewCards.Label(OverviewCard.Streaks));
    }

    // The chart is the fixed anchor: Overview is what every hidden lens falls
    // back to, and it must never be emptied.
    [Fact]
    public void EveryCardButTheChartIsToggleable() =>
        Assert.Equal(
            [OverviewCard.QuotaSummary, OverviewCard.Limits, OverviewCard.Trace,
                OverviewCard.Models, OverviewCard.Streaks],
            OverviewCards.Toggleable);

    [Theory]
    [InlineData("")]
    [InlineData(",,")]
    public void NothingHiddenShowsEverythingInRenderOrder(string raw) =>
        Assert.Equal(AllInOrder, OverviewCards.Visible(raw));

    [Fact]
    public void HiddenCardsAreDroppedAndOrderIsKept() =>
        Assert.Equal(
            [OverviewCard.Chart, OverviewCard.Limits, OverviewCard.Streaks],
            OverviewCards.Visible("quotaSummary,trace,models"));

    [Fact]
    public void ATamperedRawStringCannotHideTheChart() =>
        Assert.Equal([OverviewCard.Chart], OverviewCards.Visible(
            "chart,quotaSummary,limits,trace,models,streaks"));

    [Fact]
    public void UnknownIdsAreIgnored() =>
        Assert.Equal(AllInOrder, OverviewCards.Visible("nonsense,Chart,QUOTASUMMARY"));

    // Off drops BOTH the summary line and the card (OverviewView.swift:69-90),
    // and only those two.
    [Fact]
    public void LimitsOffDropsTheSummaryAndTheLimitsCardTogether() =>
        Assert.Equal(
            [OverviewCard.Chart, OverviewCard.Trace, OverviewCard.Models, OverviewCard.Streaks],
            OverviewCards.Visible("", limitsEnabled: false));

    [Fact]
    public void LimitsGateIsTheOneAnswerEverySurfaceReads()
    {
        Assert.True(OverviewCards.ShowsLimitsCard(true));
        Assert.False(OverviewCards.ShowsLimitsCard(false));
    }

    private static SettingsStore StoreWith(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "tb-ov-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    [Fact]
    public void AbsentLimitsKeyMeansEnabled()
    {
        var store = StoreWith("{}");
        Assert.True(OverviewCards.LimitsEnabled(store));
        Assert.Equal(AllInOrder, OverviewCards.Visible(store));
    }

    [Fact]
    public void StoreOverloadReadsBothPersistedKeys()
    {
        var store = StoreWith("""{"tokenbar.limits.enabled": false, "tokenbar.overview.hidden": "models"}""");
        Assert.False(OverviewCards.LimitsEnabled(store));
        Assert.Equal(
            [OverviewCard.Chart, OverviewCard.Trace, OverviewCard.Streaks],
            OverviewCards.Visible(store));
    }

    // The Settings section's strings go through Localized(); a key missing
    // from the shipped tables renders English in zh. Against the shipped files,
    // not a fixture (same reason as UsageAttributionPageTests).
    [Fact]
    public void SettingsStringsHaveBothTranslations()
    {
        string[] keys =
        [
            "Overview cards",
            "Choose which cards Overview shows. The usage chart always stays.",
            "Show Agent limits card",
            "Off hides the quota card on Overview and on every client tab.",
            .. OverviewCards.Toggleable.Select(OverviewCards.Label),
        ];
        foreach (var file in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            foreach (var key in keys)
            {
                Assert.True(table.ContainsKey(key), $"{file}: {key}");
            }
        }
    }
}
