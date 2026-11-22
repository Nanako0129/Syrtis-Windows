using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

// The Settings "Devices" page model (SA-4f), ported from macOS DevicesModel
// (DevicesModel.swift :1-725, Syrtis f8a1a0ae): sharing this PC, the scan-scope
// consent and pairing. Every decision about sharing, consent, pairing and
// grants is made by the embedded agent (Rust remote_agent.rs); this model only
// calls the TbCore.Remote* wrappers, keeps what they answer and maps their
// fixed error classes to copy. It computes no digest and authorizes nothing.
// UI-free: the Settings page (SettingsWindow.Devices.cs) renders it, and the
// tests drive it with a fake client, a fake clipboard and a fake clock.

/// <summary>The <c>TbCore.Remote*</c> calls the page makes. Every member
/// BLOCKS; the model calls them on the thread pool (<see cref="PairPoll"/> on
/// a thread of its own). There is deliberately no stop: "stop sharing" is
/// <see cref="SetSharing"/>(false), never <c>RemoteStop</c> (R6).</summary>
public interface IDevicesRemoteClient
{
    RemoteAgentStatus Start();
    RemoteAgentStatus Status();
    void SetSharing(bool enabled);
    RemoteAgentStatus ListenConfigure(string host, int port, string? nodeLabel);
    RemoteScopeDisclosure ScopeDisclosure();
    void ScopeApprove(string ownerInputsSha256);
    RemoteOffer OfferCreate(long ttlSeconds);
    void OfferCancel(string offerId);
    IReadOnlyList<RemotePending> PendingList();
    void Approve(string pendingId);
    void Reject(string pendingId);
    IReadOnlyList<RemoteGrant> Grants();
    void Revoke(string grantId);
    /// <summary>Where <see cref="PairClaim"/> would connect; parses only.</summary>
    RemotePairDestination PairDestination(string pairUrl);
    RemoteClaim PairClaim(string pairUrl);
    /// <summary>Blocks up to the frozen 960 s poll window; no cancellation in the ABI.</summary>
    RemotePaired PairPoll(long handle);
    IReadOnlyList<RemotePeer> Peers();
    void PeerRemove(string producerStatic);
}

/// <summary>The production client: the 4d wrappers. Tests never construct it.</summary>
public sealed class TbDevicesClient : IDevicesRemoteClient
{
    public RemoteAgentStatus Start() => TbCore.RemoteStart();
    public RemoteAgentStatus Status() => TbCore.RemoteStatus();
    public void SetSharing(bool enabled) => TbCore.RemoteSetSharing(enabled);
    public RemoteAgentStatus ListenConfigure(string host, int port, string? nodeLabel) =>
        TbCore.RemoteListenConfigure(host, port, nodeLabel);
    public RemoteScopeDisclosure ScopeDisclosure() => TbCore.RemoteScopeDisclosure();
    public void ScopeApprove(string ownerInputsSha256) => TbCore.RemoteScopeApprove(ownerInputsSha256);
    public RemoteOffer OfferCreate(long ttlSeconds) => TbCore.RemoteOfferCreate(ttlSeconds);
    public void OfferCancel(string offerId) => TbCore.RemoteOfferCancel(offerId);
    public IReadOnlyList<RemotePending> PendingList() => TbCore.RemotePendingList();
    public void Approve(string pendingId) => TbCore.RemoteApprove(pendingId, 1);
    public void Reject(string pendingId) => TbCore.RemoteReject(pendingId, 1);
    public IReadOnlyList<RemoteGrant> Grants() => TbCore.RemoteGrants();
    public void Revoke(string grantId) => TbCore.RemoteRevoke(grantId);
    public RemotePairDestination PairDestination(string pairUrl) => TbCore.RemotePairDestination(pairUrl);
    public RemoteClaim PairClaim(string pairUrl) => TbCore.RemotePairClaim(pairUrl);
    public RemotePaired PairPoll(long handle) => TbCore.RemotePairPoll(handle);
    public IReadOnlyList<RemotePeer> Peers() => TbCore.RemotePeers();
    public void PeerRemove(string producerStatic) => TbCore.RemotePeerRemove(producerStatic);
}

/// <summary>One clipboard write. The flags map one to one onto WinRT
/// <c>ClipboardContentOptions</c> (<c>IsAllowedInHistory</c>,
/// <c>IsRoamable</c>). <see cref="Text"/> is a secret: never printed.</summary>
public sealed record ClipboardWriteRequest(string Text, bool AllowHistory, bool Roamable)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AllowHistory = ").Append(AllowHistory).Append(", Roamable = ").Append(Roamable);
        return true;
    }
}

