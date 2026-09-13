using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenBar.App;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace TokenBar.Core.Tests;

public class UpdateFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-update-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }



    /// <summary>The Velopack pack id is a literal in two places by design, so a
    /// product rename cannot move it by accident. That only holds if the two
    /// cannot drift: a packaging script emitting one id while the installed
    /// client demands another produces releases that every client silently
    /// refuses, and the failure appears as "no update available" rather than an
    /// error.</summary>
    [Fact]
    public void PackageIdMatchesPackagingScript()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-velopack.ps1"));
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"^\$packId\s*=\s*""(?<id>[^""]+)""\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(match.Success, "package-velopack.ps1 has no $packId assignment.");
        Assert.Equal(UpdateFlow.PackageId, match.Groups["id"].Value);
    }

    [Theory]
    [InlineData("win-x64", "x64")]
    [InlineData("win-x64-lite", "x64")]
    [InlineData("win-arm64", "arm64")]
    [InlineData("win-arm64-lite", "arm64")]
    public async Task GithubSourceIsFixedStableUnauthenticatedAndDownloadsOnlyAfterClick(
        string channel,
        string architecture)
    {
        var fixture = CreateFixture(channel);

        var candidate = await fixture.Flow.CheckForUpdatesAsync();

        Assert.NotNull(candidate);
        Assert.Equal("0.3.0", candidate.Version);
        Assert.Equal(channel, candidate.Channel);
        Assert.Equal(architecture, candidate.Architecture);
        Assert.Equal(
            $"{UpdateFlow.PackageId}-0.3.0-{channel}-full.nupkg",
            candidate.Target.FileName);
        Assert.Equal(1, fixture.Downloader.DownloadStringCalls);
        Assert.Equal(1, fixture.Downloader.DownloadBytesCalls);
        Assert.Equal(0, fixture.Downloader.DownloadFileCalls);
        Assert.Equal(
            "https://api.github.com/repos/Nanako0129/Syrtis-Windows/releases?per_page=10&page=1",
            fixture.Downloader.Requests[0].Url);
        Assert.DoesNotContain(
            fixture.Downloader.Requests,
            request => request.Url.Contains("prerelease", StringComparison.Ordinal));
        Assert.All(
            fixture.Downloader.Requests,
            request => Assert.DoesNotContain(
                request.Headers.Keys,
                key => string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase)));

        await fixture.Flow.DownloadAndVerifyAsync(candidate);

        Assert.Equal(1, fixture.Downloader.DownloadFileCalls);
        Assert.True(File.Exists(candidate.PackagePath));
        Assert.EndsWith(
            $"{UpdateFlow.PackageId}-0.3.0-{channel}-full.nupkg",
            candidate.PackagePath,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win-x64-lite", "x64")]
    [InlineData("win-arm64-lite", "arm64")]
    public async Task LiteChannel_CheckFilenameNuspecChecksumDownloadAndHandoff(
        string channel,
        string architecture)
    {
        var fixture = CreateFixture(channel);
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);

        var candidate = await flow.CheckForUpdatesAsync();

        Assert.NotNull(candidate);
        Assert.Equal(channel, candidate.Channel);
        Assert.Equal(architecture, candidate.Architecture);
        Assert.Equal(
            $"{UpdateFlow.PackageId}-0.3.0-{channel}-full.nupkg",
            candidate.Target.FileName);

        await flow.DownloadAndVerifyAsync(candidate);

        Assert.True(File.Exists(candidate.PackagePath));
        Assert.Equal(1, fixture.Downloader.DownloadFileCalls);

        // Re-open package to prove nuspec channel survived checksum path.
        using (var archive = ZipFile.OpenRead(candidate.PackagePath))
        {
            var nuspec = Assert.Single(
                archive.Entries.Where(e =>
                    e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)));
            using var reader = new StreamReader(nuspec.Open());
            var text = await reader.ReadToEndAsync();
            Assert.Contains($"<channel>{channel}</channel>", text, StringComparison.Ordinal);
            Assert.Contains(
                $"<machineArchitecture>{architecture}</machineArchitecture>",
                text,
                StringComparison.Ordinal);
        }

        var events = new List<string>();
        flow.Events = events;
        var handedOff = flow.TryHandoff(candidate, () => true, () => events.Add("quit"));

        Assert.True(handedOff);
        Assert.Equal(["handoff", "quit"], events);
        Assert.Equal(1, flow.HandoffCalls);
        Assert.Same(candidate.Target, flow.HandoffTarget);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-x64-lite")]
    [InlineData("win-arm64")]
    [InlineData("win-arm64-lite")]
    public void MapChannelToArchitecture_AcceptsExactChannels(string channel)
    {
        var arch = UpdateFlow.MapChannelToArchitecture(channel);
        Assert.Equal(
            channel.StartsWith("win-arm64", StringComparison.Ordinal) ? "arm64" : "x64",
            arch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("win")]
    [InlineData("win-x64-full")]
    [InlineData("win-x64lite")]
    [InlineData("win-x64-Lite")]
    [InlineData("win-arm64-full")]
    [InlineData("linux-x64")]
    [InlineData("win-x86")]
    public void MapChannelToArchitecture_FailClosedOnNearMiss(string? channel)
    {
        Assert.Throws<InvalidOperationException>(
            () => UpdateFlow.MapChannelToArchitecture(channel));
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("wrong-type")]
    [InlineData("wrong-filename")]
    [InlineData("same-version")]
    [InlineData("lower-version")]
    [InlineData("prerelease-version")]
    [InlineData("missing-sha256")]
    [InlineData("bad-sha256")]
    [InlineData("zero-size")]
    public async Task RejectsInvalidTargetsBeforeDownload(string testCase)
    {
        var fixture = CreateFixture(mutateTarget: target =>
        {
            switch (testCase)
            {
                case "wrong-id":
                    target.PackageId = "Other.Package";
                    break;
                case "wrong-type":
                    target.Type = VelopackAssetType.Delta;
                    break;
                case "wrong-filename":
                    target.FileName = "other.nupkg";
                    break;
                case "same-version":
                    target.Version = SemanticVersion.Parse("0.2.0");
                    target.FileName = "Nyanako.Syrtis-0.2.0-win-x64-full.nupkg";
                    break;
                case "lower-version":
                    target.Version = SemanticVersion.Parse("0.1.0");
                    target.FileName = "Nyanako.Syrtis-0.1.0-win-x64-full.nupkg";
                    break;
                case "prerelease-version":
                    target.Version = SemanticVersion.Parse("0.3.0-beta.1");
                    target.FileName = "Nyanako.Syrtis-0.3.0-beta.1-win-x64-full.nupkg";
                    break;
                case "missing-sha256":
                    target.SHA256 = "";
                    break;
                case "bad-sha256":
                    target.SHA256 = new string('g', 64);
                    break;
                case "zero-size":
                    target.Size = 0;
                    break;
            }
        });

        UpdateCandidate? candidate = null;
        var exception = await Record.ExceptionAsync(async () =>
            candidate = await fixture.Flow.CheckForUpdatesAsync());

        Assert.True(exception is not null || candidate is null);
        Assert.Equal(0, fixture.Downloader.DownloadFileCalls);
    }

    [Theory]
    [InlineData("Other.Package", "win-x64")]
    [InlineData("Nyanako.Syrtis", "win")]
    [InlineData("Nyanako.Syrtis", "linux-x64")]
    [InlineData("Nyanako.Syrtis", "win-x64-full")]
    [InlineData("Nyanako.Syrtis", "win-x64lite")]
    [InlineData("Nyanako.Syrtis", "win-arm64-full")]
    public async Task RejectsInvalidInstalledIdentityOrChannelBeforeNetwork(
        string appId,
        string channel)
    {
        var fixture = CreateFixture(channel, appId: appId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Flow.CheckForUpdatesAsync());

        Assert.Empty(fixture.Downloader.Requests);
    }

    [Fact]
    public async Task RejectsNotInstalledBeforeNetwork()
    {
        Directory.CreateDirectory(_dir);
        var packages = Path.Combine(_dir, "packages");
        Directory.CreateDirectory(packages);
        var package = CreateValidPackage("win-x64", "x64");
        var target = CreateTarget(package, "win-x64");
        var downloader = new FakeDownloader("win-x64", target, package);
        var locator = new NotInstalledLocator(packages);
        var flow = new UpdateFlow(downloader, locator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CheckForUpdatesAsync());

        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task ExistingCorruptedFinalPackageIsReverifiedAndDeleted()
    {
        var fixture = CreateFixture();
        var candidate = Assert.IsType<UpdateCandidate>(
            await fixture.Flow.CheckForUpdatesAsync());
        Directory.CreateDirectory(Path.GetDirectoryName(candidate.PackagePath)!);
        await File.WriteAllBytesAsync(candidate.PackagePath, [1, 2, 3, 4]);

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Flow.DownloadAndVerifyAsync(candidate));

        Assert.Equal(0, fixture.Downloader.DownloadFileCalls);
        Assert.False(File.Exists(candidate.PackagePath));
    }

    [Fact]
    public async Task DownloadedChecksumFailureDeletesPackageAndNeverHandoffs()
    {
        var fixture = CreateFixture(mutateTarget: target =>
            target.SHA256 = new string('0', 64));
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());

        var quitCalls = 0;
        async Task DownloadThenHandoff()
        {
            await flow.DownloadAndVerifyAsync(candidate);
            _ = flow.TryHandoff(candidate, () => true, () => quitCalls++);
        }

        await Assert.ThrowsAnyAsync<Exception>(DownloadThenHandoff);

        Assert.Equal(1, fixture.Downloader.DownloadFileCalls);
        Assert.False(File.Exists(candidate.PackagePath));
        Assert.Equal(0, flow.HandoffCalls);
        Assert.Equal(0, quitCalls);
    }

    [Theory]
    [InlineData("no-entry")]
    [InlineData("two-entries")]
    [InlineData("empty")]
    [InlineData("declared-over")]
    [InlineData("actual-over")]
    [InlineData("compressed-over")]
    [InlineData("ratio")]
    [InlineData("dtd")]
    [InlineData("malformed")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("wrong-channel")]
    [InlineData("wrong-architecture")]
    public async Task RejectsAdversarialNuspecAndInvalidatesPackage(string testCase)
    {
        var package = CreateAdversarialPackage(testCase);
        AssertAdversarialFixture(testCase, package);
        var fixture = CreateFixture(packageBytes: package);
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());
        var quitCalls = 0;
        async Task DownloadThenHandoff()
        {
            await flow.DownloadAndVerifyAsync(candidate);
            _ = flow.TryHandoff(candidate, () => true, () => quitCalls++);
        }

        await Assert.ThrowsAnyAsync<Exception>(DownloadThenHandoff);

        Assert.False(File.Exists(candidate.PackagePath));
        Assert.Equal(0, flow.HandoffCalls);
        Assert.Equal(0, quitCalls);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("404")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("malformed-api")]
    [InlineData("malformed-feed")]
    public async Task SourceFailuresDoNotRetryOrDownload(string testCase)
    {
        var fixture = CreateFixture();
        switch (testCase)
        {
            case "offline":
                fixture.Downloader.ApiException = new HttpRequestException();
                break;
            case "404":
                fixture.Downloader.ApiException = new HttpRequestException(
                    null, null, HttpStatusCode.NotFound);
                break;
            case "403":
                fixture.Downloader.ApiException = new HttpRequestException(
                    null, null, HttpStatusCode.Forbidden);
                break;
            case "429":
                fixture.Downloader.ApiException = new HttpRequestException(
                    null, null, HttpStatusCode.TooManyRequests);
                break;
            case "malformed-api":
                fixture.Downloader.ApiTextOverride = "{";
                break;
            case "malformed-feed":
                fixture.Downloader.FeedBytesOverride = "{"u8.ToArray();
                break;
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Flow.CheckForUpdatesAsync());

        Assert.Equal(1, fixture.Downloader.DownloadStringCalls);
        Assert.Equal(testCase == "malformed-feed" ? 1 : 0,
            fixture.Downloader.DownloadBytesCalls);
        Assert.Equal(0, fixture.Downloader.DownloadFileCalls);
    }

    [Fact]
    public async Task DownloadFailureDoesNotRetryAndDeletesPartialPackage()
    {
        var fixture = CreateFixture();
        var candidate = Assert.IsType<UpdateCandidate>(
            await fixture.Flow.CheckForUpdatesAsync());
        fixture.Downloader.DownloadException = new IOException();

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Flow.DownloadAndVerifyAsync(candidate));

        Assert.Equal(1, fixture.Downloader.DownloadFileCalls);
        Assert.False(File.Exists(candidate.PackagePath));
        Assert.False(File.Exists(candidate.PackagePath + ".partial"));
    }

    [Fact]
    public async Task LockFailureDoesNotDownloadOrHandoff()
    {
        Directory.CreateDirectory(_dir);
        var packages = Path.Combine(_dir, "packages-is-a-file");
        await File.WriteAllTextAsync(packages, "not a directory");
        var fixture = CreateFixture(packagesDirectory: packages);
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());

        await Assert.ThrowsAnyAsync<Exception>(
            () => flow.DownloadAndVerifyAsync(candidate));

        Assert.Equal(0, fixture.Downloader.DownloadFileCalls);
        Assert.Equal(0, flow.HandoffCalls);
    }

    [Fact]
    public async Task DownloadIsSingleFlight()
    {
        var fixture = CreateFixture();
        var candidate = Assert.IsType<UpdateCandidate>(
            await fixture.Flow.CheckForUpdatesAsync());
        fixture.Downloader.BlockDownload = true;

        var first = fixture.Flow.DownloadAndVerifyAsync(candidate);
        await fixture.Downloader.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Flow.DownloadAndVerifyAsync(candidate));
        fixture.Downloader.ReleaseDownload.TrySetResult();
        await first;

        Assert.Equal(1, fixture.Downloader.DownloadFileCalls);
    }

    [Fact]
    public async Task HandoffUsesExactParametersAndQuitsAfterReturn()
    {
        var fixture = CreateFixture();
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());
        var events = new List<string>();
        flow.Events = events;

        var handedOff = flow.TryHandoff(
            candidate,
            () => true,
            () => events.Add("quit"));

        Assert.True(handedOff);
        Assert.Equal(["handoff", "quit"], events);
        Assert.Same(candidate.Target, flow.HandoffTarget);
        // Silent: the dialog is still up showing "Installing update…" when the
        // handoff runs, so Velopack's own apply UI would be a second window on
        // top of it.
        Assert.True(flow.Silent);
        Assert.True(flow.Restart);
        Assert.Empty(Assert.IsType<string[]>(flow.RestartArgs));
        Assert.Equal(1, flow.HandoffCalls);
    }

    [Fact]
    public async Task HandoffThrowDoesNotQuit()
    {
        var fixture = CreateFixture();
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator)
        {
            HandoffException = new InvalidOperationException(),
        };
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());
        var quitCalls = 0;

        Assert.Throws<InvalidOperationException>(() => flow.TryHandoff(
            candidate,
            () => true,
            () => quitCalls++));

        Assert.Equal(1, flow.HandoffCalls);
        Assert.Equal(0, quitCalls);
    }

    [Fact]
    public async Task DisposedTrayGateSkipsHandoffAndQuit()
    {
        var fixture = CreateFixture();
        var flow = new RecordingUpdateFlow(fixture.Downloader, fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(
            await flow.CheckForUpdatesAsync());
        var quitCalls = 0;

        var handedOff = flow.TryHandoff(
            candidate,
            () => false,
            () => quitCalls++);

        Assert.False(handedOff);
        Assert.Equal(0, flow.HandoffCalls);
        Assert.Equal(0, quitCalls);
    }

    [Fact]
    public void PendingActionPublishAndIgnoreDoNotInvoke()
    {
        using var pending = new PendingUpdateAction();
        var calls = 0;

        Assert.True(pending.Publish("0.3.0", () => calls++));
        Assert.Equal("0.3.0", pending.Peek());
        Assert.Equal(0, calls);
        Assert.Equal("0.3.0", pending.Peek());
    }

    [Fact]
    public void PendingActionTakesAndInvokesOnce()
    {
        using var pending = new PendingUpdateAction();
        var calls = 0;
        Assert.True(pending.Publish("0.3.0", () => calls++));

        var action = Assert.IsType<PendingUpdateAction.PendingAction>(pending.Take());

        Assert.Null(pending.Take());
        Assert.True(action.TryInvoke());
        Assert.False(action.TryInvoke());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PendingActionRestoresForManualRetry()
    {
        using var pending = new PendingUpdateAction();
        var calls = 0;
        Assert.True(pending.Publish("0.3.0", () => calls++));
        var first = Assert.IsType<PendingUpdateAction.PendingAction>(pending.Take());
        Assert.True(first.TryInvoke());

        Assert.True(pending.Restore(first));
        var retry = Assert.IsType<PendingUpdateAction.PendingAction>(pending.Take());
        Assert.Same(first, retry);
        Assert.True(retry.TryInvoke());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PendingActionDisposePreventsRestoreAndInvoke()
    {
        var pending = new PendingUpdateAction();
        var calls = 0;
        Assert.True(pending.Publish("0.3.0", () => calls++));
        var action = Assert.IsType<PendingUpdateAction.PendingAction>(pending.Take());

        pending.Dispose();

        Assert.False(action.TryInvoke());
        Assert.False(pending.Restore(action));
        Assert.False(pending.Publish("0.4.0", () => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PendingActionClearRemovesAndInvalidatesAction()
    {
        using var pending = new PendingUpdateAction();
        var calls = 0;
        Assert.True(pending.Publish("0.3.0", () => calls++));
        var action = Assert.IsType<PendingUpdateAction.PendingAction>(pending.Take());

        pending.Clear();

        Assert.Null(pending.Peek());
        Assert.False(action.TryInvoke());
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("0.3.0\n")]
    [InlineData("v0.3.0")]
    [InlineData("00.3.0")]
    public void PendingActionRejectsNonNormalizedDisplayVersion(string version)
    {
        using var pending = new PendingUpdateAction();

        Assert.Throws<ArgumentException>(() => pending.Publish(version, () => { }));
    }

    private Fixture CreateFixture(
        string channel = "win-x64",
        byte[]? packageBytes = null,
        Action<VelopackAsset>? mutateTarget = null,
        string appId = UpdateFlow.PackageId,
        string installedVersion = "0.2.0",
        string? packagesDirectory = null)
    {
        Directory.CreateDirectory(_dir);
        packagesDirectory ??= Path.Combine(_dir, Guid.NewGuid().ToString("N"), "packages");
        if (!File.Exists(packagesDirectory))
        {
            Directory.CreateDirectory(packagesDirectory);
        }

        // Production mapping: fail-closed except the four accepted channels.
        // For invalid-channel tests we still need a package architecture token;
        // MapChannelToArchitecture is exercised separately.
        string architecture;
        try
        {
            architecture = UpdateFlow.MapChannelToArchitecture(channel);
        }
        catch (InvalidOperationException)
        {
            architecture = channel.Contains("arm64", StringComparison.Ordinal) ? "arm64" : "x64";
        }

        packageBytes ??= CreateValidPackage(channel, architecture);
        var target = CreateTarget(packageBytes, channel);
        mutateTarget?.Invoke(target);
        var downloader = new FakeDownloader(channel, target, packageBytes);
        var locator = new TestVelopackLocator(
            appId,
            installedVersion,
            packagesDirectory,
            appDir: _dir,
            rootDir: _dir,
            updateExe: Path.Combine(_dir, "Update.exe"),
            channel: channel);
        return new Fixture(
            new UpdateFlow(downloader, locator),
            downloader,
            locator);
    }

    private static VelopackAsset CreateTarget(byte[] package, string channel)
    {
        const string version = "0.3.0";
        return new VelopackAsset
        {
            PackageId = UpdateFlow.PackageId,
            Version = SemanticVersion.Parse(version),
            Type = VelopackAssetType.Full,
            FileName = $"{UpdateFlow.PackageId}-{version}-{channel}-full.nupkg",
            SHA1 = Convert.ToHexString(SHA1.HashData(package)),
            SHA256 = Convert.ToHexString(SHA256.HashData(package)),
            Size = package.Length,
        };
    }

    private static byte[] CreateValidPackage(
        string channel,
        string architecture,
        string id = UpdateFlow.PackageId,
        string version = "0.3.0") =>
        CreateZip(new ZipEntrySpec(
            $"{UpdateFlow.PackageId}.nuspec",
            Encoding.UTF8.GetBytes(Nuspec(id, version, channel, architecture)),
            CompressionLevel.Optimal));

    private static byte[] CreateAdversarialPackage(string testCase) => testCase switch
    {
        "no-entry" => CreateZip(new ZipEntrySpec(
            "content.txt", "content"u8.ToArray(), CompressionLevel.Optimal)),
        "two-entries" => CreateZip(
            new ZipEntrySpec("one.nuspec", "<package/>"u8.ToArray(), CompressionLevel.Optimal),
            new ZipEntrySpec("two.nuspec", "<package/>"u8.ToArray(), CompressionLevel.Optimal)),
        "empty" => CreateZip(new ZipEntrySpec(
            "empty.nuspec", [], CompressionLevel.NoCompression)),
        "declared-over" => CreateZip(new ZipEntrySpec(
            "large.nuspec", ModeratelyCompressibleBytes(65_537), CompressionLevel.Optimal)),
        "actual-over" => PatchDeclaredUncompressedLength(
            CreateZip(new ZipEntrySpec(
                "actual.nuspec", ModeratelyCompressibleBytes(65_537), CompressionLevel.Optimal)),
            65_536),
        "compressed-over" => CreateZip(new ZipEntrySpec(
            "compressed.nuspec", RandomBytes(65_536), CompressionLevel.SmallestSize)),
        "ratio" => CreateZip(new ZipEntrySpec(
            "ratio.nuspec",
            Encoding.UTF8.GetBytes(Nuspec(
                UpdateFlow.PackageId,
                "0.3.0",
                "win-x64",
                "x64",
                new string('A', 60_000))),
            CompressionLevel.SmallestSize)),
        "dtd" => CreateZip(new ZipEntrySpec(
            "dtd.nuspec",
            Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <!DOCTYPE package [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
                <package><metadata><id>&xxe;</id><version>0.3.0</version><channel>win-x64</channel><machineArchitecture>x64</machineArchitecture></metadata></package>
                """),
            CompressionLevel.Optimal)),
        "malformed" => CreateZip(new ZipEntrySpec(
            "malformed.nuspec", "<package><metadata>"u8.ToArray(), CompressionLevel.Optimal)),
        "wrong-id" => CreateValidPackage("win-x64", "x64", id: "Other.Package"),
        "wrong-version" => CreateValidPackage("win-x64", "x64", version: "0.4.0"),
        "wrong-channel" => CreateValidPackage("win-arm64", "x64"),
        "wrong-architecture" => CreateValidPackage("win-x64", "arm64"),
        _ => throw new ArgumentOutOfRangeException(nameof(testCase)),
    };

    private static string Nuspec(
        string id,
        string version,
        string channel,
        string architecture,
        string description = "test") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
          <metadata>
            <id>{{id}}</id>
            <version>{{version}}</version>
            <description>{{description}}</description>
            <channel>{{channel}}</channel>
            <machineArchitecture>{{architecture}}</machineArchitecture>
          </metadata>
        </package>
        """;

    private static byte[] CreateZip(params ZipEntrySpec[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spec in entries)
            {
                var entry = archive.CreateEntry(spec.Name, spec.Compression);
                using var output = entry.Open();
                output.Write(spec.Content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] PatchDeclaredUncompressedLength(byte[] package, uint length)
    {
        var local = FindSignature(package, 0x04034b50, fromEnd: false);
        var central = FindSignature(package, 0x02014b50, fromEnd: true);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(local + 22, 4), length);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(central + 24, 4), length);
        return package;
    }

    private static int FindSignature(byte[] bytes, uint signature, bool fromEnd)
    {
        if (fromEnd)
        {
            for (var i = bytes.Length - 4; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == signature)
                {
                    return i;
                }
            }
        }
        else
        {
            for (var i = 0; i <= bytes.Length - 4; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == signature)
                {
                    return i;
                }
            }
        }

        throw new InvalidDataException("ZIP signature not found.");
    }

    private static byte[] ModeratelyCompressibleBytes(int length)
    {
        var bytes = RandomBytes(length);
        for (var i = 0; i < bytes.Length; i += 8)
        {
            bytes[i] = 0;
        }
        return bytes;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static void AssertAdversarialFixture(string testCase, byte[] package)
    {
        if (testCase is not ("declared-over" or "actual-over" or "compressed-over" or "ratio"))
        {
            return;
        }

        using var stream = new MemoryStream(package);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        switch (testCase)
        {
            case "declared-over":
                Assert.True(entry.Length > 65_536);
                Assert.InRange(entry.CompressedLength, 1, 65_536);
                break;
            case "actual-over":
                Assert.Equal(65_536, entry.Length);
                Assert.InRange(entry.CompressedLength, 1, 65_536);
                Assert.True(entry.Length <= entry.CompressedLength * 100);
                break;
            case "compressed-over":
                Assert.InRange(entry.Length, 1, 65_536);
                Assert.True(entry.CompressedLength > 65_536);
                break;
            case "ratio":
                Assert.InRange(entry.Length, 1, 65_536);
                Assert.InRange(entry.CompressedLength, 1, 65_536);
                Assert.True(entry.Length > entry.CompressedLength * 100);
                break;
        }
    }

    private sealed record Fixture(
        UpdateFlow Flow,
        FakeDownloader Downloader,
        TestVelopackLocator Locator);

    private readonly record struct ZipEntrySpec(
        string Name,
        byte[] Content,
        CompressionLevel Compression);

    private sealed record Request(
        string Url,
        IReadOnlyDictionary<string, string> Headers);

    private sealed class FakeDownloader(
        string channel,
        VelopackAsset target,
        byte[] packageBytes) : IFileDownloader
    {
        private const string FeedUrl = "https://downloads.invalid/stable-feed";
        private const string PrereleaseFeedUrl = "https://downloads.invalid/prerelease-feed";
        private const string PackageUrl = "https://downloads.invalid/package";

        internal List<Request> Requests { get; } = [];
        internal int DownloadStringCalls { get; private set; }
        internal int DownloadBytesCalls { get; private set; }
        internal int DownloadFileCalls { get; private set; }
        internal Exception? ApiException { get; set; }
        internal Exception? DownloadException { get; set; }
        internal string? ApiTextOverride { get; set; }
        internal byte[]? FeedBytesOverride { get; set; }
        internal bool BlockDownload { get; set; }
        internal TaskCompletionSource DownloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDownload { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30)
        {
            DownloadStringCalls++;
            Capture(url, headers);
            if (ApiException is not null)
            {
                return Task.FromException<string>(ApiException);
            }

            return Task.FromResult(ApiTextOverride ?? GithubReleasesJson());
        }

        public Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30)
        {
            DownloadBytesCalls++;
            Capture(url, headers);
            if (!string.Equals(url, FeedUrl, StringComparison.Ordinal))
            {
                return Task.FromException<byte[]>(
                    new InvalidOperationException("Unexpected feed URL."));
            }

            return Task.FromResult(FeedBytesOverride ?? Encoding.UTF8.GetBytes(FeedJson()));
        }

        public async Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers = null,
            double timeout = 30,
            CancellationToken cancelToken = default)
        {
            DownloadFileCalls++;
            Capture(url, headers);
            DownloadStarted.TrySetResult();
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            if (!string.Equals(url, PackageUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected package URL.");
            }

            if (BlockDownload)
            {
                await ReleaseDownload.Task.WaitAsync(cancelToken);
            }

            await File.WriteAllBytesAsync(targetFile, packageBytes, cancelToken);
            progress(100);
        }

        private void Capture(string url, IDictionary<string, string>? headers)
        {
            Requests.Add(new Request(
                url,
                headers is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)));
        }

        private string GithubReleasesJson()
        {
            var index = $"releases.{channel}.json";
            return JsonSerializer.Serialize(new object[]
            {
                new
                {
                    name = "prerelease",
                    prerelease = true,
                    published_at = "2026-07-31T02:00:00Z",
                    assets = new[]
                    {
                        new
                        {
                            name = index,
                            url = PrereleaseFeedUrl,
                            browser_download_url = PrereleaseFeedUrl,
                            content_type = "application/json",
                        },
                    },
                },
                new
                {
                    name = "stable",
                    prerelease = false,
                    published_at = "2026-07-31T01:00:00Z",
                    assets = new[]
                    {
                        new
                        {
                            name = index,
                            url = FeedUrl,
                            browser_download_url = FeedUrl,
                            content_type = "application/json",
                        },
                        new
                        {
                            name = target.FileName,
                            url = PackageUrl,
                            browser_download_url = PackageUrl,
                            content_type = "application/octet-stream",
                        },
                    },
                },
            });
        }

        private string FeedJson() => JsonSerializer.Serialize(new
        {
            Assets = new[]
            {
                new
                {
                    target.PackageId,
                    Version = target.Version.ToNormalizedString(),
                    Type = target.Type.ToString(),
                    target.FileName,
                    target.SHA1,
                    target.SHA256,
                    target.Size,
                    // Notes ride the feed, before any download. Without this
                    // the fixture could not reach BoundNotes at all, which is
                    // why that path had no test.
                    target.NotesMarkdown,
                },
            },
        });
    }

    private sealed class RecordingUpdateFlow(
        IFileDownloader downloader,
        IVelopackLocator locator) : UpdateFlow(downloader, locator)
    {
        internal int HandoffCalls { get; private set; }
        internal VelopackAsset? HandoffTarget { get; private set; }
        internal bool? Silent { get; private set; }
        internal bool? Restart { get; private set; }
        internal string[]? RestartArgs { get; private set; }
        internal Exception? HandoffException { get; set; }
        internal List<string>? Events { get; set; }

        protected override void WaitExitThenApplyUpdates(
            VelopackAsset target,
            bool silent,
            bool restart,
            string[] restartArgs)
        {
            HandoffCalls++;
            HandoffTarget = target;
            Silent = silent;
            Restart = restart;
            RestartArgs = restartArgs;
            Events?.Add("handoff");
            if (HandoffException is not null)
            {
                throw HandoffException;
            }
        }
    }

    private sealed class NotInstalledLocator(string packagesDirectory)
        : TestVelopackLocator(
            UpdateFlow.PackageId,
            "0.2.0",
            packagesDirectory,
            appDir: null,
            rootDir: null,
            updateExe: null,
            channel: "win-x64")
    {
        public override SemanticVersion? CurrentlyInstalledVersion => null;
    }

    // ---- manual check status ---------------------------------------------
    //
    // The four states are what the settings line renders; SettingsWindow.cs is
    // not compiled by any test project, so this is the only place the mapping
    // can be asserted.

    [Fact]
    public void ManualCheckStatesRenderTheirOwnLine()
    {
        Localization.Load("en", AppContext.BaseDirectory);
        Assert.Equal("Checking for updates…", UpdateCheckResult.Checking.Text());
        Assert.Equal("You are up to date.", UpdateCheckResult.UpToDate.Text());
        Assert.Equal("Update available: v1.2.3", UpdateCheckResult.Available("1.2.3").Text());
        Assert.Equal("Could not check for updates.", UpdateCheckResult.Failed.Text());
        Assert.Equal(
            "Updates are handled by whatever installed this copy.",
            UpdateCheckResult.Unmanaged.Text());
    }

    // A copy Velopack did not install — Scoop extracts the package payload and
    // versions it itself — has no feed to check. Saying "could not check"
    // there sends someone hunting a network or permissions fault that is not
    // present, so the two states must not render the same line.
    [Fact]
    public void UnmanagedDoesNotRenderAsAFailedCheck()
    {
        Localization.Load("en", AppContext.BaseDirectory);
        Assert.NotEqual(UpdateCheckState.Failed, UpdateCheckResult.Unmanaged.State);
        Assert.NotEqual(
            UpdateCheckResult.Failed.Text(), UpdateCheckResult.Unmanaged.Text());
    }

    // ValidateTarget already rejects an empty version, so Available("") means a
    // caller went around it. Reporting failure is honest; "Update available: v"
    // is not.
    [Fact]
    public void AvailableWithoutAVersionReportsFailureRatherThanAnEmptyVersion()
    {
        Localization.Load("en", AppContext.BaseDirectory);
        Assert.Equal(UpdateCheckState.Failed, UpdateCheckResult.Available("").State);
        Assert.Equal("Could not check for updates.", UpdateCheckResult.Available("").Text());
    }

    [Fact]
    public void ManualCheckStatesAreTranslated()
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            Assert.Equal("正在檢查更新…", UpdateCheckResult.Checking.Text());
            Assert.Equal("已是最新版本。", UpdateCheckResult.UpToDate.Text());
            Assert.Equal("有可用更新：v1.2.3", UpdateCheckResult.Available("1.2.3").Text());
            Assert.Equal("無法檢查更新。", UpdateCheckResult.Failed.Text());
            Assert.Equal(
                "更新由安裝這份程式的工具負責。", UpdateCheckResult.Unmanaged.Text());
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // ---- release notes bounding -------------------------------------------
    //
    // Notes arrive with the feed *before* any download, so ValidateTarget and
    // ValidateNuspec — which protect the package — never see them. BoundNotes
    // is the only thing between the feed and the dialog, and a violation must
    // drop the notes while keeping the update: failing the candidate instead
    // would make an oversized notes field a lever for denying updates.
    //
    // This path had no test at all until the outcome verifier said so, even
    // though UpdateFlow.cs is already in the test project's compile set.

    [Fact]
    public async Task OversizedNotesAreDroppedAndTheUpdateSurvives()
    {
        var fixture = CreateFixture(mutateTarget: target =>
            target.NotesMarkdown = new string('x', ReleaseNotesMarkdown.MaxInputChars + 1));

        var candidate = await fixture.Flow.CheckForUpdatesAsync();

        Assert.NotNull(candidate);
        Assert.Null(candidate!.Notes);
    }

    [Fact]
    public async Task NotesWithinTheBoundReachTheCandidate()
    {
        const string notes = "## Fixed\n\n- A thing that was broken.";
        var fixture = CreateFixture(mutateTarget: target => target.NotesMarkdown = notes);

        var candidate = await fixture.Flow.CheckForUpdatesAsync();

        Assert.NotNull(candidate);
        Assert.Equal(notes, candidate!.Notes);
    }

    // The packaging script embeds release notes in the nuspec and asserts the
    // result fits what this client accepts. That limit is stated in two places
    // in two languages; a silent divergence would ship packages every client
    // rejects, and packaging would still succeed. Same shape as
    // PackageIdMatchesPackagingScript.
    [Fact]
    public void NuspecMaxBytesMatchesPackagingScript()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-velopack.ps1"));

        // Both constants, because ValidateNuspec's condition is three checks and
        // the script must mirror all of them: a first pass implemented only the
        // size check, and notes that compress well pass that while failing the
        // ratio the client applies independently.
        Assert.Contains("$maxNuspecBytes = 65536", script, StringComparison.Ordinal);
        Assert.Contains("$maxCompressionRatio = 100", script, StringComparison.Ordinal);
        Assert.Contains("entryCompressed", script, StringComparison.Ordinal);
        Assert.Equal(65_536, UpdateFlow.MaxNuspecBytes);
        Assert.Equal(100L, UpdateFlow.MaxCompressionRatio);
    }
}
