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
