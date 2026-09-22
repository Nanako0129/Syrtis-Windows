using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The trailing summary line on a Daily or Monthly row — messages,
/// optional turn count and scope, tokens, cost.
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
        Compose(row.Messages, row.Turns, row.TurnClients, row.Tokens, row.Cost, authoritative);

    public static string Text(MonthlyRow row, bool authoritative) =>
        Compose(row.Messages, row.Turns, row.TurnClients, row.Tokens, row.Cost, authoritative);

    private static string Compose(
        long messages,
        long? turns,
        IReadOnlyList<string> turnClients,
        long tokens,
        double cost,
        bool authoritative)
    {
        var summary = "{0} msgs".Localized(messages);
        if (turns is { } turnCount)
        {
            summary += " · " + "{0} turns".Localized(turnCount) + " · " + Scope(turnClients);
        }

        return summary + " · " + Format.CompactTokens(tokens)
            + " · " + CostSurfaceProjection.CostText(tokens, cost, authoritative);
    }

    /// <summary>Which clients the turn count covers.
    ///
    /// The two-name arm takes both names as arguments rather than joining them
    /// outside the key, because the separator is not " + " in every language —
    /// macOS renders 、 in zh-Hant. Two arguments is exhaustive, not a cap:
    /// DailyRows filters TurnClients through SupportedTurnClients ("codex",
    /// "claude") and de-duplicates, and MonthlyRows only folds ids that were
    /// already in a DailyRow, so the list holds at most two. A third supported
    /// turn client would need a new key here and in macOS's DailyView.swift,
    /// which drops the third name the same way.
    ///
    /// macOS keys this as "Turns · %@ only" because it renders the scope once
    /// as the card subtitle; here it follows the per-row turn count, where the
    /// prefix would repeat, so the prefix is dropped and the count stays its
    /// own key.</summary>
    private static string Scope(IReadOnlyList<string> turnClients) =>
        turnClients.Count switch
        {
            1 => "{0} only".Localized(ClientRegistry.ShortName(turnClients[0])),
            > 1 => "{0} + {1} only".Localized(
                ClientRegistry.ShortName(turnClients[0]),
                ClientRegistry.ShortName(turnClients[1])),
            // Defensive: DailyRows leaves Turns null when TurnClients is empty,
            // so production never reaches this. Kept because the arm it ports
            // was already here, and a directly-constructed row can still hit it.
            _ => "selected clients".Localized(),
        };
}
