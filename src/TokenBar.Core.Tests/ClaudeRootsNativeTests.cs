using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>These replace the process-wide native root registries, so nothing
/// else may scan while they run.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeRootsCollection
{
    public const string Name = "native extra Claude roots";
}

/// <summary>
/// The extra-account setters through the real native library and the real
/// pusher, the way the app wires them at launch. The registries only take
/// absolute drive paths, so the scenarios that register a real directory run
/// on Windows (CI's x64 job); elsewhere they return without asserting.
/// Assertions are on the Claude lane only, as deltas against this process's
/// own baseline: the scan also reads whatever the machine really has.
/// </summary>
[Collection(NativeRootsCollection.Name)]
public class ClaudeRootsNativeTests
{
    private const string UncSentinel = @"\\SENTINEL-7f3a\share\.claude-work";

    private static RootsResult Ok(int count) => new(count, []);

    private static long ClaudeGraphOutput() =>
        TbCore.Graph(null).Contributions
            .SelectMany(day => day.Clients)
            .Where(row => row.Client == "claude")
            .Sum(row => row.Tokens.Output);

    /// <summary>One Claude turn with a distinct id, so the fixture's
    /// contribution is exactly <paramref name="output"/> tokens (same shape as
    /// the W4b Rust acceptance fixture).</summary>
    private static string Fixture(long output)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tokenbar-tests", "extra-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        var project = Path.Combine(dir, "projects", "proj");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, id + ".jsonl"),
            "{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"requestId\":\"req_" + id
            + "\",\"message\":{\"id\":\"msg_" + id
            + "\",\"model\":\"claude-3-5-sonnet\",\"usage\":{\"input_tokens\":0,\"output_tokens\":" + output + "}}}");
        return dir;
    }

    /// <summary>R6 at the boundary: a refused path comes back as an index and a
    /// fixed code, never echoed, from both setters.</summary>
    [Fact]
    public void RejectionsNeverEchoThePath()
    {
        try
        {
            var config = TbCore.SetClaudeConfigDirs([UncSentinel]);
            var scan = TbCore.SetExtraClaudeScanPaths([UncSentinel + @"\projects"]);

            Assert.Equal(new RootRejection(0, "unsupportedPath"), Assert.Single(config.Rejected));
            Assert.Equal(new RootRejection(0, "unsupportedPath"), Assert.Single(scan.Rejected));
            Assert.Equal(0, config.RegisteredCount);
            Assert.DoesNotContain("SENTINEL", JsonSerializer.Serialize(config), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SENTINEL", JsonSerializer.Serialize(scan), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TbCore.SetClaudeConfigDirs([]);
            TbCore.SetExtraClaudeScanPaths([]);
        }
    }

    /// <summary>The picker's pre-save check is the native answer
    /// (<c>tb_validate_claude_config_dir</c>), and for a single path it is
    /// the config setter's own: one rule, kept in Rust. The rule's full table
    /// is the Rust test <c>validate_answers_as_the_setter_would_for_the_appended_entry</c>;
    /// these rows prove the C# wiring.</summary>
    [Theory]
    [InlineData(@"E:\", "rootDirectory")]
    [InlineData(@"C:work", "unsupportedPath")]
    [InlineData(@"\Users\x", "unsupportedPath")]
    [InlineData(@"\\server\share\.claude", "unsupportedPath")]
    [InlineData(@"//wsl.localhost/Ubuntu/home/me/.claude", "unsupportedPath")]
    [InlineData(@"D:\a\con", "invalidComponent")]
    [InlineData(@"D:\parity\.claude-work", null)]
    public void UiRejectionIsTheNativeAnswer(string path, string? expected)
    {
        try
        {
            Assert.Equal(expected, ClaudeExtraRoots.UiRejection(path, []));
            Assert.Equal(expected, TbCore.SetClaudeConfigDirs([path]).Rejected.SingleOrDefault()?.Reason);
        }
        finally
        {
            TbCore.SetClaudeConfigDirs([]);
        }
    }

    [Fact]
    public void UiRejectionReadsTheSavedList()
    {
        Assert.Equal("duplicate", ClaudeExtraRoots.UiRejection("d:/WORK/.claude/", [@"D:\work\.claude"]));
        IReadOnlyList<string> eight = [.. Enumerable.Range(0, 8).Select(i => $@"D:\a{i}")];
        Assert.Equal("limitExceeded", ClaudeExtraRoots.UiRejection(@"D:\b", eight));
        // A refused saved entry takes no slot.
        Assert.Null(ClaudeExtraRoots.UiRejection(@"D:\b", [.. eight.Take(7), @"\\server\share"]));
    }

    /// <summary>Security review R2 against the real profile: the primary's
    /// .claude, anything under it and any folder above it. Windows only, since
    /// native compares against the real home, which is a drive path only
    /// there.</summary>
    [Fact]
    public void UiRejectionRefusesThePrimaryFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in new[]
                 {
                     home, home + @"\.claude", (home + @"/.CLAUDE/").ToLowerInvariant(),
                     home + @"\.claude\work", Path.GetDirectoryName(home)!,
                 })
        {
            Assert.Equal("defaultConfigDir", ClaudeExtraRoots.UiRejection(path, []));
        }

        Assert.Null(ClaudeExtraRoots.UiRejection(home + @"\.claude-work", []));
    }

    /// <summary>Settings stats only a path the registries accept: never a UNC,
    /// WSL, rooted or drive-relative one.</summary>
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\.claude", false)]
    [InlineData(@"//WSL$/Ubuntu/home/me/.claude", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"C:work", false)]
    [InlineData(@"\Users\x", false)]
    [InlineData(@"D:\work\.claude", true)]
    public void SettingsStatsOnlyAcceptedPaths(string dir, bool mayCheck)
    {
        Assert.Equal(mayCheck, ClaudeExtraRoots.MayCheckExists(dir));
    }

    /// <summary>
    /// Security review R1 through the production entry: the app's pusher over
    /// the real setters, then <c>tb_graph</c>. Adding a directory raises the
    /// Claude totals by exactly the fixture's tokens and changes the source id;
    /// removing it restores both. The control pusher has the scan setter taken
    /// out (the mutation): the totals must not move, so the delta assertion
    /// cannot pass on a push that never reached the scan.
    /// </summary>
    [Fact]
    public void AddingAndRemovingADirectoryMovesTheGraphThroughTheRealSetters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const long Output = 7_919_771;
        var dir = Fixture(Output);
        IReadOnlyList<string> saved = [];
        var pusher = new ClaudeRootsPusher(
            () => saved, TbCore.SetClaudeConfigDirs, TbCore.SetExtraClaudeScanPaths, _ => { });
        var withoutScan = new ClaudeRootsPusher(
            () => saved, TbCore.SetClaudeConfigDirs, _ => Ok(0), _ => { });
        try
        {
            var baselineId = TbCore.SourceContextId();
            var baseline = ClaudeGraphOutput();

            // Control: nothing registered, a second read is identical.
            Assert.Equal(baseline, ClaudeGraphOutput());
            Assert.Equal(baselineId, TbCore.SourceContextId());

            saved = [dir];
            Assert.True(withoutScan.Request().Wait(TimeSpan.FromSeconds(60)));
            Assert.Equal(baseline, ClaudeGraphOutput());

            Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(60)));
            Assert.Empty(pusher.Last!.Rejected);
            Assert.Equal(baseline + Output, ClaudeGraphOutput());
            Assert.NotEqual(baselineId, TbCore.SourceContextId());

            saved = [];
            Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(60)));
            Assert.Equal(baseline, ClaudeGraphOutput());
            Assert.Equal(baselineId, TbCore.SourceContextId());
        }
        finally
        {
            TbCore.SetClaudeConfigDirs([]);
            TbCore.SetExtraClaudeScanPaths([]);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The account's own window (#160 contract): its key reads its
    /// own rows; a key the registry never reported is an error, which the
    /// dashboard turns into "unattributed".</summary>
    [Fact]
    public void AnAccountWindowReadsOnlyItsRegisteredRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const long Output = 6_131;
        var dir = Fixture(Output);
        var pusher = new ClaudeRootsPusher(
            () => [dir], TbCore.SetClaudeConfigDirs, TbCore.SetExtraClaudeScanPaths, _ => { });
        try
        {
            Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(60)));
            const long From = 1_767_225_600_000; // 2026-01-01T00:00:00Z
            const long Until = 1_767_312_000_000;

            var own = TbCore.WindowUsage(dir, From, Until);
            Assert.Equal(Output, own.Messages.Where(m => m.Client == "claude").Sum(m => m.Output));
            Assert.DoesNotContain(
                TbCore.WindowUsage(From, Until).Messages,
                m => m.Client == "claude" && m.Output == Output);
            Assert.ThrowsAny<TbCoreException>(() => TbCore.WindowUsage(dir + "-unregistered", From, Until));
        }
        finally
        {
            TbCore.SetClaudeConfigDirs([]);
            TbCore.SetExtraClaudeScanPaths([]);
            Directory.Delete(dir, recursive: true);
        }
    }
}
