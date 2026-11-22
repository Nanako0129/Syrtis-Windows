using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The Devices page's agent in tests, a port of macOS
/// <c>SA3CFakeClient</c> (SelfTest.swift :25307-25513, Syrtis f8a1a0ae): it
/// refuses as the agent does, records every call, and can hold a call until
/// released. No native call, no socket.</summary>
internal sealed class FakeDevicesClient : IDevicesRemoteClient
{
    public static readonly string DigestA = new('a', 64);
    public static readonly string DigestB = new('b', 64);
    public static readonly string ProducerA = new('1', 64);
    public static readonly string ProducerB = new('2', 64);
    public static readonly string FingerprintA = new('1', 16);
    public const long Far = 4_102_444_800_000;

    public sealed class State
    {
        public bool Running = true;
        public string? StartError;
        public bool Sharing;
        public string? SharingError;
        public bool Approved;
        public string Digest = DigestA;
        public string? BindHost;
        public bool Listening;
        public string? NodeLabel;
        public List<string> ApproveCalls = [];
        public List<RemotePending> Pending = [];
        public List<string> ApprovedIds = [];
        public List<string> Rejected = [];
        public List<string> CancelledOffers = [];
        public int Offers;
        public int Claims;
        public List<string> PreviewedUrls = [];
        public List<string> ClaimedUrls = [];
        public string? DestinationError;
        public Exception? ClaimException;
        public bool HoldDestination;
        public bool DestinationEntered;
        public List<string> Peers = [];
        public List<string> RemovedPeers = [];
        public List<string> Calls = [];
        public List<bool> SharingCalls = [];
        public bool HoldStatus;
        public bool StatusEntered;
        public bool HoldOffers;
        public int OffersEntered;
        public RemoteScopeDisclosure? Disclosure;
    }

    private readonly object _gate = new();
    private readonly State _state = new();
    private readonly SemaphoreSlim _pollGate = new(0);
    private readonly SemaphoreSlim _statusGate = new(0);
    private readonly SemaphoreSlim _offerGate = new(0);
    private readonly SemaphoreSlim _destinationGate = new(0);
    private Func<RemotePaired>? _pollResult;

    public void Set(Action<State> change)
    {
        lock (_gate) { change(_state); }
    }

    public T Get<T>(Func<State, T> read)
    {
        lock (_gate) { return read(_state); }
    }

    public int Count(string call) => Get(s => s.Calls.Count(c => c == call));

    public void ReleaseDestination()
    {
        Set(s => s.HoldDestination = false);
        _destinationGate.Release();
    }

    public void ReleaseStatus()
    {
        Set(s => s.HoldStatus = false);
        _statusGate.Release();
    }

    public void ReleaseOffers(int count) => _offerGate.Release(count);

    /// <summary>Ends the one waiting PairPoll with the producer's static, or
    /// with the agent's class string.</summary>
    public void ReleasePoll(string producer) => ReleasePoll(() =>
    {
        Set(s => s.Peers.Add(producer)); // as the core writes peers.json on success
        return new RemotePaired(producer, producer[..16], Far);
    });

    public void ReleasePollFailure(string reason) => ReleasePoll(() => throw new TbCoreException(reason));

    private void ReleasePoll(Func<RemotePaired> result)
    {
        lock (_gate) { _pollResult = result; }
        _pollGate.Release();
    }

    public static RemotePending Request(string id, string sas) => new(Far, id, sas);

    public static RemoteScopeDisclosure DisclosureWith(string digest, string? xdg = null) => new(
        @"C:\fixture\home", @"C:\fixture\config", @"C:\fixture\data", @"C:\fixture\cache",
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(),
        [], ["claude"], digest, false, [], xdg);

    private static void Fail(string? reason)
    {
        if (reason is not null) { throw new TbCoreException(reason); }
    }

    public RemoteAgentStatus Start()
    {
        Set(s => s.Calls.Add("start"));
        Fail(Get(s => s.StartError));
        return StatusCore();
    }

    public RemoteAgentStatus Status()
    {
        var hold = Get(s =>
        {
            s.Calls.Add("status");
            if (s.HoldStatus) { s.StatusEntered = true; }
            return s.HoldStatus;
        });
        if (hold) { _statusGate.Wait(); }
        return StatusCore();
    }

    private RemoteAgentStatus StatusCore() => Get(s => s.Running
        ? new RemoteAgentStatus(
            true, s.Sharing, s.Approved, s.Listening, "00000000000000ff", s.BindHost, 47_000, s.NodeLabel)
        : new RemoteAgentStatus(false));

    public void SetSharing(bool enabled)
    {
        Set(s => { s.Calls.Add("setSharing"); s.SharingCalls.Add(enabled); });
        Fail(Get(s => s.SharingError));
        Set(s => s.Sharing = enabled);
    }

