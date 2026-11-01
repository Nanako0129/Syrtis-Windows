using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// Which graph request the Models on screen were fetched for, and what that
/// permits (Syrtis-Windows #247). Pure so tests drive the same transitions
/// <c>DashboardModel</c> calls.
/// <para>
/// macOS keeps the displayed <c>modelReport</c> and its <c>ModelColorMap</c>
/// across a same-year refresh and replaces both only when the new report is
/// published (<c>publishModel</c>); a year change drops them
/// (<c>invalidateModel</c>). It has no cost-authority flag on the models, so
/// this port adds one: a kept report still shows (that is the fix), but its
/// costs, shares and warnings read "Checking" until a report fetched for the
/// current request lands. The rank basis (cost vs tokens) changes only when
/// Models first appear or a current report and an authoritative graph coexist.
/// </para>
/// </summary>
public readonly record struct ModelsLineage(
    ModelReport? Models,
    GraphRequestId? RequestId,
    GraphRequestId? ModelsRequestId,
    bool GraphAuthoritative,
    bool RankByCost)
{
    /// <summary>The shown Models were fetched for the snapshot's own request.</summary>
    public bool ModelsCurrent =>
        Models is not null && RequestId is { } r && ModelsRequestId == r;

    /// <summary>Model-surface costs, shares and warnings may be stated.</summary>
    public bool ModelsCostAuthoritative => GraphAuthoritative && ModelsCurrent;

    /// <summary>The request moves to another query, so the whole rendered
    /// snapshot (not only Models) must go.</summary>
    public static bool DropsSnapshot(GraphRequestId? previous, GraphRequestId next) =>
        previous is { } old && old.Query != next.Query;

    /// <summary>A new graph request begins on the same query (a query change
    /// never reaches here: <c>ApplyGraphRequestStart</c> drops the snapshot via
    /// <see cref="DropsSnapshot"/> first). Keeps the Models and the rank basis,
    /// revokes cost authority.</summary>
    public ModelsLineage Begin(GraphRequestId next) =>
        this with { RequestId = next, GraphAuthoritative = false };

    /// <summary>The graph for <paramref name="id"/> is accepted. A report that
    /// arrived early for this request is applied; a failed fetch leaves the old
    /// report un-promoted.</summary>
    public ModelsLineage Publish(GraphRequestId id, ModelReport? early, bool graphAuthoritative)
    {
        var next = early is null
            ? this
            : this with { Models = early, ModelsRequestId = id };
        next = next with { RequestId = id, GraphAuthoritative = graphAuthoritative };
        return next with { RankByCost = next.Rank(firstAppearance: early is not null && Models is null) };
    }

    /// <summary>The report for <paramref name="id"/> lands on the snapshot.</summary>
    public ModelsLineage Apply(GraphRequestId id, ModelReport report)
    {
        var next = this with { Models = report, ModelsRequestId = id };
        return next with { RankByCost = next.Rank(firstAppearance: Models is null) };
    }

    // The basis changes only when Models first appear (nothing to reorder), or
    // when a current report and an authoritative graph coexist. A report that
    // lands during a non-authoritative LocalFirst graph keeps the old basis, so
    // rows and colors move at most once, when the authoritative graph arrives.
    private bool Rank(bool firstAppearance) =>
        firstAppearance ? GraphAuthoritative
        : ModelsCurrent && GraphAuthoritative ? true
        : RankByCost;
}
