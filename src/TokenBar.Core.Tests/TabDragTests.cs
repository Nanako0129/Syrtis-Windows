namespace TokenBar.Core.Tests;

// G5i: dragging a client tab, against macOS DashboardTabs.swift:124-178.
public class TabDragTests
{
    [Fact]
    public void DraggingRightDropsAfterAndLeftDropsBefore()
    {
        Assert.Equal("codex,claude,gemini",
            ClientRegistry.MoveTab("claude,codex,gemini", ["claude", "codex", "gemini"],
                ["claude", "codex", "gemini"], "claude", "codex"));
        Assert.Equal("gemini,claude,codex",
            ClientRegistry.MoveTab("claude,codex,gemini", ["claude", "codex", "gemini"],
                ["claude", "codex", "gemini"], "gemini", "claude"));
    }

    // A first drag with no saved order must not push a hidden tab to the end:
    // the order is completed with every present tab before the visible
    // subset is reordered (macOS completeOrder).
    [Fact]
    public void AHiddenTabKeepsItsSlotOnTheFirstDrag()
    {
        var order = ClientRegistry.MoveTab(
            "", ["claude", "codex", "gemini"], ["claude", "gemini"], "gemini", "claude");

        Assert.Equal("gemini,codex,claude", order);
    }

    [Fact]
    public void ALegacyMemberIdFoldsToItsTabInsteadOfDrifting()
    {
        var order = ClientRegistry.MoveTab(
            "antigravity-cli,claude,codex", ["antigravity", "claude", "codex"],
            ["antigravity", "claude", "codex"], "codex", "claude");

        Assert.Equal("antigravity,codex,claude", order);
    }

    [Theory]
    [InlineData("claude", "gemini", "gemini", 1)]
    [InlineData("gemini", "claude", "claude", -1)]
    [InlineData("claude", "gemini", "codex", 0)]
    [InlineData("claude", "claude", "claude", 0)]
    [InlineData(null, "codex", "codex", 0)]
    public void DropLineSitsOnTheEdgeTheTabWillLandOn(
        string? drag, string? over, string tab, int edge) =>
        Assert.Equal(edge,
            ClientRegistry.DropEdge(drag, over, tab, ["claude", "codex", "gemini"]));

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, true)]
    [InlineData(3, 3, true)]
    public void APressBecomesADragAtFourPixels(double dx, double dy, bool drag) =>
        Assert.Equal(drag, ClientRegistry.IsTabDrag(dx, dy));

    [Fact]
    public void PresentTabsIncludeQuotaOnlySourcesAndFoldRawIds() =>
        Assert.Equal(["claude", "copilot"],
            ClientRegistry.PresentTabs(["claude-code", "claude"], ["copilot"]));

    // A client tab's hover text is macOS's .help, translated as macOS does.
    [Theory]
    [InlineData("strings-zh-Hant.json", "拖曳可調整順序")]
    [InlineData("strings-zh-Hans.json", "拖动以调整顺序")]
    public void TabHoverTextIsTheMacDragHint(string table, string translation)
    {
        Assert.Equal("Drag to reorder", ClientRegistry.TabDragHint);
        var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;
        Assert.Equal(translation, entries[ClientRegistry.TabDragHint]);
    }
}
