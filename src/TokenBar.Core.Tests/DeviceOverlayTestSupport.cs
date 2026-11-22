using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>Shared fixtures for the device-overlay tests. The macOS
/// self-tests build the same rows with local helpers; the clock and ids are
/// the ones they use (SelfTest.swift :24003-24010, :24106-24112).</summary>
internal static class DeviceOverlayTestSupport
{
    public const long T0 = 1_700_000_000;
    public const string Fp = "aaaaaaaaaaaaaaaa";
    public const string Address = "10.0.0.2:47000";

    /// <summary>300 s after <see cref="T0"/>: "5m ago".</summary>
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(T0 + 300);

    public static string Age => Format.RelativeTime((ulong)T0, Now);

    public static RemoteDevice Row(
        string state, string? reason = null, bool data = true, string id = Fp,
        string? label = Address, bool incomplete = false, bool answered = true) =>
        new(Id: id, State: state, Label: label, LastSuccessAt: data ? T0 : null, Reason: reason,
            Address: Address, CostIncomplete: incomplete, EverAnswered: answered);

    public static RemotePeer Peer(string? label = null) =>
        new(ProducerStatic: new string('a', 64), Fingerprint: Fp, Host: "10.0.0.2", Port: 47000,
            CreatedAt: 0, ExpiresAt: 0, State: "active", NodeLabel: label);

    public static void Load(string tag) => Localization.Load(tag, AppContext.BaseDirectory);
}
