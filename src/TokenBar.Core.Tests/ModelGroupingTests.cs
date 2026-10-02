using System.Text.Json;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>The display-grouping case table shared with macOS
/// (<c>Fixtures/model-grouping-cases.json</c>) and checked against the engine
/// by tb_core_ffi's <c>model_grouping_cases_match_the_engine</c>.</summary>
public class ModelGroupingTests
{
    [Fact]
    public void SharedCaseTableMatchesGroupId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "model-grouping-cases.json");
        using var table = JsonDocument.Parse(File.ReadAllText(path));
        var cases = table.RootElement.GetProperty("cases").EnumerateArray().ToList();

        // Control: an empty or truncated table must not pass vacuously.
        Assert.True(cases.Count >= 17, $"case table has {cases.Count} rows, expected >= 17");
        foreach (var row in cases)
        {
            var input = row[0].GetString()!;
            var expected = row[1].GetString()!;
            Assert.True(
                ModelGrouping.GroupId(input) == expected,
                $"GroupId({input}) == {expected}, got {ModelGrouping.GroupId(input)}");
        }
    }
}