/// <summary>Where the Pair URL is copied to. Injected so tests never touch
/// the real clipboard. <see cref="SequenceNumber"/> is user32
/// <c>GetClipboardSequenceNumber</c> in the app: the model never reads the
/// clipboard's content back.</summary>
public interface IDevicesClipboard
{
    uint SequenceNumber { get; }

    /// <summary>Writes the request; returns the sequence number right after.</summary>
    uint Write(ClipboardWriteRequest request);

    void Clear();
}

public enum PairingKind { Idle, Previewing, Confirming, Claiming, Waiting, Linked, Failed }

/// <summary>The receiver's pairing state (macOS <c>DevicesModel.Pairing</c>).
/// <see cref="Claim"/> holds the SAS, which <see cref="RemoteClaim"/> never
/// prints. <see cref="Failure"/> is a <see cref="DevicesCopy"/> key.</summary>
public sealed record DevicesPairing(
    PairingKind Kind,
    RemotePairDestination? Destination = null,
    RemoteClaim? Claim = null,
    string? Fingerprint = null,
    string? Failure = null)
{
    public static DevicesPairing Idle { get; } = new(PairingKind.Idle);
}

/// <summary>The listen-address menu (macOS <c>ListenAddress</c>, DM :106-208).
/// Only this PC's own non-loopback addresses and "this PC only"; never an
/// unspecified address (<c>0.0.0.0</c>, <c>::</c>).</summary>
public static class ListenAddress
{
    public const string LocalOnly = "127.0.0.1";

    /// <summary><see cref="LocalOnly"/> first, then each usable address once,
    /// in interface order, then each of <paramref name="keeping"/> (the
    /// configured address, the current selection) not listed yet. Offered from
    /// the interfaces: IPv4 and IPv6 unique-local (<c>fc00::/7</c>); a global
    /// IPv6 address stays only through <paramref name="keeping"/>.
    /// ponytail: no way to pick a new global IPv6 address, as on macOS.</summary>
    public static IReadOnlyList<string> Choices(IEnumerable<string> addresses, IEnumerable<string>? keeping = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { LocalOnly };
        var output = new List<string> { LocalOnly };
        foreach (var address in addresses)
        {
            if (Usable(address) is { } bytes && (bytes.Length == 4 || IsUniqueLocalV6(bytes)) && seen.Add(address))
            {
                output.Add(address);
            }
        }

        foreach (var address in keeping ?? [])
        {
            if (Usable(address) is not null && seen.Add(address))
            {
                output.Add(address);
            }
        }

        return output;
    }

    /// <summary>A public IPv4 address or a global IPv6 one (2000::/3).
    /// Carrier-grade NAT (100.64/10, Tailscale) counts as local.</summary>
    public static bool MayBePublic(string address)
    {
        if (Bytes(address) is not { } bytes) { return false; }
        if (bytes.Length == 16) { return (bytes[0] & 0xE0) == 0x20; }
        var (a, b) = (bytes[0], bytes[1]);
        var local = a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31)
            || (a == 192 && b == 168) || (a == 169 && b == 254)
            || (a == 100 && b is >= 64 and <= 127);
        return !local && !IsUnspecified(bytes);
    }

    /// <summary>This PC's addresses: interfaces that are up and not loopback,
    /// IPv4 or IPv6, as numeric text (link-local IPv6 keeps its <c>%zone</c>,
    /// which <see cref="Choices"/> drops). Never throws.</summary>
    public static IReadOnlyList<string> InterfaceAddresses()
    {
        var output = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        output.Add(unicast.Address.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // An empty list leaves "This PC only" and the configured address.
        }

        return output;
    }

    private static byte[]? Usable(string address) =>
        !address.Contains('%') && Bytes(address) is { } bytes && !IsUnspecified(bytes) && !IsLoopback(bytes)
            ? bytes : null;

    /// <summary>Strict numeric text, as <c>inet_pton</c>: four dotted decimal
    /// parts for IPv4 (IPAddress.TryParse alone also takes "1" or "1.2"), or
    /// an IPv6 literal without a zone.</summary>
    private static byte[]? Bytes(string address)
    {
        if (address.Contains('%') || !IPAddress.TryParse(address, out var parsed)) { return null; }
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = address.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit))
                ? parsed.GetAddressBytes() : null;
        }

        return parsed.AddressFamily == AddressFamily.InterNetworkV6 && address.Contains(':')
            ? parsed.GetAddressBytes() : null;
    }

    private static bool IsUniqueLocalV6(byte[] bytes) => bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;

    private static bool IsUnspecified(byte[] bytes) => bytes.All(b => b == 0);

    private static bool IsLoopback(byte[] bytes) =>
        bytes.Length == 4 ? bytes[0] == 127 : bytes.Take(15).All(b => b == 0) && bytes[15] == 1;
}

