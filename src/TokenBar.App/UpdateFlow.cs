using System.IO.Compression;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using TokenBar.Core;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace TokenBar.App;

/// <summary>What a manual "Check now" produced, and the line the settings page
/// shows for it.
///
/// This is the *check* half only. ALIGN-2 in the approved plan commits to a
/// Sparkle-style dialog carrying version, changelog, Update, Remind me later
/// and Skip; that is still to build, and this does not stand in for it.
///
/// Reporting the check in place is right on its own terms — a button that runs
/// for a few seconds and then shows nothing when you are already current reads
/// as broken, and gets pressed again. What it does not cover is what happens
/// after a version IS found, which is where the dialog belongs.
///
/// Lives beside UpdateFlow because the text mapping is pure and this file is
/// already compiled into TokenBar.Core.Tests; SettingsWindow.cs is not.</summary>
internal enum UpdateCheckState
{
    Checking,
    UpToDate,
    Available,
    Failed,

    /// <summary>The running copy was not installed by Velopack, so there is
    /// no feed for it to check. A Scoop install is the case that exists:
    /// Scoop extracts the package payload into its own versioned directory
    /// and updates it itself. Distinct from <see cref="Failed"/> because
    /// nothing went wrong — reporting "could not check" would send someone
    /// looking for a network or permissions problem that is not there.
    /// </summary>
    Unmanaged,

    /// <summary>GitHub refused the release query because the caller's IP ran
    /// out of unauthenticated API requests (60 an hour, shared by every
    /// machine behind the same address). Distinct from <see cref="Failed"/>
    /// because it clears on its own and needs nothing fixed.</summary>
    RateLimited,
}

/// <summary>Raised when the running copy was not installed by the in-app
/// updater. Its own type rather than a message on InvalidOperationException,
/// because the caller has to tell it apart from a genuinely broken install to
/// choose between two different things to say to the user.</summary>
internal sealed class UnmanagedInstallException()
    : InvalidOperationException("This copy was not installed by the in-app updater.");

internal readonly record struct UpdateCheckResult(UpdateCheckState State, string? Version)
{
    internal static UpdateCheckResult Checking => new(UpdateCheckState.Checking, null);

    internal static UpdateCheckResult UpToDate => new(UpdateCheckState.UpToDate, null);

    internal static UpdateCheckResult Failed => new(UpdateCheckState.Failed, null);

    internal static UpdateCheckResult Unmanaged => new(UpdateCheckState.Unmanaged, null);

    internal static UpdateCheckResult RateLimited => new(UpdateCheckState.RateLimited, null);

    /// <summary>Maps a failed check to what the user is told. Velopack 1.2.0's
    /// GithubSource fetches the release list with HttpClient.GetStringAsync,
    /// which throws HttpRequestException carrying the status code; GitHub
    /// answers an exhausted unauthenticated quota with 403 (observed
    /// 2026-09-25, X-RateLimit-Remaining: 0) or 429. A 403 for another reason
    /// is not expected on a public repository's release list.</summary>
    internal static UpdateCheckResult FromFailure(Exception exception) =>
        exception is HttpRequestException
        {
            StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests,
        }
            ? RateLimited
            : Failed;

    /// <summary>The only intended way to reach <see cref="UpdateCheckState
    /// .Available"/>, and it refuses an empty version: ValidateTarget has
    /// already rejected those, so an empty string here means a caller bypassed
    /// it rather than a release that genuinely has no version. Reporting a
    /// failure is honest; rendering "Update available: v" is not.</summary>
    internal static UpdateCheckResult Available(string version) =>
        string.IsNullOrEmpty(version)
            ? Failed
            : new(UpdateCheckState.Available, version);

    internal string Text() => State switch
    {
        UpdateCheckState.Checking => "Checking for updates…".Localized(),
        UpdateCheckState.UpToDate => "You are up to date.".Localized(),
        UpdateCheckState.Available =>
            "Update available: v{0}".Localized(Version ?? string.Empty),
        UpdateCheckState.Unmanaged =>
            "Updates are handled by whatever installed this copy.".Localized(),
        UpdateCheckState.RateLimited =>
            "GitHub is limiting update checks. Try again later.".Localized(),
        _ => "Could not check for updates.".Localized(),
    };
}

