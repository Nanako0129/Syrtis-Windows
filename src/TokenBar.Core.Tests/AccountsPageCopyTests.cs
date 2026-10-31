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

    [Fact]
    public void AntigravityInfoHoldsEveryFormerUnconditionalHint()
    {
        Assert.Equal(
            [AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.HowTo, AntigravityAccountsCopy.Reads,
                AntigravityAccountsCopy.RemoveMeans, AntigravityAccountsCopy.TwiceFromIde],
            AntigravityAccountsCopy.Info);
        Assert.DoesNotContain(AntigravityAccountsCopy.MergeOnlyUntilRefresh, AntigravityAccountsCopy.Info);
    }

    // The note applies only while automatic capture is off: it joins the info
    // then, and is absent while capture is on; the rest is the same either way.
    [Fact]
    public void MergeOnlyNoteIsInTheInfoOnlyWhileAutomaticCaptureIsOff()
    {
        var off = AntigravityAccountsCopy.InfoFor(false).ToList();
        var on = AntigravityAccountsCopy.InfoFor(true).ToList();

        Assert.Contains(AntigravityAccountsCopy.MergeOnlyUntilRefresh, off);
        Assert.DoesNotContain(AntigravityAccountsCopy.MergeOnlyUntilRefresh, on);
        Assert.Equal(AntigravityAccountsCopy.Info, on);
        Assert.Equal(AntigravityAccountsCopy.Info, off.Where(s => s != AntigravityAccountsCopy.MergeOnlyUntilRefresh));
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

    // The flyout heads the toggle's paragraphs with the toggle's own label, so
    // both paragraphs must be ones the flyout can show, and the label must be
    // the toggle's (translated through the existing table).
    [Fact]
    public void ToggleParagraphsAreInTheInfoAndHeadedByTheToggleLabel()
    {
        Assert.All(AntigravityAccountsCopy.ToggleParagraphs,
            p => Assert.Contains(p, AntigravityAccountsCopy.InfoFor(false)));
        Assert.Contains(AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.InfoFor(true));
        Assert.Contains(AntigravityAccountsCopy.Toggle, AntigravityAccountsCopy.All());
        // Adjacent, so one heading covers them.
        var info = AntigravityAccountsCopy.InfoFor(false).ToList();
        var at = info.IndexOf(AntigravityAccountsCopy.ToggleParagraphs[0]);
        Assert.Equal(AntigravityAccountsCopy.ToggleParagraphs, info.Skip(at).Take(2));
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
