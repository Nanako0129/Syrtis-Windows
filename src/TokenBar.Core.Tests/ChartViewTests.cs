using TokenBar.App;

namespace TokenBar.Core.Tests;

// macOS ChartView (UsageChartCard.swift:28-60): same raw values, same cycle.
public class ChartViewTests
{
    [Theory]
    [InlineData("heat", ChartView.Heatmap)]
    [InlineData("3d", ChartView.ThreeD)]
    [InlineData("2d", ChartView.Bars)]
    [InlineData("", ChartView.Bars)]
    [InlineData("garbage", ChartView.Bars)]
    [InlineData(null, ChartView.Bars)]
    public void ParseFollowsMacOsRawValues(string? raw, ChartView expected) =>
        Assert.Equal(expected, ChartViews.Parse(raw));

    [Fact]
    public void RawRoundTripsEveryView()
    {
        foreach (var view in Enum.GetValues<ChartView>())
        {
            Assert.Equal(view, ChartViews.Parse(view.Raw()));
        }

        Assert.Equal("heat", ChartView.Heatmap.Raw());
    }

    [Fact]
    public void NextCyclesBarsHeatmapThreeDBars()
    {
        Assert.Equal(ChartView.Heatmap, ChartView.Bars.Next());
        Assert.Equal(ChartView.ThreeD, ChartView.Heatmap.Next());
        Assert.Equal(ChartView.Bars, ChartView.ThreeD.Next());
    }

    [Fact]
    public void NextVisitsEveryViewBeforeRepeating()
    {
        var seen = new HashSet<ChartView>();
        var v = ChartView.Bars;
        for (var i = 0; i < Enum.GetValues<ChartView>().Length; i++)
        {
            seen.Add(v);
            v = v.Next();
        }

        Assert.Equal(Enum.GetValues<ChartView>().Length, seen.Count);
        Assert.Equal(ChartView.Bars, v);
    }

    // The picker labels go through Localized(); a key missing from the shipped
    // tables would render English in zh builds with no failure anywhere.
    [Fact]
    public void PickerLabelsAreTranslated()
    {
        foreach (var file in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            foreach (var key in new[] { "Bars", "Heatmap" })
            {
                Assert.True(table.ContainsKey(key), $"{file}: {key}");
            }
        }
    }
}
