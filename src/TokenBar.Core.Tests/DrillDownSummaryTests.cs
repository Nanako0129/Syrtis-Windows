using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// The summary line the Daily and Monthly lenses put on each row. Both lenses
// built it inline and identically before it moved here; DashboardView.xaml.cs
// is WinUI and no test project compiles it, so this is also the only place the
// copy can be asserted at all.
public class DrillDownSummaryTests
{
    // Localization.Load installs one process-wide table. English assertions in
    // this class used to hold only because every test that loads zh-Hant
    // restores it in a finally — a convention every future test would have to
    // remember. Establishing the precondition here makes it a mechanism: xUnit
    // constructs the class before each test.
    public DrillDownSummaryTests() => Localization.Load("en", AppContext.BaseDirectory);
    private static DailyRow Day(
        long messages = 12,
        long? turns = null,
        IReadOnlyList<string>? turnClients = null,
        long tokens = 12_345,
        double cost = 5.2) =>
        new("2026-06-10", tokens, cost, messages, turns, turnClients ?? [], []);

    private static MonthlyRow Month(
        long messages = 12,
        long? turns = null,
        IReadOnlyList<string>? turnClients = null,
        long tokens = 12_345,
        double cost = 5.2) =>
        new("2026-06", tokens, cost, messages, turns, turnClients ?? [], []);

    [Fact]
    public void NoTurnsIsMessagesTokensCost() =>
        Assert.Equal("12 msgs · 12.3K · $5.20", DrillDownSummary.Text(Day(), true));

    // The scope is not on the row: it is said once above the list, so a row
    // carries only the count even when it knows which clients it covers.
    [Fact]
    public void TurnsAddCountWithoutScope() =>
        Assert.Equal(
            "12 msgs · 40 turns · 12.3K · $5.20",
            DrillDownSummary.Text(Day(turns: 40, turnClients: ["codex", "claude"]), true));

    [Fact]
    public void ScopeLineNamesOneClient() =>
        Assert.Equal("Turns · Codex only", DrillDownSummary.ScopeLine(["codex"]));

    // Both names are arguments to one key rather than joined outside it: the
    // separator is not " + " in every language.
    [Fact]
    public void ScopeLineNamesBoth() =>
        Assert.Equal(
            "Turns · Codex + Claude only", DrillDownSummary.ScopeLine(["codex", "claude"]));

    // No selected client has turn counts: no line at all, not an empty one.
    [Fact]
    public void ScopeLineIsAbsentWithoutTurnClients() =>
        Assert.Null(DrillDownSummary.ScopeLine([]));

    // The line describes the selection, so a client selected but idle on a
    // given day is still named, and unsupported clients never are.
    [Fact]
    public void TurnScopeIsTheSupportedPartOfTheSelectionInOrder() =>
        Assert.Equal(
            ["claude", "codex"],
            DailyRows.TurnScope(["gemini", "claude", "codex", "claude"]));

    [Fact]
    public void UnauthoritativeCostShowsCheckingInstead() =>
        Assert.Equal("12 msgs · 12.3K · Checking", DrillDownSummary.Text(Day(), false));

    // The two lenses shared this text by copy before; now they share the code.
    [Fact]
    public void MonthlyRendersIdenticallyToDaily()
    {
        Assert.Equal(
            DrillDownSummary.Text(Day(turns: 40, turnClients: ["codex"]), true),
            DrillDownSummary.Text(Month(turns: 40, turnClients: ["codex"]), true));
    }

    // ---- i18n ----------------------------------------------------------
    //
    // Exact strings per branch, not "differs from English". The line is
    // composed from up to four table entries and one resolving is enough to
    // make it differ, so an inequality check passes with three keys missing.

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
    public void EveryBranchIsTranslated() => InChinese(() =>
    {
        Assert.Equal("12 則訊息 · 12.3K · $5.20", DrillDownSummary.Text(Day(), true));

        Assert.Equal(
            "12 則訊息 · 40 互動 · 12.3K · $5.20",
            DrillDownSummary.Text(Day(turns: 40, turnClients: ["codex"]), true));

        Assert.Equal("互動 · 僅計 Codex", DrillDownSummary.ScopeLine(["codex"]));

        // 、 rather than " + " — the separator lives inside the key, matching
        // macOS's "Turns · %@ + %@ only" = "互動 · 僅計 %@、%@".
        Assert.Equal(
            "互動 · 僅計 Codex、Claude", DrillDownSummary.ScopeLine(["codex", "claude"]));

        Assert.Equal("12 則訊息 · 12.3K · 查詢中", DrillDownSummary.Text(Day(), false));
    });

    // Client names are brand names and stay English in every language.
    // ShortName drops the trailing form-factor word, so "Claude Code"
    // reaches the summary as "Claude".
    [Fact]
    public void ClientNamesAreNotTranslated() => InChinese(() =>
        Assert.Contains(
            "Claude",
            DrillDownSummary.ScopeLine(["claude"])));


    // A day or month whose usage all failed to price shows "—" beside its own
    // token count, not "$0.00" — the same rule as the per-model rows beneath
    // it, which the drill-down expands into. Authority is graph-level and was
    // true here, so it cannot be what says this bucket had no price.
    [Fact]
    public void AnUnpricedDayReadsAsUnpricedNotFree() =>
        Assert.Equal("12 msgs · 12.3K · —", DrillDownSummary.Text(Day(cost: 0), true));

    [Fact]
    public void AnUnpricedMonthReadsAsUnpricedNotFree() =>
        Assert.Equal("12 msgs · 12.3K · —", DrillDownSummary.Text(Month(cost: 0), true));
}
