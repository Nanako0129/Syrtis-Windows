using System.Text.Json;
using System.Text.RegularExpressions;
using TokenBar.App;
using Xunit;

namespace TokenBar.Core.Tests;

public class AppLanguageTests
{
    [Theory]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de", "en")]
    public void ExplicitChoiceResolvesToAShippedTable(string stored, string expected) =>
        Assert.Equal(expected, AppLanguage.Resolve(stored));

    // The notice is a standing state indicator, not a one-shot event: it must
    // read correctly when the page is rebuilt, and clear when the user selects
    // their way back to whatever this process actually loaded.
    [Theory]
    [InlineData("zh-Hant", "en", true)]
    [InlineData("en", "zh-Hant", true)]
    [InlineData("zh-TW", "en", true)]
    [InlineData("en", "en", false)]
    [InlineData("zh-Hant", "zh-Hant", false)]
    [InlineData("zh-HK", "zh-Hant", false)]
    [InlineData("zh-Hans", "en", true)]
    [InlineData("zh-Hans", "zh-Hans", false)]
    [InlineData("zh-CN", "zh-Hans", false)]
    [InlineData("fr", "en", false)]
    public void RelaunchIsNeededOnlyWhenTheResolvedTagDiffers(
        string stored, string activeTag, bool expected) =>
        Assert.Equal(expected, AppLanguage.NeedsRelaunch(stored, activeTag));

    [Fact]
    public void OptionsNameEachLanguageInItsOwnLanguage()
    {
        Assert.Equal("English", AppLanguage.Options.Single(o => o.Value == "en").Label);
        Assert.Equal("简体中文",
            AppLanguage.Options.Single(o => o.Value == "zh-Hans").Label);
        Assert.Equal("繁體中文",
            AppLanguage.Options.Single(o => o.Value == "zh-Hant").Label);
    }
}

public class StringsTableTests
{
    // Both shipped translation tables must cover exactly the same English
    // source keys — a key added for one language and forgotten for the other
    // would silently render as its English source only in the language it
    // was skipped for, which a reviewer scanning either file alone won't see.
    private static readonly string HantPath =
        Path.Combine(AppContext.BaseDirectory, "strings-zh-Hant.json");
    private static readonly string HansPath =
        Path.Combine(AppContext.BaseDirectory, "strings-zh-Hans.json");

    private static Dictionary<string, string> Load(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    [Fact]
    public void HansAndHantCoverTheSameKeySet()
    {
        var hant = Load(HantPath);
        var hans = Load(HansPath);

        Assert.Equal(hant.Keys.OrderBy(k => k), hans.Keys.OrderBy(k => k));
    }

    [Fact]
    public void EveryEntryInBothTablesIsNonEmpty()
    {
        foreach (var path in new[] { HantPath, HansPath })
        {
            foreach (var (key, value) in Load(path))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{path}: {key}");
            }
        }
    }

    // A translation that renumbers or drops a {n} placeholder crashes
    // string.Format at the call site instead of just reading oddly.
    [Fact]
    public void PlaceholderSetsMatchBetweenHansAndHantPerKey()
    {
        var hant = Load(HantPath);
        var hans = Load(HansPath);
        var placeholder = new Regex(@"\{\d+\}");

        foreach (var key in hant.Keys)
        {
            var hantPlaceholders = placeholder.Matches(hant[key]).Select(m => m.Value).OrderBy(x => x);
            var hansPlaceholders = placeholder.Matches(hans[key]).Select(m => m.Value).OrderBy(x => x);
            Assert.True(hantPlaceholders.SequenceEqual(hansPlaceholders), key);
        }
    }
}

public class LocalizationTests
{
    // The English source text is the key, so a call site that has been wrapped
    // before its translation exists renders exactly as it did before.
    [Fact]
    public void MissingTableRendersTheEnglishSource()
    {
        Localization.Load("zh-Hant", Path.Combine(Path.GetTempPath(), "no-such-dir"));
        Assert.Equal("Menu bar", "Menu bar".Localized());
    }

    [Fact]
    public void EnglishNeedsNoTable()
    {
        Localization.Load("en", Path.GetTempPath());
        Assert.Equal("Dashboard", "Dashboard".Localized());
        Assert.Equal("en", Localization.CurrentTag);
    }

    [Fact]
    public void MalformedTableFailsSoftToEnglish()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "strings-zh-Hant.json"), "{ not json");
        try
        {
            Localization.Load("zh-Hant", dir);
            Assert.Equal("Startup", "Startup".Localized());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // System.Text.Json deserializes a JSON null into a non-nullable string
    // value without complaint, so a hand-edited or generated table can carry
    // one. Reading .Length on it would crash the UI it was meant to render.
    [Fact]
    public void NullEntryFallsBackInsteadOfThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "strings-zh-Hant.json"),
            """{"Language":null,"Menu bar":"選單列"}""");
        try
        {
            Localization.Load("zh-Hant", dir);
            Assert.Equal("Language", "Language".Localized());
            Assert.Equal("選單列", "Menu bar".Localized());
        }
        finally
        {
            Localization.Load("en", dir);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PresentEntryIsUsedAndBlankEntryFallsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "strings-zh-Hant.json"),
            """{"Menu bar":"選單列","Dashboard":""}""");
        try
        {
            Localization.Load("zh-Hant", dir);
            Assert.Equal("選單列", "Menu bar".Localized());
            Assert.Equal("Dashboard", "Dashboard".Localized());
        }
        finally
        {
            Localization.Load("en", dir);
            Directory.Delete(dir, recursive: true);
        }
    }
}