/// <summary>
/// The Settings → Devices state (macOS <c>DevicesModel</c>). One instance,
/// owned by the Settings window, so a pairing wait survives a page rebuild.
/// Thread-agnostic: every field is read and written under one lock, every
/// blocking client call runs on the thread pool, and <see cref="Changed"/>
/// fires on whichever thread changed the state (the page marshals it to the
/// UI thread). Nothing here logs.
///
/// <para>Known limitations (decided with the Syrtis session, same on macOS):
/// a grant lasts 100 years, so in practice it never expires and no date is
/// shown; only one pairing runs at a time, and an unreachable producer delays
/// other claims and polls until they time out; after an identity change a
/// poll may report <c>transport</c> or <c>unauthorized</c> instead of
/// <c>identityChanged</c>.</para>
///
/// <para>R9: the Pair URL lives only in <see cref="Offer"/> (and the
/// clipboard while copied), the pasted invitation only in a private field
/// between its preview and Link, the SAS only inside <see cref="RemotePending"/>
/// and <see cref="RemoteClaim"/>, whose printing leaves them out. This
/// type's <see cref="ToString"/> is a constant.</para>
/// </summary>
public sealed class DevicesModel
{
    /// <summary>How often the page re-reads status, pending requests, grants
    /// and peers while it is shown.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>Invitation lifetime. The agent takes 1–900 s; 600 is its CLI default.</summary>
    public const long OfferTtlSeconds = 600;

    /// <summary>The longest Pair URL the field accepts (the contract's bound), in UTF-8 bytes.</summary>
    public const int PairUrlMaxLength = 16_384;

    /// <summary>The agent's node-label bound (NODE_LABEL_MAX_BYTES).</summary>
    public const int NodeLabelMaxBytes = 128;

    public const int PortMin = 1024;
    public const int PortMax = 65535;

    private readonly object _gate = new();
    private readonly IDevicesRemoteClient _client;
    private readonly IDevicesClipboard _clipboard;
    private readonly IDeviceClock _clock;
    private readonly Func<(DeviceOverlaySnapshot Snapshot, IReadOnlyDictionary<string, string> LearnedNames)>? _overlay;

    private RemoteAgentStatus? _status;
    private string? _startFailure;
    private string? _sharingError;
    private string? _listenError;
    private string? _scopeNotice;
    private string? _offerError;
    private string? _decideError;
    private string? _manageError;
    private RemoteScopeDisclosure? _disclosure;
    private RemoteOffer? _offer;
    private bool _copied;
    private IReadOnlyList<RemotePending> _pending = [];
    private IReadOnlyList<RemoteGrant> _grants = [];
    private IReadOnlyList<RemotePeer> _peers = [];
    private DevicesPairing _pairing = DevicesPairing.Idle;
    private readonly HashSet<string> _deciding = new(StringComparer.Ordinal);
    private bool _working;

    /// <summary>The clipboard sequence number right after the Pair URL was
    /// copied; the clear empties the clipboard only if nothing changed it since.</summary>
    private uint? _copiedSequence;
    /// <summary>Claims whose wait the user cancelled; a later success is removed.</summary>
    private readonly HashSet<long> _cancelledHandles = [];
    private CancellationTokenSource? _poll;
    /// <summary>Bumped by every create and by <see cref="PageDisappeared"/>: a
    /// create publishes only if it is still the latest.</summary>
    private int _offerGeneration;
    /// <summary>The pasted invitation between its preview and the user's
    /// choice; the very text whose destination is on screen. A secret.</summary>
    private string? _heldPairUrl;
    private int _previewGeneration;

    public DevicesModel(
        IDevicesRemoteClient client, IDevicesClipboard clipboard, IDeviceClock? clock = null,
        Func<(DeviceOverlaySnapshot Snapshot, IReadOnlyDictionary<string, string> LearnedNames)>? overlay = null)
    {
        _client = client;
        _clipboard = clipboard;
        _clock = clock ?? new SystemDeviceClock();
        _overlay = overlay;
    }

