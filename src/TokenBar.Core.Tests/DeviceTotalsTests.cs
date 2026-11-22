using System.Globalization;
using TokenBar.Core;
using TokenBar.Interop;
using C = TokenBar.Core.DeviceOverlayCopy;
using static TokenBar.Core.Tests.DeviceOverlayTestSupport;

namespace TokenBar.Core.Tests;

/// <summary>The "Per device" card's rows and detail line against macOS
/// <c>DeviceTotals.rows</c> and <c>DeviceTotalsCard.detail</c>
/// (DeviceTotalsCard.swift :9-45, :115-129; SelfTest.swift :24575-24605,
/// :24980-24986).</summary>
public class DeviceTotalsTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public DeviceTotalsTests() => Load("en");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        Load("en");
    }

    // The page of SelfTest.swift :24575-24582: this PC, a fresh device, and
    // one that never answered (no totals).
    private static readonly RemoteDevice[] Page =
    [
        new(Id: "local", State: "local", Totals:
            [new("claude", 3, 100, 1.5), new("codex", 1, 20, 0.25)]),
        new(Id: "aaaaaaaaaaaaaaaa", State: "fresh", Label: "10.0.0.2:47000", LastSuccessAt: T0, Totals:
            [new("claude", 2, 10, 0.5), new("codex", 4, 40, 0.75)]),
        new(Id: "bbbbbbbbbbbbbbbb", State: "unavailable", Label: "10.0.0.3:47000", Reason: "unreachable"),
    ];

    // :24590-24598. macOS adds the rows up against UsageStats(payload) and the
    // payload's own message sum; no payload exists at this layer, so the
    // expected totals are the sums of the same fixture, typed out.
    [Theory]
    [InlineData(new[] { "claude", "codex" }, 170L, 3.0, 10L)]
    [InlineData(new[] { "codex" }, 60L, 1.0, 5L)]
    [InlineData(new[] { "claude" }, 110L, 2.0, 5L)]
    public void TheDeviceRowsAddUpToTheCombinedTokensCostAndMessages(
        string[] clients, long tokens, double cost, long messages)
    {
        var rows = DeviceTotals.Rows(Page, clients);
        Assert.Equal(tokens, rows.Where(r => r.Tokens is not null).Sum(r => r.Tokens!.Value));
        Assert.Equal(cost, rows.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value), 9);
        Assert.Equal(messages, rows.Where(r => r.Messages is not null).Sum(r => r.Messages!.Value));
    }

    [Fact] // :24599-24607
    public void AHiddenClientIsLeftOutOfEveryRowAndTheFiguresAreNullWithNoMergedPart()
    {
        var rows = DeviceTotals.Rows(Page, ["claude"]);
        Assert.Equal<long?>([100, 10, null], rows.Select(r => r.Tokens));
        Assert.Equal<long?>([3, 2, null], rows.Select(r => r.Messages));
        Assert.True(rows[0].IsLocal);
        Assert.Null(rows[0].UpdatedAt);
        Assert.Equal(T0, rows[1].UpdatedAt);
        Assert.Null(rows[2].Tokens);
        Assert.Equal("10.0.0.2:47000", rows[1].Name);
    }

    [Fact] // (mutation b) a hidden client never counts: codex alone must not include claude's part
    public void AHiddenClientDoesNotChangeAnyRowOfTheOtherClient()
    {
        var rows = DeviceTotals.Rows(Page, ["codex"]);
        Assert.Equal<long?>([20, 40, null], rows.Select(r => r.Tokens));
        Assert.Equal<long?>([1, 4, null], rows.Select(r => r.Messages));
        Assert.Equal<double>([0.25, 0.75], rows.Take(2).Select(r => r.Cost!.Value));
    }

    [Fact] // :24267-24271
    public void ThePerDeviceCardNamesTheDeviceElseHostPortAndThisPcByItsFixedWord()
    {
        var rows = DeviceTotals.Rows(
            [Row("fresh", label: "Studio Mac"), Row("fresh", id: "bbbbbbbbbbbbbbbb", label: Address)],
            ["claude"]);
        Assert.Equal<string>(["Studio Mac", Address], rows.Select(r => r.Name));
        Assert.Equal(C.ThisPc.Localized(), DeviceTotals.Rows(Page, ["claude"])[0].Name);
        // the newest real name per id wins over the row's own label (DeviceTotalsCard.swift :26-27)
        var learned = new Dictionary<string, string> { ["aaaaaaaaaaaaaaaa"] = "New" };
        Assert.Equal("New", DeviceTotals.Rows(Page, ["claude"], learned)[1].Name);
    }

    [Fact]
    public void ThisPcIsNamedThisPcInZhHantToo()
    {
        Load("zh-Hant");
        Assert.Equal("這台電腦", DeviceTotals.Rows(Page, ["claude"])[0].Name);
    }

    [Fact] // :24980-24986 (screen 2): grouped like the other rows' counts
    public void ThePerDeviceMessageCountIsGrouped()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var row = new DeviceTotalsRow("x", "n", false, 124_634, 5, 1, null);
        var detail = DeviceTotals.Detail(row, DateTimeOffset.UnixEpoch);
        Assert.Contains(124_634L.ToString("N0"), detail);
        Assert.NotEqual("124634", 124_634L.ToString("N0"));
        Assert.Equal("124,634 msgs · 5 tokens", detail);
    }

    [Fact]
    public void TheDetailLineIsLiteralInEnglishAndZhHant()
    {
        var row = new DeviceTotalsRow("a", "n", false, 12, 1_500, 1, T0);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("12 msgs · 1.5K tokens · Updated 5m ago", DeviceTotals.Detail(row, Now));
        Assert.Equal("12 msgs · 1.5K tokens", DeviceTotals.Detail(row, Now, showsUpdated: false));
        Load("zh-Hant");
        Assert.Equal("12 則訊息 · 1.5K tokens · 更新於 5m ago", DeviceTotals.Detail(row, Now));
    }

    [Fact] // :115-129 the dash branch; this PC has no update time (:24605)
    public void ARowWithNoMergedPartShowsADashAndThisPcShowsNoUpdateTime()
    {
        var rows = DeviceTotals.Rows(Page, ["claude"]);
        Assert.Equal("—", DeviceTotals.Detail(rows[2], Now, showsUpdated: false));
        Assert.DoesNotContain("Updated", DeviceTotals.Detail(rows[0], Now));
        Assert.Contains("Updated 5m ago", DeviceTotals.Detail(rows[1], Now));
    }

    [Fact] // :47-51
    public void AdditionSaturatesAtTheBoundOfTheAddendsSign()
    {
        Assert.Equal(long.MaxValue, DeviceTotals.SaturatingAdd(long.MaxValue, 1));
        Assert.Equal(long.MinValue, DeviceTotals.SaturatingAdd(long.MinValue, -1));
        Assert.Equal(3, DeviceTotals.SaturatingAdd(1, 2));
        Assert.Equal(-2, DeviceTotals.SaturatingAdd(-5, 3));
        var rows = DeviceTotals.Rows(
            [new(Id: "aaaaaaaaaaaaaaaa", State: "fresh", Totals:
                [new("claude", 1, long.MaxValue, 0), new("codex", 1, 5, 0)])], ["claude", "codex"]);
        Assert.Equal(long.MaxValue, rows[0].Tokens);
    }
}
