using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Syrtis-Windows #247: every same-query generation set Models = null, so the
// Models lens blanked until the new report landed. DashboardModel.cs is
// compiled by no test project, so the revoke is exercised through this seam.
public class SameQueryRevokeTests
{
    [Fact]
    public void SameQueryGenerationKeepsPreviousModelsButRevokesCostAuthority()
    {
        var report = new ModelReport([], 0, 0, 0, 0, 0, 0);

        var (models, authoritative) = SameQueryRevoke.Apply(report);

        Assert.Same(report, models);
        Assert.False(authoritative);
    }

    [Fact]
    public void NoPreviousModelsStaysNull()
    {
        Assert.Null(SameQueryRevoke.Apply(null).Models);
    }
}
