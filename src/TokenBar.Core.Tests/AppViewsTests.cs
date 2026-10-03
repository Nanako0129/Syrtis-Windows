using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class AppViewsTests
{
    [Fact]
    public void OverviewAndModelsAreNotToggleable()
    {
        Assert.DoesNotContain(AppView.Overview, AppViews.Toggleable);
        Assert.DoesNotContain(AppView.Models, AppViews.Toggleable);
        Assert.Contains(AppView.Daily, AppViews.Toggleable);
        Assert.Contains(AppView.Agents, AppViews.Toggleable);
    }

    // Declaration order is the lens row's order. Pinned for the same reason
    // OverviewCards.RenderOrder is. (Ctrl+1..9 no longer select lenses: they
    // select the client tab row, as on macOS — see TabShortcuts.)
    [Fact]
    public void QuotaIsTheSecondLens()
    {
        Assert.Equal(AppView.Quota, Enum.GetValues<AppView>()[1]);
        Assert.Equal(AppView.Quota, AppViews.Visible(string.Empty)[1]);
        // Still second once an earlier-hidden lens is gone; only Overview
        // precedes it and Overview cannot be hidden.
        Assert.Equal(AppView.Quota, AppViews.Visible("daily,stats")[1]);
    }

    [Fact]
    public void HiddenLensLeavesTheRow()
    {
        var visible = AppViews.Visible("daily,agents");
        Assert.DoesNotContain(AppView.Daily, visible);
        Assert.DoesNotContain(AppView.Agents, visible);
        Assert.Contains(AppView.Hourly, visible);
        Assert.Contains(AppView.Overview, visible);
    }

    // A hand-edited settings file must not be able to remove the fallback
    // target: Effective() sends every hidden lens to Overview, so an Overview
    // that could itself be hidden would leave nowhere to land.
    [Fact]
    public void TamperedRawCannotHideTheFallbackTargets()
    {
        var visible = AppViews.Visible("overview,models,daily");
        Assert.Contains(AppView.Overview, visible);
        Assert.Contains(AppView.Models, visible);
        Assert.DoesNotContain(AppView.Daily, visible);

        Assert.Equal(AppView.Overview, AppViews.Effective(AppView.Overview, "overview"));
        Assert.Equal(AppView.Models, AppViews.Effective(AppView.Models, "models"));
    }

    [Theory]
    [InlineData("stats", AppView.Stats, AppView.Overview)]
    [InlineData("daily,stats", AppView.Stats, AppView.Overview)]
    [InlineData("daily", AppView.Stats, AppView.Stats)]
    [InlineData("", AppView.Agents, AppView.Agents)]
    public void EffectiveFallsBackOnlyWhenHidden(
        string hiddenRaw, AppView requested, AppView expected)
    {
        Assert.Equal(expected, AppViews.Effective(requested, hiddenRaw));
    }

    // Ids are what get persisted; macOS writes the lowercase enum names into
    // the same key, so a divergence here would silently split the format.
    [Fact]
    public void IdsAreLowercaseEnumNames()
    {
        Assert.Equal("hourly", AppViews.Id(AppView.Hourly));
        Assert.Equal("agents", AppViews.Id(AppView.Agents));
    }
}
