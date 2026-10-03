using System.Text.Json;
using TokenBar.App;

namespace TokenBar.Core.Tests;

public class SettingsCopyTests
{
    // About lists every project Syrtis draws on, as macOS does
    // (SettingsPanel.swift:1188); Windows once credited only the first two.
    [Theory]
    [InlineData("tokcat by handlecusion")]
    [InlineData("tokscale by Junho Yeo")]
    [InlineData("CodexBar by Peter Steinberger")]
    [InlineData("RunCat by Takuto Nakamura")]
    public void AboutCreditsEveryUpstreamProject(string credit) =>
        Assert.Contains(credit, SettingsCopy.About);

    // Without a table entry the copy silently renders in English for a
    // Chinese UI.
    [Theory]
    [InlineData("strings-zh-Hant.json")]
    [InlineData("strings-zh-Hans.json")]
    public void EverySettingsCopyStringIsTranslated(string table)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;

        foreach (var copy in SettingsCopy.All)
        {
            Assert.True(entries.ContainsKey(copy), $"{table}: {copy}");
        }
    }

    // The gauge hint quotes the "Color on warning only" option by name, so
    // each translation must quote that option's own translation.
    [Theory]
    [InlineData("strings-zh-Hant.json")]
    [InlineData("strings-zh-Hans.json")]
    public void GaugeHintQuotesTheTranslatedOptionLabel(string table)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;

        Assert.Contains(
            entries["Color on warning only"], entries[SettingsCopy.GaugeColoringHint]);
    }

    [Theory]
    [InlineData("cat", "Spinning cat")]
    [InlineData("parrot", "Party parrot")]
    public void AnimatedIconStylesUseTheMacLabels(string raw, string label) =>
        Assert.Equal(label, TrayIconStyles.Options.Single(o => o.Raw == raw).Label);
}
