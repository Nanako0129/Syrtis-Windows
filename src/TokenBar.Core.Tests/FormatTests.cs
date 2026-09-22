using Xunit;

namespace TokenBar.Core.Tests;

// Format outputs must match the Swift Format.swift character for character
// (cross-checked by the Phase 3 fixture pass).
public class FormatTests
{
    // Localization.Load installs one process-wide table. English assertions in
    // this class used to hold only because every test that loads zh-Hant
    // restores it in a finally — a convention every future test would have to
    // remember. Establishing the precondition here makes it a mechanism: xUnit
    // constructs the class before each test.
    public FormatTests() => Localization.Load("en", AppContext.BaseDirectory);
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(100_000, "100K")]
    [InlineData(999_499, "999K")] // last value the K band keeps
    [InlineData(999_500, "1M")] // F0 would carry 999.5 to "1000K", so the unit is promoted
    [InlineData(999_950, "1M")] // matches Swift
    [InlineData(1_234_567, "1.2M")]
    [InlineData(1_250_000, "1.2M")] // exact binary half → round-half-even, like printf
    [InlineData(999_499_999, "999M")] // last value the M band keeps
    [InlineData(999_500_000, "1B")] // same carry, one tier up
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_500_000_000, "1.5B")]
    public void CompactTokensMatchesSwift(long count, string expected) =>
        Assert.Equal(expected, Format.CompactTokens(count));

    [Theory]
    [InlineData(0.0, "$0.00")]
    [InlineData(5.205, "$5.21")] // binary 5.205 ≈ 5.2050…0071 (above the half), printf rounds up — Swift-verified via the fixture cross-check
    [InlineData(-1.5, "$-1.50")] // sign inside, matching Swift's "$%.2f"
    [InlineData(4845.174, "$4845.17")]
    public void UsdMatchesSwift(double amount, string expected) =>
        Assert.Equal(expected, Format.Usd(amount));

    [Theory]
    [InlineData(0.0, "$0.00")] // a real zero total, not "below resolution"
    [InlineData(0.003, "<$0.01")] // priced, but below the format's half-cent resolution
    [InlineData(0.5, "$0.50")]
    public void UsdOrBelowCentSaysSmallRatherThanNothing(double amount, string expected) =>
        Assert.Equal(expected, Format.UsdOrBelowCent(amount));

    // -0.0 == 0.0 in IEEE comparison, but Usd alone formats it as "$-0.00" —
    // a distinct Fact because InlineData(-0.0, ...) collapses to a duplicate
    // of the 0.0 case above under xUnit's theory-data equality check.
    [Fact]
    public void UsdOrBelowCentCatchesNegativeZero() =>
        Assert.Equal("$0.00", Format.UsdOrBelowCent(-0.0));

    // Format.money(tokens:cost:) / Format.tokens(tokens:cost:) — ported from
    // Format.swift's own SelfTest expectations (item 1).
    [Theory]
    [InlineData(2_100_000, 0, "—")] // real usage, nothing priced — not "$0.00"
    [InlineData(0, 0, "$0.00")] // a real zero total keeps its zero
    [InlineData(2_100_000, 0.003, "<$0.01")] // priced below resolution
    public void MoneyDashesAPriceNobodyHas(long tokens, double cost, string expected) =>
        Assert.Equal(expected, Format.Money(tokens, cost));

    [Theory]
    [InlineData(0, 4.10, "—")] // cost-only source, no token count measured
    [InlineData(0, 0, "0")] // a real zero total keeps its zero
    [InlineData(2_100_000, 0, "2.1M")]
    public void TokensDashesACountNobodyMeasured(long tokens, double cost, string expected) =>
        Assert.Equal(expected, Format.Tokens(tokens, cost));

    [Theory]
    [InlineData("2026-06-10", "Jun 10")]
    [InlineData("2026-12-01", "Dec 1")]
    [InlineData("garbage", "garbage")]
    [InlineData("2026-13-01", "2026-13-01")]
    public void MonthDay(string iso, string expected) =>
        Assert.Equal(expected, Format.MonthDay(iso));

    [Theory]
    [InlineData("2026-06", "Jun 2026")]
    [InlineData("2026-01", "Jan 2026")]
    [InlineData("2026-12", "Dec 2026")]
    [InlineData("2026-13", "2026-13")]
    [InlineData("2026-06-10", "2026-06-10")]
    [InlineData("garbage", "garbage")]
    public void MonthYear(string ym, string expected) =>
        Assert.Equal(expected, Format.MonthYear(ym));

    [Fact]
    public void MmddSplits() => Assert.Equal("06/10", Format.Mmdd("2026-06-10"));

    [Fact]
    public void ExactTokensGroups() => Assert.Equal("1,234,567", Format.ExactTokens(1_234_567));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(300, "5m ago")]
    [InlineData(10_800, "3h ago")]
    [InlineData(172_800, "2d ago")]
    public void RelativeTime(long ageSecs, string expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
        var epoch = (ulong)(1_750_000_000 - ageSecs);
        Assert.Equal(expected, Format.RelativeTime(epoch, now));
    }

    // ---- i18n ----------------------------------------------------------
    //
    // Exact strings, not "differs from English": these lines are composed from
    // more than one table entry, and one entry resolving is enough to make the
    // whole line differ. Exact assertions also pin argument *position*, which
    // format.monthYear reverses.

    private static void InChinese(Action body)
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            body();
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void TranslatedMonthTemplatesTakeTheirFieldOrderFromTheTable() => InChinese(() =>
    {
        Assert.Equal("6月10日", Format.MonthDay("2026-06-10"));
        Assert.Equal("12月1日", Format.MonthDay("2026-12-01"));
        // format.monthYear is "{1}年{0}" — the arguments swap. Translating only
        // the month names would render "6月 2026".
        Assert.Equal("2026年6月", Format.MonthYear("2026-06"));
        Assert.Equal("2026年1月", Format.MonthYear("2026-01"));

        // Unparseable input still comes back untouched rather than being
        // guessed at, table or no table.
        Assert.Equal("garbage", Format.MonthDay("garbage"));
        Assert.Equal("2026-13", Format.MonthYear("2026-13"));
    });

    [Fact]
    public void EveryMonthNameIsTranslated() => InChinese(() =>
    {
        for (var month = 1; month <= 12; month++)
        {
            Assert.Equal(
                $"{month}月1日", Format.MonthDay($"2026-{month:D2}-01"));
        }
    });

    [Fact]
    public void TranslatedRelativeTimeKeepsChineseWordOrder() => InChinese(() =>
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
        string At(long ageSecs) =>
            Format.RelativeTime((ulong)(1_750_000_000 - ageSecs), now);

        Assert.Equal("剛剛", At(0));
        Assert.Equal("剛剛", At(59));
        Assert.Equal("5 分鐘前", At(300));
        Assert.Equal("3 小時前", At(10_800));
        Assert.Equal("2 天前", At(172_800));
    });


    // A multiple as a whole number: the warning only ever names ratios above
    // 50, where a fraction carries nothing.
    [Theory]
    [InlineData(308.4, "308")]
    [InlineData(50.6, "51")]
    public void CompactRatioIsAWholeNumber(double ratio, string expected) =>
        Assert.Equal(expected, Format.CompactRatio(ratio));
}
