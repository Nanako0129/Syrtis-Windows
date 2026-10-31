using TokenBar.App;

namespace TokenBar.Core.Tests;

/// <summary>
/// The Accounts settings page keeps one short line per section and moves every
/// other explanation behind the info button. SettingsWindow compiles under no
/// test project, so these pin the copy the page is built from: nothing the
/// sections used to say inline was dropped, and the privacy and storage facts
/// are still in the info text.
/// </summary>
public class AccountsPageCopyTests
{
    [Fact]
    public void EachSectionKeepsOneShortLine()
    {
        foreach (var summary in new[] { ClaudeAccountsCopy.Summary, AntigravityAccountsCopy.Summary })
        {
            Assert.DoesNotContain('\n', summary);
            Assert.True(summary.Length <= 60, summary);
        }

        Assert.DoesNotContain(ClaudeAccountsCopy.Summary, ClaudeAccountsCopy.Info);
        Assert.DoesNotContain(AntigravityAccountsCopy.Summary, AntigravityAccountsCopy.Info);
    }

    // The five hints each section showed inline before the move.
    [Fact]
    public void ClaudeInfoHoldsEveryFormerInlineHint() =>
        Assert.Equal(
            [ClaudeAccountsCopy.HowTo, ClaudeAccountsCopy.Reads, ClaudeAccountsCopy.Transport,
                ClaudeAccountsCopy.Locations, ClaudeAccountsCopy.RemoveMeans],
            ClaudeAccountsCopy.Info);

    // MergeOnlyUntilRefresh is a conditional notice (automatic capture off), so
    // it stays inline and is not part of the info text.
    [Fact]
    public void AntigravityInfoHoldsEveryFormerUnconditionalHint()
    {
        Assert.Equal(
            [AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.HowTo, AntigravityAccountsCopy.Reads,
                AntigravityAccountsCopy.RemoveMeans, AntigravityAccountsCopy.TwiceFromIde],
            AntigravityAccountsCopy.Info);
        Assert.DoesNotContain(AntigravityAccountsCopy.MergeOnlyUntilRefresh, AntigravityAccountsCopy.Info);
    }

    // Where a credential goes, where it is kept, and what Remove does must stay
    // reachable from the page.
    [Fact]
    public void PrivacyAndStorageFactsStayInTheInfo()
    {
        var claude = string.Join('\n', ClaudeAccountsCopy.Info);
        Assert.Contains("api.anthropic.com", claude);
        Assert.Contains("nothing is revoked at Anthropic", claude);

        var antigravity = string.Join('\n', AntigravityAccountsCopy.Info);
        Assert.Contains("Windows Credential Manager", antigravity);
        Assert.Contains("Google's token service", antigravity);
        Assert.Contains("deletes only the copy on this PC", antigravity);
        Assert.Contains("revoke access", antigravity);
    }

    // The translation tests walk All(); the summaries and info must be in it.
    [Fact]
    public void TranslationCheckCoversTheNewStrings()
    {
        Assert.Subset(ClaudeAccountsCopy.All().ToHashSet(), ClaudeAccountsCopy.Info.Append(ClaudeAccountsCopy.Summary).ToHashSet());
        Assert.Subset(AntigravityAccountsCopy.All().ToHashSet(), AntigravityAccountsCopy.Info.Append(AntigravityAccountsCopy.Summary).ToHashSet());
        Assert.Contains(SettingsCopy.AccountsPage, SettingsCopy.All);
        Assert.Contains(SettingsCopy.MoreInfo, SettingsCopy.All);
    }
}
