using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>What a new same-query graph generation does to the snapshot it
/// replaces, before the new payloads land. Cost authority is revoked (the cost
/// surfaces show "Checking" and rank by tokens), but the previous
/// <see cref="ModelReport"/> stays: it answers the same query, so the Models
/// lens keeps its rows until the new report is applied instead of blanking on
/// every generation (macOS keeps it too). A different query never reaches this
/// path; the dashboard drops the whole snapshot for that.</summary>
public static class SameQueryRevoke
{
    public static (ModelReport? Models, bool CostAuthoritative) Apply(ModelReport? models) =>
        (models, false);
}
