using TokenBar.App;
using TokenBar.Core;
using C = TokenBar.Core.DeviceOverlayCopy;

namespace TokenBar.Core.Tests;

/// <summary>Which pages a lens asks the devices for, and the clients argument
/// (DashboardModel.swift :3337-3358; SelfTest.swift :23852-23876), plus the
/// pure parts of <c>OverviewMerge</c> (OverviewView.swift :30-41;
/// SelfTest.swift :24946-24975).</summary>
public class DeviceOverlayRequestsTests : IDisposable
{
    public DeviceOverlayRequestsTests() => DeviceOverlayTestSupport.Load("en");

    public void Dispose() => DeviceOverlayTestSupport.Load("en");

    [Theory] // DashboardModel.swift :3337-3348
    [InlineData(AppView.Daily, new[] { DeviceOverlayKind.Graph })]
    [InlineData(AppView.Monthly, new[] { DeviceOverlayKind.Graph })]
    [InlineData(AppView.Overview, new[] { DeviceOverlayKind.Graph, DeviceOverlayKind.Models })]
    [InlineData(AppView.Stats, new[] { DeviceOverlayKind.Graph, DeviceOverlayKind.Models })]
    [InlineData(AppView.Models, new[] { DeviceOverlayKind.Models })]
    [InlineData(AppView.Hourly, new[] { DeviceOverlayKind.Hourly })]
    [InlineData(AppView.Agents, new[] { DeviceOverlayKind.Agents })]
    [InlineData(AppView.Quota, new DeviceOverlayKind[0])]
    public void EachLensAsksForItsPages(AppView view, DeviceOverlayKind[] expected) =>
        Assert.Equal<DeviceOverlayKind>(expected, DeviceOverlayRequests.KindsFor(view));

    [Fact]
    public void EveryLensIsCovered() =>
        Assert.All(Enum.GetValues<AppView>(), v => DeviceOverlayRequests.KindsFor(v));

    [Fact] // :23852-23876 (11), the pure part
    public void TheClientsArgumentIsNullWithNothingHiddenTheRegistryMinusTheHiddenOtherwiseTheSliceOnAClientTab()
    {
        var expectedHidden = ClientRegistry.AllIds
            .Where(id => !ClientRegistry.HiddenTabClients(new HashSet<string> { "claude" }).Contains(id))
            .ToList();
        Assert.Null(DeviceOverlayRequests.ClientsArgument(null, new HashSet<string>()));
        Assert.Equal<string>(expectedHidden, DeviceOverlayRequests.ClientsArgument(null, new HashSet<string> { "claude" }));
        Assert.DoesNotContain("claude", expectedHidden);
        Assert.NotEmpty(expectedHidden);
        Assert.Equal<string>(ClientRegistry.TabSlice("codex"),
            DeviceOverlayRequests.ClientsArgument("codex", new HashSet<string> { "claude" }));
    }

    [Fact] // :24946-24967 as a pure table of (saturated, keptLast)
    public void NoteLinesAreSaturatedThenKeptLast()
    {
        Assert.Empty(OverviewMerge.NoteLines(false, false));
        Assert.Equal<string>([C.KeptLast.Localized()], OverviewMerge.NoteLines(false, true));
        Assert.Equal<string>([C.Saturated.Localized()], OverviewMerge.NoteLines(true, false));
        Assert.Equal<string>([C.Saturated.Localized(), C.KeptLast.Localized()], OverviewMerge.NoteLines(true, true));
    }

    [Fact]
    public void NoteLinesAreLiteralInEnglishAndZhHant()
    {
        Assert.Equal<string>(["Some totals reached their limit and are capped."], OverviewMerge.NoteLines(true, false));
        Assert.Equal(
            ["Couldn't update from the selected devices. Showing the last data received."],
            OverviewMerge.NoteLines(false, true));
        DeviceOverlayTestSupport.Load("zh-Hant");
        Assert.Equal<string>(["部分總計已達上限，以上限顯示。"], OverviewMerge.NoteLines(true, false));
        Assert.Equal<string>(["無法從所選裝置更新，顯示的是最後收到的資料。"], OverviewMerge.NoteLines(false, true));
    }

    [Fact] // :24971-24975
    public void NoReportAndAFailedRequestShowsTheFailureAReportEvenEmptyShowsTheCard()
    {
        Assert.Equal(OverviewMerge.ModelsCardState.Failed, OverviewMerge.ModelsCard(false, true));
        Assert.Equal(OverviewMerge.ModelsCardState.Loading, OverviewMerge.ModelsCard(false, false));
        Assert.Equal(OverviewMerge.ModelsCardState.Card, OverviewMerge.ModelsCard(true, true));
        Assert.Equal(OverviewMerge.ModelsCardState.Card, OverviewMerge.ModelsCard(true, false));
    }
}