    /// <summary>Any state changed. Fires outside the lock, on the thread that
    /// changed it; each handler's exception is swallowed so one subscriber
    /// cannot stop the polling.</summary>
    public event Action? Changed;

    /// <summary>A link succeeded or a linked device was removed here: the
    /// dashboard reloads its peer list at once (SA-4g wires it).</summary>
    public event Action? PeersChanged;

    // Never the Pair URL or a SAS: interpolation and logging print this.
    public override string ToString() => "DevicesModel";

    // ---- State (each read is a consistent value under the lock) -----------

    public RemoteAgentStatus? Status => Read(() => _status);
    public string? StartFailure => Read(() => _startFailure);
    public string? SharingError => Read(() => _sharingError);
    public string? ListenError => Read(() => _listenError);
    public string? ScopeNotice => Read(() => _scopeNotice);
    public string? OfferError => Read(() => _offerError);
    public string? DecideError => Read(() => _decideError);
    public string? ManageError => Read(() => _manageError);
    public RemoteScopeDisclosure? Disclosure => Read(() => _disclosure);
    public RemoteOffer? Offer => Read(() => _offer);
    public bool Copied => Read(() => _copied);
    public IReadOnlyList<RemotePending> Pending => Read(() => _pending);
    public IReadOnlyList<RemoteGrant> Grants => Read(() => _grants);
    public IReadOnlyList<RemotePeer> Peers => Read(() => _peers);
    public DevicesPairing Pairing => Read(() => _pairing);
    public bool IsDeciding(string pendingId) => Read(() => _deciding.Contains(pendingId));
    public bool Working => Read(() => _working);
    public bool Polling => Read(() => _poll is not null);

    public bool Running => Status?.Running == true;
    public bool SharingOn => Status?.SharingEnabled == true;
    public bool ScopeApproved => Status?.ScopeApproved == true;

    /// <summary>On only once the scope is approved; off always.</summary>
    public bool CanToggleSharing => Running && (SharingOn || ScopeApproved);

    /// <summary>No address, or one set that does not listen.</summary>
    public bool Unreachable => Status is not { } s || s.BindHost is null || s.Listening != true;

    public bool ShowsRetry => !Running;

    /// <summary>Whether an invitation is held for confirmation (never the text).</summary>
    public bool HoldsPairUrl => Read(() => _heldPairUrl is not null);

    /// <summary>The short status under a linked device, from what the
    /// dashboard last learned; null (the line hidden) while no provider is
    /// wired (4f) or the dashboard has no result for it.</summary>
    public DeviceOverlayCopy.ShortStatusResult? ShortStatus(RemotePeer peer) =>
        _overlay?.Invoke().Snapshot.SettingsStatus(peer, _clock.Now);

    /// <summary>The linked device's name line: the newest name the dashboard
    /// learned, else its own label; null without either (DSV :436-438).</summary>
    public string? NameLine(RemotePeer peer) =>
        NameLineFor(peer, _overlay?.Invoke().LearnedNames);

    public static string? NameLineFor(RemotePeer peer, IReadOnlyDictionary<string, string>? learned) =>
        learned is not null && learned.TryGetValue(peer.Fingerprint, out var name) ? name : peer.NodeLabel;

