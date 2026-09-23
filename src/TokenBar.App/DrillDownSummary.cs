using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The trailing summary line on a Daily or Monthly row — messages,
/// optional turn count, tokens, cost — plus the one scope line above the list.
///
/// Daily and Monthly each built this inline and the two blocks were byte
/// identical; BuildDaily/BuildMonthly already share ModelStripeRow so the
/// drill-downs cannot drift, and this is the half that still could. It also
/// puts the copy somewhere a test can reach: DashboardView.xaml.cs is WinUI
/// and no test project compiles it.
///
/// Lives in App because it reads the cost-authority policy, and is pulled into
/// TokenBar.Core.Tests by &lt;Compile Include&gt; the way CostSurfaceProjection
/// and AppViews are.</summary>
public static class DrillDownSummary
{
    public static string Text(DailyRow row, bool authoritative) =>
        Compose(row.Messages, row.Turns, row.Tokens, row.Cost, authoritative);

    public static string Text(MonthlyRow row, bool authoritative) =>
        Compose(row.Messages, row.Turns, row.Tokens, row.Cost, authoritative);

    private static string Compose(
        long messages,
        long? turns,
        long tokens,
        double cost,
        bool authoritative)
    {
        var summary = "{0} msgs".Localized(messages);
        if (turns is { } turnCount)
        {
            summary += " · " + "{0} turns".Localized(turnCount);
        }

        return summary + " · " + Format.CompactTokens(tokens)
            + " · " + CostSurfaceProjection.CostText(tokens, cost, authoritative);
    }

    /// <summary>Which clients the turn counts cover, said once above the list
    /// rather than on every row — as macOS does with its card subtitle
    /// (DailyView.swift <c>TurnCountBuckets.scope</c>). It describes the lens's
    /// selection, <see cref="DailyRows.TurnScope"/>, not any one row's clients.
    /// Null when no selected client has turn counts, so nothing is drawn.
    ///
    /// The two-name arm takes both names as arguments rather than joining them
    /// outside the key, because the separator is not " + " in every language —
    /// macOS renders 、 in zh-Hant. TurnScope holds at most two ids (the
    /// supported set is codex and claude); a third would need a new key here
    /// and in macOS, which drops the third name the same way.</summary>
    public static string? ScopeLine(IReadOnlyList<string> turnClients) =>
        turnClients.Count switch
        {
            0 => null,
            1 => "Turns · {0} only".Localized(ClientRegistry.ShortName(turnClients[0])),
            _ => "Turns · {0} + {1} only".Localized(
                ClientRegistry.ShortName(turnClients[0]),
                ClientRegistry.ShortName(turnClients[1])),
        };
}
