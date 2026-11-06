using TokenBar.App;
using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>MA-43: the decisions behind the Claude and Antigravity sections
/// updating in place. The WinUI wiring (rows kept, focus moved) is device-only.</summary>
public class SectionRowsTests
{
    [Fact]
    public void KeysAreStableAndUniqueEvenForAnExactDuplicate()
    {
        Assert.Equal([@"D:\a", @"D:\b", "D:\\a\01", "D:\\a\02"], SectionRows.Keys([@"D:\a", @"D:\b", @"D:\a", @"D:\a"]));
        // A real folder named like a suffixed key does not collide with it.
        Assert.Equal(3, SectionRows.Keys([@"D:\a", @"D:\a", @"D:\a#1"]).Distinct().Count());
        Assert.Equal([@"D:\b"], SectionRows.Keys([@"D:\b"]));
    }

    [Theory]
    [InlineData("b", "a,c", "c")]      // the next surviving row
    [InlineData("c", "a,b", "b")]      // none after: the previous one
    [InlineData("b", "a", "a")]        // the next one is gone too: walk back
    public void AFocusedRowThatGoesHandsFocusToTheNearestSurvivor(string focused, string wanted, string expected)
    {
        var (move, key) = SectionRows.FocusHandoff(["a", "b", "c"], wanted.Split(','), focused);
        Assert.True(move);
        Assert.Equal(expected, key);
    }

    [Fact]
    public void TheLastRowGoingHandsFocusToTheSectionButton()
    {
        Assert.Equal((true, (string?)null), SectionRows.FocusHandoff(["a"], [], "a"));
    }

    [Fact]
    public void NoHandoffWhenNothingIsFocusedInARowOrItsRowStays()
    {
        Assert.Equal((false, (string?)null), SectionRows.FocusHandoff(["a", "b"], ["a"], null));
        Assert.Equal((false, (string?)null), SectionRows.FocusHandoff(["a", "b"], ["a", "b"], "a"));
        Assert.Equal((false, (string?)null), SectionRows.FocusHandoff(["a", "b"], ["a"], "zzz"));
    }

    [Fact]
    public void TheClaudeRemoveButtonIsNamedAfterItsFolderWithTheSharedTranslatedString()
    {
        Assert.Equal(AntigravityAccountsCopy.RemoveNamed, ClaudeAccountsCopy.RemoveNamed);
        Assert.Equal(@"Remove D:\work", string.Format(ClaudeAccountsCopy.RemoveNamed, @"D:\work"));
        Assert.Contains(ClaudeAccountsCopy.RemoveNamed, ClaudeAccountsCopy.All());
    }
}