    /// <summary><c>host:port</c>; an IPv6 host in brackets (DSV :443-450).</summary>
    public static string Address(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    public static string Address(RemotePeer peer) => Address(peer.Host, peer.Port);

    /// <summary>m:ss until <paramref name="expiresAtMs"/>, rounded up, never negative.</summary>
    public static string Remaining(long expiresAtMs, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(expiresAtMs / 1000.0 - now.ToUnixTimeMilliseconds() / 1000.0));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    public string RemainingForOffer(RemoteOffer offer) => Remaining(offer.ExpiresAt, _clock.Now);

    /// <summary>Each variable once, in the order given (DSV :452-458).</summary>
    public static string VariableNames(IEnumerable<RemoteEnvRoot> roots) =>
        string.Join(", ", roots.Select(r => r.Variable).Distinct(StringComparer.Ordinal));

    /// <summary>"client: path" per path, clients sorted (DSV :460-462).</summary>
    public static IReadOnlyList<string> Flatten(IReadOnlyDictionary<string, IReadOnlyList<string>> paths) =>
        [.. paths.Keys.Order(StringComparer.Ordinal).SelectMany(client => paths[client].Select(p => $"{client}: {p}"))];

    /// <summary>The "Data folder" card: <c>DataDir</c>, then <c>XdgDataHome</c>
    /// as a second path when the disclosure carries one (Windows only; no new
    /// copy). <c>SourceCacheDir</c> is not shown, as on macOS.</summary>
    public static IReadOnlyList<string> DataFolderPaths(RemoteScopeDisclosure disclosure) =>
        disclosure.XdgDataHome is { Length: > 0 } xdg && xdg != disclosure.DataDir
            ? [disclosure.DataDir, xdg]
            : [disclosure.DataDir];

    // ---- Page lifetime ----------------------------------------------------

    /// <summary>The Devices page became visible. Starts the 2 s poll (the
    /// disclosure first); a second call while it runs does nothing, so a
    /// rebuild or a repeated show never runs two loops.</summary>
    public void PageAppeared()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_poll is not null) { return; }
            _poll = new CancellationTokenSource();
            token = _poll.Token;
        }

        _ = PollLoop(token);
    }

    /// <summary>Stops the polling and clears the invitation, the held
    /// paste and the clipboard if it still holds the Pair URL. A pairing wait
    /// goes on: its result is shown when the page comes back. Returns the
    /// offer cancel running in the background, or null.</summary>
    public Task? PageDisappeared()
    {
        lock (_gate)
        {
            _poll?.Cancel();
            _poll = null;
            // A create still in flight must not publish its offer afterwards.
            _offerGeneration++;
            DropHeldInvitationLocked();
        }

        var task = ClearOffer();
        Notify();
        return task;
    }

    private async Task PollLoop(CancellationToken token)
    {
        await LoadDisclosure(token).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            await Refresh(token).ConfigureAwait(false);
            try
            {
                await _clock.Delay(RefreshInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- Status -----------------------------------------------------------

    public Task RefreshAsync() => Refresh(CancellationToken.None);

    /// <summary>Every call is followed by a cancellation check: when the page
    /// goes away mid-refresh, the rest of that refresh makes no call and
    /// writes nothing (macOS :383-422).</summary>
    private async Task Refresh(CancellationToken token)
    {
        ExpireOfferIfDue(_clock.Now.ToUnixTimeMilliseconds());
        var result = await Call(c => c.Status()).ConfigureAwait(false);
        if (token.IsCancellationRequested) { return; }
        if (!result.Ok)
        {
            Set(() => _status = null);
            return;
        }

        var next = result.Value!;
        bool wasApproved = false, loadDisclosure = false;
        Set(() =>
        {
            wasApproved = _status?.ScopeApproved == true;
            _status = next;
            if (!next.Running)
            {
                _pending = [];
                _grants = [];
                _peers = [];
                return;
            }

            _startFailure = null;
            if (_disclosure is null)
            {
                loadDisclosure = true;
            }
            else if (wasApproved && next.ScopeApproved != true)
            {
                // The agent restarted over changed inputs: show the new
                // scope and say so, rather than keep the old one on screen.
                _scopeNotice = DevicesCopy.ScopeChanged;
                loadDisclosure = true;
            }
        });
        if (!next.Running) { return; }
        if (loadDisclosure) { await LoadDisclosure(token).ConfigureAwait(false); }
        if (token.IsCancellationRequested) { return; }
        var pending = await Call(c => c.PendingList()).ConfigureAwait(false);
        if (token.IsCancellationRequested) { return; }
        if (pending.Ok) { Set(() => _pending = pending.Value!); }
        var grants = await Call(c => c.Grants()).ConfigureAwait(false);
        if (token.IsCancellationRequested) { return; }
        if (grants.Ok) { Set(() => _grants = grants.Value!); }
        var peers = await Call(c => c.Peers()).ConfigureAwait(false);
        if (token.IsCancellationRequested) { return; }
        if (peers.Ok) { Set(() => _peers = peers.Value!); }
    }

    public async Task RetryStart()
    {
        Set(() => _working = true);
        try
        {
            var result = await Call(c => c.Start()).ConfigureAwait(false);
            Set(() => _startFailure = result.Ok ? null : DevicesCopy.StartFailure(result.Reason));
            await RefreshAsync().ConfigureAwait(false);
        }
        finally
        {
            Set(() => _working = false);
        }
    }

    // ---- Sharing and address ----------------------------------------------

    /// <summary>On or off as the switch asks: off is <c>sharing_set(false)</c>,
    /// never a stop (R6). The switch then shows what the core says after a
    /// fresh read, not what was asked (no optimistic update).</summary>
    public async Task SetSharing(bool enabled)
    {
        Set(() => _sharingError = null);
        var result = await Call(c => { c.SetSharing(enabled); return true; }).ConfigureAwait(false);
        if (!result.Ok) { Set(() => _sharingError = DevicesCopy.SharingFailure(result.Reason)); }
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>The cleaned name (<see cref="DeviceNameCleaning.CleanedName"/>)
    /// is what is sent and what the page writes back to the field.</summary>
    public async Task ApplyListen(string host, int port, string nodeLabel)
    {
        Set(() => _listenError = null);
        if (port is < PortMin or > PortMax)
        {
            Set(() => _listenError = DevicesCopy.PortRangeLine);
            return;
        }

        var label = DeviceNameCleaning.CleanedName(nodeLabel);
        if (label is not null && Encoding.UTF8.GetByteCount(label) > NodeLabelMaxBytes)
        {
            Set(() => _listenError = DevicesCopy.NameTooLong);
            return;
        }

        var result = await Call(c => c.ListenConfigure(host, port, label)).ConfigureAwait(false);
        Set(() =>
        {
            if (result.Ok) { _status = result.Value; }
            else { _listenError = result.Reason == "invalid" ? DevicesCopy.AddressRefused : DevicesCopy.GenericFailure; }
        });
    }

    // ---- Scan scope -------------------------------------------------------

    public Task LoadDisclosureAsync() => LoadDisclosure(CancellationToken.None);

    private async Task LoadDisclosure(CancellationToken token)
    {
        var result = await Call(c => c.ScopeDisclosure()).ConfigureAwait(false);
        if (token.IsCancellationRequested) { return; }
        if (result.Ok) { Set(() => _disclosure = result.Value); }
    }

    /// <summary>Sends <paramref name="shownDigest"/>, the
    /// <c>OwnerInputsSha256</c> of the disclosure object the page rendered
    /// the paths from, never one read for the click (R4). <c>invalid</c> (the
    /// inputs changed after it was shown): reload and ask again; never resent.</summary>
    public async Task ApproveScope(string shownDigest)
    {
        Set(() => _scopeNotice = null);
        var result = await Call(c => { c.ScopeApprove(shownDigest); return true; }).ConfigureAwait(false);
        if (!result.Ok && result.Reason == "invalid")
        {
            Set(() => _scopeNotice = DevicesCopy.ScopeChanged);
            await LoadDisclosureAsync().ConfigureAwait(false);
        }
        else if (!result.Ok)
        {
            Set(() => _scopeNotice = DevicesCopy.GenericFailure);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    // ---- Invitation (producer) --------------------------------------------

    /// <summary>Only the newest create, and only one the page has not left
    /// since, publishes its offer; an older or orphaned success is cancelled
    /// in the core at once, so no invitation stays claimable out of sight.</summary>
    public async Task CreateOffer()
    {
        int generation;
        lock (_gate)
        {
            generation = ++_offerGeneration;
            _offerError = null;
        }

        if (ClearOffer() is { } previous) { await previous.ConfigureAwait(false); }
        var result = await Call(c => c.OfferCreate(OfferTtlSeconds)).ConfigureAwait(false);
        var stale = false;
        Set(() =>
        {
            if (generation != _offerGeneration)
            {
                stale = true;
                return;
            }

            if (result.Ok) { _offer = result.Value; }
            else { _offerError = DevicesCopy.OfferFailure(result.Reason); }
        });
        if (stale && result.Ok)
        {
            var offerId = result.Value!.OfferId;
            _ = Call(c => { c.OfferCancel(offerId); return true; });
        }
    }

    /// <summary>Copies the Pair URL kept out of the clipboard history and off
    /// cloud sync. UI thread (the clipboard is the UI thread's).</summary>
    public void CopyOffer()
    {
        var offer = Offer;
        if (offer is null) { return; }
        var sequence = _clipboard.Write(new ClipboardWriteRequest(offer.PairUrl, AllowHistory: false, Roamable: false));
        Set(() =>
        {
            _copiedSequence = sequence;
            _copied = true;
        });
    }

    /// <summary>Clears the Pair URL now, empties the clipboard if it still
    /// holds it (its sequence number is the one right after our write; the
    /// secret is never read back), and cancels the invitation in the
    /// background (the returned task).</summary>
    public Task? ClearOffer()
    {
        RemoteOffer? cleared;
        uint? copiedSequence;
        lock (_gate)
        {
            cleared = _offer;
            if (cleared is null) { return null; }
            _offer = null;
            _copied = false;
            copiedSequence = _copiedSequence;
            _copiedSequence = null;
        }

        if (copiedSequence is { } sequence && _clipboard.SequenceNumber == sequence)
        {
            _clipboard.Clear();
        }

        Notify();
        var offerId = cleared.OfferId;
        return Call(c => { c.OfferCancel(offerId); return true; });
    }

    public void ExpireOfferIfDue(long nowMs)
    {
        if (Offer is { } offer && nowMs >= offer.ExpiresAt) { ClearOffer(); }
    }

    // ---- Requests and grants (producer) -----------------------------------

    /// <summary>Approve or reject the request on the row the user clicked:
    /// that row's own <c>PendingId</c>, the one shown with its SAS, never an
    /// index and never a fresh read of the list (R5).</summary>
    public async Task Decide(RemotePending row, bool approve)
    {
        var pendingId = row.PendingId;
        string? shownOffer = null;
        Set(() =>
        {
            _decideError = null;
            _deciding.Add(pendingId);
            shownOffer = _offer?.OfferId;
        });
        try
        {
            var result = await Call(c =>
            {
                if (approve) { c.Approve(pendingId); } else { c.Reject(pendingId); }
                return true;
            }).ConfigureAwait(false);
            if (!result.Ok)
            {
                Set(() => _decideError = result.Reason == "expired" ? DevicesCopy.RequestExpired : DevicesCopy.GenericFailure);
            }
            else if (approve && shownOffer is not null && Offer?.OfferId == shownOffer)
            {
                // An invitation works once and the approved request used the
                // one on screen. ponytail: not matched to the request's own
                // invitation (the pending row does not carry it), as on macOS.
                _ = ClearOffer();
            }
        }
        finally
        {
            Set(() => _deciding.Remove(pendingId));
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task RevokeGrant(RemoteGrant grant)
    {
        Set(() => _manageError = null);
        var grantId = grant.GrantId;
        var result = await Call(c => { c.Revoke(grantId); return true; }).ConfigureAwait(false);
        if (!result.Ok) { Set(() => _manageError = DevicesCopy.GenericFailure); }
        await RefreshAsync().ConfigureAwait(false);
    }

    // ---- Linking (receiver) -----------------------------------------------

    public async Task RemovePeer(RemotePeer peer)
    {
        Set(() => _manageError = null);
        var key = peer.ProducerStatic;
        var result = await Call(c => { c.PeerRemove(key); return true; }).ConfigureAwait(false);
        if (!result.Ok) { Set(() => _manageError = DevicesCopy.GenericFailure); }
        else { RaisePeersChanged(); }
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>Reads where the pasted invitation would connect (no
    /// connection) and shows it for confirmation; <see cref="ConfirmLink"/>
    /// claims. Empty or over 16384 UTF-8 bytes is refused without a call. A
    /// result that comes back after Cancel or after the page left is dropped.</summary>
    public async Task Link(string pairUrl)
    {
        var text = pairUrl.Trim();
        int generation;
        lock (_gate)
        {
            if (_pairing.Kind is PairingKind.Previewing or PairingKind.Confirming
                or PairingKind.Claiming or PairingKind.Waiting)
            {
                return;
            }

            if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > PairUrlMaxLength)
            {
                _pairing = new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.InvalidInvitation);
                generation = -1;
            }
            else
            {
                generation = ++_previewGeneration;
                _heldPairUrl = null;
                _pairing = new DevicesPairing(PairingKind.Previewing);
            }
        }

        Notify();
        if (generation < 0) { return; }
        var result = await Call(c => c.PairDestination(text)).ConfigureAwait(false);
        Set(() =>
        {
            if (generation != _previewGeneration || _pairing.Kind != PairingKind.Previewing) { return; }
            if (result.Ok)
            {
                _heldPairUrl = text;
                _pairing = new DevicesPairing(PairingKind.Confirming, Destination: result.Value);
            }
            else
            {
                _pairing = new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.LinkFailure(result.Reason));
            }
        });
    }

    /// <summary>Drops the invitation shown for confirmation, or a preview in flight.</summary>
    public void CancelLink()
    {
        Set(() =>
        {
            if (_pairing.Kind is PairingKind.Previewing or PairingKind.Confirming) { DropHeldInvitationLocked(); }
        });
    }

    private void DropHeldInvitationLocked()
    {
        _previewGeneration++;
        _heldPairUrl = null;
        if (_pairing.Kind is PairingKind.Previewing or PairingKind.Confirming) { _pairing = DevicesPairing.Idle; }
    }

    /// <summary>Claims the invitation whose destination is on screen, then
    /// waits for the producer's approval on a thread of its own. Only from
    /// Confirming; the text leaves the model and Claiming is set before the
    /// call, so a second press claims nothing.</summary>
    public async Task ConfirmLink()
    {
        string text;
        lock (_gate)
        {
            if (_pairing.Kind != PairingKind.Confirming || _heldPairUrl is not { } held) { return; }
            text = held;
            _heldPairUrl = null;
            _pairing = new DevicesPairing(PairingKind.Claiming);
        }

        Notify();
        var result = await Call(c => c.PairClaim(text)).ConfigureAwait(false);
        if (!result.Ok)
        {
            Set(() => _pairing = new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.LinkFailure(result.Reason)));
            return;
        }

        var claim = result.Value!;
        Set(() => _pairing = new DevicesPairing(PairingKind.Waiting, Claim: claim));
        _ = WaitForApproval(claim);
    }

    private async Task WaitForApproval(RemoteClaim claim)
    {
        // Up to 960 s with no cancellation in the ABI: a dedicated thread,
        // not a pool one.
        var result = await Task.Factory.StartNew(
            () => Invoke(c => c.PairPoll(claim.Handle)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        await PollEnded(claim, result).ConfigureAwait(false);
    }

    /// <summary>Stops showing the wait. The poll runs on to its end; if it
    /// then succeeds, the producer is removed at once (security r1 F2).</summary>
    public void CancelWait()
    {
        Set(() =>
        {
            if (_pairing is { Kind: PairingKind.Waiting, Claim: { } claim })
            {
                _cancelledHandles.Add(claim.Handle);
                _pairing = DevicesPairing.Idle;
            }
        });
    }

    public void DismissPairingResult()
    {
        Set(() =>
        {
            if (_pairing.Kind is PairingKind.Linked or PairingKind.Failed) { _pairing = DevicesPairing.Idle; }
        });
    }

    private async Task PollEnded(RemoteClaim claim, Outcome<RemotePaired> result)
    {
        bool cancelled;
        lock (_gate) { cancelled = _cancelledHandles.Remove(claim.Handle); }
        if (result.Ok && cancelled)
        {
            var key = result.Value!.ProducerStatic;
            var removed = await Call(c => { c.PeerRemove(key); return true; }).ConfigureAwait(false);
            if (!removed.Ok) { Set(() => _manageError = DevicesCopy.CancelledStillLinked); }
        }
        else if (result.Ok)
        {
            var fingerprint = result.Value!.Fingerprint;
            Set(() => _pairing = new DevicesPairing(PairingKind.Linked, Fingerprint: fingerprint));
            RaisePeersChanged();
        }
        else if (!cancelled)
        {
            Set(() => _pairing = new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.LinkFailure(result.Reason)));
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    // ---- Calls ------------------------------------------------------------

    /// <summary>A call's value, or the core's fixed class string
    /// (<see cref="TbCoreException.Message"/>), or "internal" for anything
    /// else: an exception's own message never reaches the copy.</summary>
    private readonly record struct Outcome<T>(bool Ok, T? Value, string Reason);

    private Outcome<T> Invoke<T>(Func<IDevicesRemoteClient, T> body)
    {
        try
        {
            return new(true, body(_client), "");
        }
        catch (TbCoreException ex)
        {
            return new(false, default, ex.Message);
        }
        catch (Exception)
        {
            return new(false, default, "internal");
        }
    }

    /// <summary>One blocking client call on the thread pool.</summary>
    private Task<Outcome<T>> Call<T>(Func<IDevicesRemoteClient, T> body) => Task.Run(() => Invoke(body));

    private T Read<T>(Func<T> read)
    {
        lock (_gate) { return read(); }
    }

    private void Set(Action change)
    {
        lock (_gate) { change(); }
        Notify();
    }

    private void Notify() => Raise(Changed);

    private void RaisePeersChanged() => Raise(PeersChanged);

    private static void Raise(Action? handlers)
    {
        if (handlers is null) { return; }
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A subscriber's failure must not stop the polling or a pairing.
            }
        }
    }
}
