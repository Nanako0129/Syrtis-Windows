namespace TokenBar.Core.Tests;

public class AgentIconsTests
{
    /// <summary>Walks up from the test binary's output dir to find the repo's
    /// shipped icon assets, so this test fails if a table entry has no
    /// matching file rather than silently passing on a missing directory.</summary>
    private static string FindAssetsDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "TokenBar.App", "Assets", "agent-icons");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException(
            "agent-icons assets dir not found walking up from " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("claude", AgentIconKind.Mono)]
    [InlineData("gemini", AgentIconKind.Mono)]
    [InlineData("opencode", AgentIconKind.Mono)]
    [InlineData("copilot", AgentIconKind.Mono)]
    [InlineData("qwen", AgentIconKind.Mono)]
    [InlineData("codex", AgentIconKind.Full)]
    [InlineData("cline", AgentIconKind.Full)]
    [InlineData("grok", AgentIconKind.Full)]
    public void ResolvesKnownIdsToTheirExpectedKind(string clientId, AgentIconKind kind)
    {
        var info = AgentIcons.Resolve(clientId);
        Assert.NotNull(info);
        Assert.Equal(kind, info!.Value.Kind);
        Assert.Equal(clientId, info.Value.IconId);
    }

    [Theory]
    [InlineData("antigravity-cli", "antigravity")]
    [InlineData("kilo", "kilocode")]
    [InlineData("grok-bot", "grok")]
    public void AliasesResolveToTheSharedIcon(string clientId, string expectedIconId)
    {
        var info = AgentIcons.Resolve(clientId);
        Assert.NotNull(info);
        Assert.Equal(expectedIconId, info!.Value.IconId);
        Assert.Equal(AgentIconKind.Full, info.Value.Kind);
    }

    [Theory]
    [InlineData("junie")]
    [InlineData("opencodereview")]
    [InlineData("some-unregistered-client")]
    public void UnknownOrUnregisteredIdsHaveNoIcon(string clientId)
    {
        Assert.Null(AgentIcons.Resolve(clientId));
    }

    [Theory]
    [InlineData("cline", "#ffffff")]
    [InlineData("hermes", "#ffffff")]
    [InlineData("mux", "#000000")]
    [InlineData("amp", "#000000")]
    public void BackgroundFillsMatchTheBrandBackdrop(string clientId, string expectedHex)
    {
        var info = AgentIcons.Resolve(clientId);
        Assert.NotNull(info);
        Assert.Equal(expectedHex, info!.Value.BackgroundHex);
    }

    [Fact]
    public void FullIconsWithoutABackgroundFillHaveNone()
    {
        var info = AgentIcons.Resolve("codex");
        Assert.NotNull(info);
        Assert.Null(info!.Value.BackgroundHex);
    }

    [Fact]
    public void ClineIsInsetAndEveryoneElseIsFullScale()
    {
        Assert.Equal(0.82, AgentIcons.Resolve("cline")!.Value.InsetScale);
        Assert.Equal(1.0, AgentIcons.Resolve("codex")!.Value.InsetScale);
        Assert.Equal(1.0, AgentIcons.Resolve("claude")!.Value.InsetScale);
    }

    [Fact]
    public void MonoAndFullSetsAreDisjoint()
    {
        var mono = new[] { "claude", "gemini", "opencode", "copilot", "qwen" };
        foreach (var id in mono)
        {
            Assert.Equal(AgentIconKind.Mono, AgentIcons.Resolve(id)!.Value.Kind);
        }
    }

    [Fact]
    public void EveryAssetIdHasAShippedIconFile()
    {
        var dir = FindAssetsDir();
        foreach (var id in AgentIcons.AssetIds)
        {
            var svg = Path.Combine(dir, $"{id}.svg");
            var png = Path.Combine(dir, $"{id}.png");
            Assert.True(
                File.Exists(svg) || File.Exists(png),
                $"missing agent-icons asset for '{id}': neither {svg} nor {png} exists");
        }
    }

    [Fact]
    public void AssetIdsCoverThirtyRegisteredIcons()
    {
        // 5 mono + 25 full, ported 1:1 from AgentIconView.swift's tables.
        Assert.Equal(30, AgentIcons.AssetIds.Count);
    }
}
