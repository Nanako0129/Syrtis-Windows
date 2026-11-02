using TokenBar.App;

namespace TokenBar.Core.Tests;

/// <summary>
/// The Accounts settings page keeps one short line per section and moves every
/// other explanation behind the info button. SettingsWindow compiles under no
/// test project, so these pin the rows the flyouts render: nothing the sections
/// used to say inline was dropped, the privacy and storage facts are still
/// there, and the automatic-capture group is last under its own heading.
/// </summary>
public class AccountsPageCopyTests
{
    private static List<(string Text, bool Heading)> AgyRows(bool? captureEnabled) =>
        AntigravityAccountsCopy.InfoRows(captureEnabled).ToList();

    [Fact]
    public void EachSectionKeepsOneShortLine()
    {
        foreach (var summary in new[] { ClaudeAccountsCopy.Summary, AntigravityAccountsCopy.Summary })
        {
            Assert.DoesNotContain('\n', summary);
            Assert.True(summary.Length <= 60, summary);
        }

        Assert.DoesNotContain(ClaudeAccountsCopy.Summary, ClaudeAccountsCopy.InfoRows().Select(r => r.Text));
        Assert.DoesNotContain(AntigravityAccountsCopy.Summary, AgyRows(true).Select(r => r.Text));
    }

    // The five hints the Claude section showed inline before the move.
    [Fact]
    public void ClaudeInfoHoldsEveryFormerInlineHint() =>
        Assert.Equal(
            [(ClaudeAccountsCopy.HowTo, false), (ClaudeAccountsCopy.Reads, false),
                (ClaudeAccountsCopy.Transport, false), (ClaudeAccountsCopy.Locations, false),
                (ClaudeAccountsCopy.RemoveMeans, false)],
            ClaudeAccountsCopy.InfoRows());

    // Automatic capture on: the four general paragraphs, then the toggle's
    // heading and its WhenOn paragraph, last.
    [Theory]
    [InlineData(true)]
    [InlineData(null)] // no controller counts as on
    public void AntigravityRowsWhileCaptureIsOn(bool? enabled) =>
        Assert.Equal(
            [(AntigravityAccountsCopy.HowTo, false), (AntigravityAccountsCopy.Reads, false),
                (AntigravityAccountsCopy.RemoveMeans, false), (AntigravityAccountsCopy.TwiceFromIde, false),
                (AntigravityAccountsCopy.Toggle, true), (AntigravityAccountsCopy.WhenOn, false)],
            AgyRows(enabled));

    // Off: the same rows, plus the off-only note as the last paragraph of the
    // toggle's group. No paragraph of the former inline set is lost either way.
    [Fact]
    public void AntigravityRowsWhileCaptureIsOffAddTheNoteInsideTheToggleGroup()
    {
        var on = AgyRows(true);
        var off = AgyRows(false);

        Assert.Equal(on.Append((AntigravityAccountsCopy.MergeOnlyUntilRefresh, false)), off);
        Assert.DoesNotContain(on, r => r.Text == AntigravityAccountsCopy.MergeOnlyUntilRefresh);

        foreach (var rows in new[] { on, off })
        {
            Assert.Single(rows, r => r.Heading);
            var heading = rows.FindIndex(r => r.Heading);
            Assert.Equal(AntigravityAccountsCopy.Toggle, rows[heading].Text);
            // The heading starts the last group: only toggle paragraphs follow it.
            Assert.Equal(AntigravityAccountsCopy.WhenOn, rows[heading + 1].Text);
            Assert.All(rows.Skip(heading + 1), r => Assert.Contains(
                r.Text, new[] { AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.MergeOnlyUntilRefresh }));
        }

        var former = new[]
        {
            AntigravityAccountsCopy.WhenOn, AntigravityAccountsCopy.HowTo, AntigravityAccountsCopy.Reads,
            AntigravityAccountsCopy.RemoveMeans, AntigravityAccountsCopy.TwiceFromIde,
            AntigravityAccountsCopy.MergeOnlyUntilRefresh,
        };
        Assert.Empty(former.Except(off.Select(r => r.Text)));
    }

    // Where a credential goes, where it is kept, and what Remove does must stay
    // reachable from the page.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrivacyAndStorageFactsStayInTheInfo(bool captureEnabled)
    {
        var claude = string.Join('\n', ClaudeAccountsCopy.InfoRows().Select(r => r.Text));
        Assert.Contains("api.anthropic.com", claude);
        Assert.Contains("nothing is revoked at Anthropic", claude);

        var antigravity = string.Join('\n', AgyRows(captureEnabled).Select(r => r.Text));
        Assert.Contains("Windows Credential Manager", antigravity);
        Assert.Contains("Google's token service", antigravity);
        Assert.Contains("deletes only the copy on this PC", antigravity);
        Assert.Contains("revoke access", antigravity);
    }

    [Fact]
    public void CursorKeepsOneLineAndItsPrivacyParagraphInTheInfo()
    {
        Assert.DoesNotContain('\n', SettingsCopy.CursorSummary);
        Assert.True(SettingsCopy.CursorSummary.Length <= 60);
        Assert.Equal([(CursorSync.Copy.Privacy, false)], CursorSync.Copy.InfoRows());
        Assert.Contains(SettingsCopy.CursorSummary, SettingsCopy.All); // checked in both tables

        // Reads the Cursor login; sends only to Cursor's usage service; what is
        // stored; the login is not stored; turning off deletes the usage.
        var privacy = CursorSync.Copy.Privacy;
        Assert.Contains("reads the login of the Cursor app", privacy);
        Assert.Contains("only to Cursor's usage service (cursor.com)", privacy);
        Assert.Contains("date, model, token counts, cost and conversation ID", privacy);
        Assert.Contains("the login itself is never saved or logged", privacy);
        Assert.Contains("Turning this off deletes the downloaded usage", privacy);
    }

    // The translation tests walk All(); everything the flyouts render must be in it.
    [Fact]
    public void TranslationCheckCoversTheNewStrings()
    {
        Assert.Subset(
            ClaudeAccountsCopy.All().ToHashSet(),
            ClaudeAccountsCopy.InfoRows().Select(r => r.Text).Append(ClaudeAccountsCopy.Summary).ToHashSet());
        Assert.Subset(
            AntigravityAccountsCopy.All().ToHashSet(),
            AgyRows(false).Select(r => r.Text).Append(AntigravityAccountsCopy.Summary).ToHashSet());
        Assert.Contains(SettingsCopy.AccountsPage, SettingsCopy.All);
        Assert.Contains(SettingsCopy.MoreInfo, SettingsCopy.All);
    }
}
