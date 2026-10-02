using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's ModelColors block.
public class ModelColorsTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6", "anthropic")]
    [InlineData("gpt-5.5", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("gemini-3-pro", "google")]
    [InlineData("auto", "cursor")]
    [InlineData("mystery", "unknown")]
    public void ProviderFromModel(string model, string expected) =>
        Assert.Equal(expected, ModelColors.ProviderFromModel(model));

    [Fact]
    public void MergedProviderIdFallsBackToModel() =>
        Assert.Equal("openai", ModelColors.ProviderColorKey("litellm, openai", "gpt-5.5"));

    [Fact]
    public void ProviderIdAliasMatches() =>
        Assert.Equal("anthropic", ModelColors.ProviderColorKey("Anthropic", "whatever"));

    [Fact]
    public void ShadeRank0IsBase() =>
        Assert.Equal("#da7756", ModelColors.ShadeFromBase("#da7756", 0));

    [Fact]
    public void ShadeRank1Lerps() =>
        // factor 0.11: 59→81 (0x51), 130→144 (0x90), 246→247 (0xf7)
        Assert.Equal("#5190f7", ModelColors.ShadeFromBase("#3b82f6", 1));

    [Fact]
    public void CostRankingDrivesShades()
    {
        var map = new ModelColorMap(
        [
            ("anthropic", "claude-opus-4-8", 100.0),
            ("anthropic", "claude-haiku-4-5", 1.0),
        ]);

        Assert.Equal("#da7756", map.Color("anthropic", "claude-opus-4-8")); // priciest = base
        Assert.NotEqual("#da7756", map.Color("anthropic", "claude-haiku-4-5")); // cheaper = tinted
        Assert.Equal("#06b6d4", map.Color(null, "gemini-3-pro")); // unseen → provider base
    }

    // Grouped on both sides: a table built from a raw -build entry is found by
    // the grouped and the raw lookup alike, and its cost ranks with the group.
    // The group sits at rank 1, so its shade differs from the rank-0 base an
    // unmatched lookup falls back to.
    [Fact]
    public void GrokBuildEntriesAndLookupsMeetOnTheGroupedModel()
    {
        var map = new ModelColorMap(
        [
            ("xai", "grok-5", 1000.0),
            ("xai", "grok-4.6-build", 60.0),
            ("xai", "grok-4.6", 60.0),
            ("xai", "grok-4.5", 100.0),
        ]);

        var grouped = map.Color("xai", "grok-4.6");
        Assert.Equal(grouped, map.Color("xai", "grok-4.6-build"));
        // 60 + 60 = 120 outranks grok-4.5's 100: rank 1, not rank 2.
        Assert.Equal(ModelColors.ShadeFromBase(map.Color("xai", "grok-5"), 1), grouped);
        Assert.NotEqual(map.Color("xai", "grok-unseen"), grouped);
    }

    [Fact]
    public void CheckingRankingUsesLexicalModelOrder()
    {
        var map = new ModelColorMap(
        [
            ("anthropic", "model-z", 100.0),
            ("anthropic", "model-a", 1.0),
        ], costAuthoritative: false);

        Assert.Equal("#da7756", map.Color("anthropic", "model-a"));
        Assert.NotEqual("#da7756", map.Color("anthropic", "model-z"));
    }
}