    public RemoteAgentStatus ListenConfigure(string host, int port, string? nodeLabel)
    {
        Set(s => { s.BindHost = host; s.Listening = true; s.NodeLabel = nodeLabel; });
        return StatusCore();
    }

    public RemoteScopeDisclosure ScopeDisclosure() => Get(s =>
    {
        s.Calls.Add("scopeDisclosure");
        return s.Disclosure ?? DisclosureWith(s.Digest);
    });

    public void ScopeApprove(string ownerInputsSha256)
    {
        var current = Get(s => { s.ApproveCalls.Add(ownerInputsSha256); return s.Digest; });
        if (ownerInputsSha256 != current) { throw new TbCoreException("invalid"); }
        Set(s => s.Approved = true);
    }

    /// <summary>Refuses as the agent's offer_create does: no stored bindHost →
    /// invalid (checked first); sharing off → unauthorized.</summary>
    public RemoteOffer OfferCreate(long ttlSeconds)
    {
        var (n, hold, refusal) = Get(s =>
        {
            s.Calls.Add("offerCreate");
            if (s.BindHost is null) { return (0, false, (string?)"invalid"); }
            if (!s.Sharing) { return (0, false, "unauthorized"); }
            s.Offers++;
            if (s.HoldOffers) { s.OffersEntered++; }
            return (s.Offers, s.HoldOffers, null);
        });
        Fail(refusal);
        if (hold) { _offerGate.Wait(); }
        return new RemoteOffer(Far, $"offer-{n}", $"syrtis-test-pair-url-{n}-{Guid.NewGuid():N}");
    }

    public void OfferCancel(string offerId) => Set(s => s.CancelledOffers.Add(offerId));

    public IReadOnlyList<RemotePending> PendingList() => Get(s =>
    {
        s.Calls.Add("pendingList");
        return (IReadOnlyList<RemotePending>)[.. s.Pending];
    });

    public void Approve(string pendingId) => Set(s => s.ApprovedIds.Add(pendingId));

    public void Reject(string pendingId) => Set(s => s.Rejected.Add(pendingId));

    public IReadOnlyList<RemoteGrant> Grants()
    {
        Set(s => s.Calls.Add("grants"));
        return [];
    }

    public void Revoke(string grantId)
    {
    }

    public RemotePairDestination PairDestination(string pairUrl)
    {
        var hold = Get(s =>
        {
            s.PreviewedUrls.Add(pairUrl);
            if (s.HoldDestination) { s.DestinationEntered = true; }
            return s.HoldDestination;
        });
        if (hold) { _destinationGate.Wait(); }
        Fail(Get(s => s.DestinationError));
        return new RemotePairDestination("100.73.2.33", 47_000);
    }

    public RemoteClaim PairClaim(string pairUrl)
    {
        var (n, ex) = Get(s => { s.Claims++; s.ClaimedUrls.Add(pairUrl); return (s.Claims, s.ClaimException); });
        if (ex is not null) { throw ex; }
        return new RemoteClaim(n, "5555-6666", Far, FingerprintA);
    }

    public RemotePaired PairPoll(long handle)
    {
        _pollGate.Wait();
        Func<RemotePaired> result;
        lock (_gate) { result = _pollResult!; }
        return result();
    }

    public IReadOnlyList<RemotePeer> Peers() => Get(s =>
    {
        s.Calls.Add("peers");
        return (IReadOnlyList<RemotePeer>)[.. s.Peers.Select(p =>
            new RemotePeer(p, p[..16], "192.0.2.10", 47_001, 1, Far, "active"))];
    });

    public void PeerRemove(string producerStatic) =>
        Set(s => { s.RemovedPeers.Add(producerStatic); s.Peers.Remove(producerStatic); });
}

/// <summary>The clipboard in tests: fields, never the system clipboard
/// (macOS <c>SA3CFakePasteboard</c>, :25516-25533).</summary>
internal sealed class FakeDevicesClipboard : IDevicesClipboard
{
    private readonly object _gate = new();
    private uint _sequence;

    public string? Text { get; private set; }
    public ClipboardWriteRequest? LastRequest { get; private set; }

    public uint SequenceNumber { get { lock (_gate) { return _sequence; } } }

    public uint Write(ClipboardWriteRequest request)
    {
        lock (_gate)
        {
            _sequence++;
            Text = request.Text;
            LastRequest = request;
            return _sequence;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sequence++;
            Text = null;
        }
    }

    /// <summary>Someone else copied something since.</summary>
    public void CopiedElsewhere(string text)
    {
        lock (_gate)
        {
            _sequence++;
            Text = text;
        }
    }
}