internal class UpdateFlow
{
    internal const string RepositoryUrl =
        "https://github.com/Nanako0129/Syrtis-Windows";
    /// <summary>Durable Velopack package identity. Independent of
    /// $(TbProductName): a future product rename must not move it by
    /// accident. PackageIdMatchesPackagingScript pins the two together.</summary>
    internal const string PackageId = "Nyanako.Syrtis";
    /// <summary>Internal rather than private so NuspecMaxBytesMatchesPackagingScript
    /// can pin it against the packaging script, which embeds release notes into
    /// the nuspec and must not produce one this rejects.</summary>
    internal const int MaxNuspecBytes = 65_536;
    /// <summary>Internal for the same reason as <see cref="MaxNuspecBytes"/>:
    /// the packaging script mirrors ValidateNuspec's whole condition, and a
    /// test pins both constants against it.</summary>
    internal const long MaxCompressionRatio = 100;

    /// <summary>
    /// Exact Velopack channels accepted for installed products. Full and Lite
    /// share architecture mapping; any other string fails closed.
    /// </summary>
    internal static readonly HashSet<string> AcceptedChannels = new(StringComparer.Ordinal)
    {
        "win-x64",
        "win-x64-lite",
        "win-arm64",
        "win-arm64-lite",
    };

    private readonly ManagedUpdateManager _manager;
    private int _downloadActive;

    internal UpdateFlow(
        IFileDownloader? downloader = null,
        IVelopackLocator? locator = null)
    {
        var source = new GithubSource(
            RepositoryUrl,
            accessToken: null,
            prerelease: false,
            downloader);
        var options = new UpdateOptions
        {
            ExplicitChannel = null,
            AllowVersionDowngrade = false,
        };
        _manager = new ManagedUpdateManager(source, options, locator);
    }

    internal async Task<UpdateCandidate?> CheckForUpdatesAsync()
    {
        _ = GetInstallation();
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null ? null : ValidateTarget(update);
    }

    /// <param name="progress">Download percent, 0-100. Velopack has always
    /// offered this; passing null meant Install produced no feedback at all
    /// until the process exited.</param>
    internal async Task DownloadAndVerifyAsync(
        UpdateCandidate candidate,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Interlocked.CompareExchange(ref _downloadActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("An update download is already active.");
        }

        try
        {
            var validated = ValidateCandidate(candidate);
            await _manager.DownloadUpdatesAsync(
                validated.Update,
                progress is null ? null : progress.Report,
                cancellationToken).ConfigureAwait(false);

            validated = ValidateCandidate(candidate);
            await _manager.VerifyChecksumAsync(
                validated.Target,
                validated.PackagePath).ConfigureAwait(false);
            ValidateNuspec(validated);
        }
        catch
        {
            _manager.CleanAllPackages();
            throw;
        }
        finally
        {
            Volatile.Write(ref _downloadActive, 0);
        }
    }

    internal bool TryHandoff(
        UpdateCandidate candidate,
        Func<bool> canHandoff,
        Action quit)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(canHandoff);
        ArgumentNullException.ThrowIfNull(quit);

        var validated = ValidateCandidate(candidate);
        if (!canHandoff())
        {
            return false;
        }

