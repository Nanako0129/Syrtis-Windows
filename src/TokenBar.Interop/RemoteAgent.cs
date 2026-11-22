using System.Text;

namespace TokenBar.Interop;

// DTOs of the tb_remote_* exports (ctb.h; macOS port: RemoteAgent.swift).
// Fixed class strings arrive as TbCoreException messages, never here.
//
// R9: a Pair URL or a SAS code is a secret. The records that hold one
// declare a private PrintMembers that leaves it out, so neither ToString()
// nor an interpolated string can print it. Nothing in this slice logs.

public sealed record RemoteAgentStatus(
    bool Running,
    bool? SharingEnabled = null,
    bool? ScopeApproved = null,
    bool? Listening = null,
    string? IdentityFingerprint = null,
    string? BindHost = null,
    int? BindPort = null,
    string? NodeLabel = null,
    int? ActiveGrantCount = null,
    int? ActiveOfferCount = null,
    int? PendingCount = null,
    long? LastScanAt = null,
    string? ScanStatus = null,
    IReadOnlyList<string>? SourceFamilies = null);

/// <summary>A variable set in this process that the local pages follow and
/// sharing does not. <paramref name="Client"/> is null for a global one (the
/// wire says <c>null</c>; macOS declares it non-optional, a latent decode
/// failure there).</summary>
public sealed record RemoteEnvRoot(string? Client, string Variable);

/// <summary>The agent's own disclosure, shown as is. Approve with
/// <see cref="OwnerInputsSha256"/> of the disclosure the owner saw
/// (<see cref="TbCore.RemoteScopeApprove"/>).</summary>
public sealed record RemoteScopeDisclosure(
    string Home,
    string ConfigDir,
    string DataDir,
    string SourceCacheDir,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExtraScanPaths,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExcludedScanPaths,
    IReadOnlyList<string> OpencodeDbPaths,
    IReadOnlyList<string> Clients,
    string OwnerInputsSha256,
    bool EnvRootsSet,
    IReadOnlyList<RemoteEnvRoot> EnvRoots,
    string? XdgDataHome = null);

public sealed record RemoteOffer(long ExpiresAt, string OfferId, string PairUrl)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("OfferId = ").Append(OfferId).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

public sealed record RemotePending(long ExpiresAt, string PendingId, string SasCode)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PendingId = ").Append(PendingId).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

public sealed record RemoteGrant(
    string GrantId, string PeerFingerprint, long CreatedAt, long ExpiresAt, string State);

public sealed record RemotePairDestination(string Host, int Port);

public sealed record RemoteClaim(long Handle, string Sas, long ExpiresAt, string ProducerFingerprint)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Handle = ").Append(Handle).Append(", ProducerFingerprint = ")
            .Append(ProducerFingerprint).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

public sealed record RemotePaired(string ProducerStatic, string Fingerprint, long ExpiresAt);

public sealed record RemotePeer(
    string ProducerStatic,
    string Fingerprint,
    string Host,
    int Port,
    long CreatedAt,
    long ExpiresAt,
    string State,
    string? NodeLabel = null);