        // silent, because the dialog is still on screen showing "Installing
        // update…" when this runs. Velopack's own apply UI would be the second
        // window this whole slice exists to remove.
        WaitExitThenApplyUpdates(
            validated.Target,
            silent: true,
            restart: true,
            Array.Empty<string>());
        quit();
        return true;
    }

    protected virtual void WaitExitThenApplyUpdates(
        VelopackAsset target,
        bool silent,
        bool restart,
        string[] restartArgs)
    {
        _manager.WaitExitThenApplyUpdates(target, silent, restart, restartArgs);
    }

    private UpdateCandidate ValidateCandidate(UpdateCandidate candidate)
    {
        var validated = ValidateTarget(candidate.Update);
        if (!ReferenceEquals(validated.Target, candidate.Target)
            || !string.Equals(validated.Version, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(validated.Channel, candidate.Channel, StringComparison.Ordinal)
            || !string.Equals(validated.Architecture, candidate.Architecture, StringComparison.Ordinal)
            || !string.Equals(validated.PackagePath, candidate.PackagePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update target changed after validation.");
        }

        return validated;
    }

    private UpdateCandidate ValidateTarget(UpdateInfo update)
    {
        var installation = GetInstallation();
        var target = update.TargetFullRelease
            ?? throw new InvalidDataException("Update target is missing.");
        var version = target.Version?.ToNormalizedString()
            ?? throw new InvalidDataException("Update version is missing.");

        if (target.Type != VelopackAssetType.Full
            || !string.Equals(target.PackageId, PackageId, StringComparison.Ordinal)
            || target.Version <= installation.Version
            || target.Version.IsPrerelease
            || version.Length is 0 or > PendingUpdateAction.MaxVersionLength
            || version.Any(char.IsControl)
            || !IsSha256(target.SHA256)
            || target.Size <= 0)
        {
            throw new InvalidDataException("Update target metadata is invalid.");
        }

        var fileName = $"{PackageId}-{version}-{installation.Channel}-full.nupkg";
        if (!string.Equals(target.FileName, fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update filename is invalid.");
        }

        return new UpdateCandidate(
            update,
            target,
            version,
            installation.Version.ToNormalizedString(),
            BoundNotes(target.NotesMarkdown),
            installation.Channel,
            installation.Architecture,
            Path.Combine(installation.PackagesDirectory, fileName));
    }

    /// <summary>Release notes are the one part of the feed that reaches the
    /// user <em>before</em> any download, and nothing else in this class
    /// covers them: the checks above and <see cref="ValidateNuspec"/> protect
    /// the package — hash, filename, nuspec fields — and a candidate that
    /// would ultimately fail the nuspec check has already had its notes
    /// rendered in front of the user. So the size bound belongs here, in the
    /// established failure shape, and <see cref="UpdateCandidate"/> only ever
    /// carries a value the dialog can safely take.
    ///
    /// <para><b>Drop the notes, keep the update.</b> Failing the candidate on
    /// over-long notes would make the notes field a lever for denying updates
    /// entirely — a publisher, or anyone who can serve that feed, could stop
    /// every client from updating by padding one string.</para></summary>
    private static string? BoundNotes(string? notes) =>
        notes is null || notes.Length > ReleaseNotesMarkdown.MaxInputChars
            ? null
            : notes;

    private Installation GetInstallation()
    {
        // Two different situations, deliberately not collapsed. Not installed
        // by Velopack at all means someone else manages this copy — Scoop
        // extracts the package payload and versions it itself. A Velopack
        // install whose AppId disagrees is the stranded case
        // docs/release-velopack.md records for 0.2.0/0.2.1, where the pack id
        // changed and the install can no longer find its own feed. The first
        // is normal, the second needs a reinstall, and the user is told
        // different things.
        if (!_manager.IsInstalled)
        {
            throw new UnmanagedInstallException();
        }

        if (!string.Equals(_manager.AppId, PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Installed package identity is invalid.");
        }

        var version = _manager.CurrentVersion
            ?? throw new InvalidOperationException("Installed version is missing.");
        var channel = _manager.InstalledChannel
            ?? throw new InvalidOperationException("Installed channel is missing.");
        var architecture = MapChannelToArchitecture(channel);
        var packagesDirectory = _manager.PackagesDirectory;
        if (string.IsNullOrWhiteSpace(packagesDirectory))
        {
            throw new InvalidOperationException("Installed packages path is missing.");
        }

        return new Installation(version, channel, architecture, packagesDirectory);
    }

    /// <summary>
    /// Maps an installed Velopack channel to PE architecture. Fail-closed for
    /// near-miss and unknown channels (including null/empty).
    /// </summary>
    internal static string MapChannelToArchitecture(string? channel)
    {
        if (channel is null
            || !AcceptedChannels.Contains(channel))
        {
            throw new InvalidOperationException("Installed channel is invalid.");
        }

        return channel.StartsWith("win-arm64", StringComparison.Ordinal)
            ? "arm64"
            : "x64";
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private static void ValidateNuspec(UpdateCandidate candidate)
    {
        using var archive = ZipFile.OpenRead(candidate.PackagePath);
        var entries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException("Package must contain exactly one nuspec.");
        }

        var entry = entries[0];
        if (entry.Length is < 1 or > MaxNuspecBytes
            || entry.CompressedLength is < 1 or > MaxNuspecBytes
            || entry.Length > entry.CompressedLength * MaxCompressionRatio)
        {
            throw new InvalidDataException("Nuspec size is invalid.");
        }

        var bytes = new byte[MaxNuspecBytes + 1];
        var length = 0;
        using (var stream = entry.Open())
        {
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }

        if (length is < 1 or > MaxNuspecBytes || length != entry.Length)
        {
            throw new InvalidDataException("Nuspec content length is invalid.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxNuspecBytes,
            MaxCharactersFromEntities = 0,
        };
        using var input = new MemoryStream(bytes, 0, length, writable: false);
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root?.Name.LocalName != "package")
        {
            throw new InvalidDataException("Nuspec package root is invalid.");
        }

        var metadata = SingleElement(root, "metadata");
        RequireValue(metadata, "id", PackageId);
        RequireValue(metadata, "version", candidate.Version);
        RequireValue(metadata, "channel", candidate.Channel);
        RequireValue(metadata, "machineArchitecture", candidate.Architecture);
    }

    private static XElement SingleElement(XElement parent, string name)
    {
        var elements = parent.Elements()
            .Where(element => element.Name.LocalName == name)
            .ToArray();
        return elements.Length == 1
            ? elements[0]
            : throw new InvalidDataException($"Nuspec {name} is invalid.");
    }

    private static void RequireValue(XElement metadata, string name, string expected)
    {
        var element = SingleElement(metadata, name);
        if (!string.Equals(element.Value, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Nuspec {name} is invalid.");
        }
    }

    private sealed class ManagedUpdateManager(
        IUpdateSource source,
        UpdateOptions options,
        IVelopackLocator? locator)
        : UpdateManager(source, options, locator)
    {
        internal string? InstalledChannel => Locator.Channel;
        internal string? PackagesDirectory => Locator.PackagesDir;

        internal Task VerifyChecksumAsync(VelopackAsset target, string packagePath) =>
            VerifyPackageChecksumAsync(target, packagePath);

        internal void CleanAllPackages() => CleanPackagesExcept(null);
    }

    private sealed record Installation(
        SemanticVersion Version,
        string Channel,
        string Architecture,
        string PackagesDirectory);
}

/// <summary><paramref name="InstalledVersion"/> and <paramref name="Notes"/>
/// exist for the update dialog: its version line names both versions, and its
/// middle band is the only thing it has that a tray notification does not.
/// Both are display-only — nothing in the download or hand-off path reads
/// them — and <c>ValidateCandidate</c>'s "nothing moved" comparison
/// deliberately still covers only the fields that decide what gets installed.
/// </summary>
internal sealed record UpdateCandidate(
    UpdateInfo Update,
    VelopackAsset Target,
    string Version,
    string InstalledVersion,
    string? Notes,
    string Channel,
    string Architecture,
    string PackagePath);
