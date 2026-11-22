using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Decodes the 80 Rust-produced fixtures (the bytes the shipping Rust writes)
// through the production decoders. The Rust is the source of truth.
public class RemoteFixtureTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "remote");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name + ".json"));

    private static string[] Names() =>
        Directory.GetFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToArray()!;

    private static string ErrOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("err", out var e) ? e.GetString()! : "";
    }

    // Decoder per fixture; null = unmapped (fails the test).
    private static Action<string>? DecoderFor(string name)
    {
        if (name.StartsWith("graph-")) return j => TbCore.DecodeDeviceEnvelope<UsagePayload>(j);
        if (name.StartsWith("models-")) return j => TbCore.DecodeDeviceEnvelope<ModelReport>(j);
        if (name.StartsWith("hourly-")) return j => TbCore.DecodeDeviceEnvelope<HourlyReport>(j);
        if (name.StartsWith("agents-")) return j => TbCore.DecodeDeviceEnvelope<AgentsReport>(j);
        if (name.StartsWith("error-")) return j => TbCore.DecodeEnvelope<JsonElement>(j);
        return name switch
        {
            "remote-grants" => j => TbCore.DecodeEnvelope<IReadOnlyList<RemoteGrant>>(j),
            "remote-peers" => j => TbCore.DecodeEnvelope<IReadOnlyList<RemotePeer>>(j),
            "remote-pending-list" => j => TbCore.DecodeEnvelope<IReadOnlyList<RemotePending>>(j),
            "remote-listen-configure" or "remote-stop" or "remote-status-running"
                or "remote-status-sharing" or "remote-status-not-running"
                => j => TbCore.DecodeEnvelope<RemoteAgentStatus>(j),
            "remote-offer-create" => j => TbCore.DecodeEnvelope<RemoteOffer>(j),
            "remote-pair-claim" => j => TbCore.DecodeEnvelope<RemoteClaim>(j),
            "remote-pair-poll-paired" => j => TbCore.DecodeEnvelope<RemotePaired>(j),
            "remote-pair-poll-rejected" => j => TbCore.DecodeEnvelope<RemotePaired>(j),
            "remote-scope-disclosure" => j => TbCore.DecodeEnvelope<RemoteScopeDisclosure>(j),
            "remote-scope-approve" or "remote-sharing-set" => j => TbCore.DecodeEnvelope<JsonElement>(j),
            _ => null,
        };
    }

    [Fact]
    public void EveryFixtureDecodesOrThrowsItsErrorClass()
    {
        var names = Names();
        var decoded = 0;
        foreach (var name in names)
        {
            var decoder = DecoderFor(name);
            Assert.True(decoder is not null, $"fixture {name} has no decoder mapping");
            var json = Read(name);
            var err = ErrOf(json);
            if (err.Length > 0)
            {
                var ex = Assert.Throws<TbCoreException>(() => decoder!(json));
                Assert.Equal(err, ex.Message);
                if (name.StartsWith("error-")) Assert.Equal("error-" + err, name);
            }
            else
            {
                decoder!(json);
            }

            decoded++;
        }

        // Control: an empty or uncopied fixtures directory must not pass.
        Assert.Equal(80, decoded);
    }

    [Fact]
    public void ErrorFixturesSurfaceAsTheirClass()
    {
        var errors = Names().Where(n => n.StartsWith("error-")).ToArray();
        Assert.Equal(25, errors.Length);
        foreach (var name in errors)
        {
            var ex = Assert.Throws<TbCoreException>(() => TbCore.DecodeEnvelope<JsonElement>(Read(name)));
            Assert.Equal(name["error-".Length..], ex.Message);
        }
    }

    [Fact]
    public void EveryStateAndReasonIsKnownAndRowsCarryTheirFields()
    {
        var states = new HashSet<string>();
        var reasons = new HashSet<string>();
        foreach (var name in Names().Where(n => n.StartsWith("models-") || n.StartsWith("graph-")
            || n.StartsWith("hourly-") || n.StartsWith("agents-")))
        {
            var p = name.StartsWith("models-") ? TbCore.DecodeDeviceEnvelope<ModelReport>(Read(name)).Devices
                : name.StartsWith("hourly-") ? TbCore.DecodeDeviceEnvelope<HourlyReport>(Read(name)).Devices
                : name.StartsWith("agents-") ? TbCore.DecodeDeviceEnvelope<AgentsReport>(Read(name)).Devices
                : TbCore.DecodeDeviceEnvelope<UsagePayload>(Read(name)).Devices;
            foreach (var d in p)
            {
                Assert.Contains(d.State, DeviceVocabulary.States);
                states.Add(d.State);
                if (d.Reason is { } r)
                {
                    Assert.Contains(r, DeviceVocabulary.Reasons);
                    reasons.Add(r);
                }

                // ctb.h says producer rows always carry address, but the
                // peer-store-unavailable fixtures have a producer row with a
                // null one (store unreadable): reported, decoded as null.
                if (d.Id is not ("local" or "peers") && !name.Contains("peer-store-unavailable"))
                {
                    Assert.False(string.IsNullOrEmpty(d.Address));
                }
            }
        }

        // The fixtures cover every state and reason the contract names.
        Assert.Equal(DeviceVocabulary.States.Order(), states.Order());
        Assert.Equal(DeviceVocabulary.Reasons.Order(), reasons.Order());
    }

    [Fact]
    public void LocalRowGetsDefaultsAndProducerRowKeepsItsValues()
    {
        var local = TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-local-only")).Devices.Single();
        Assert.Equal("local", local.Id);
        Assert.Null(local.Address);
        Assert.False(local.CostIncomplete);
        Assert.True(local.EverAnswered);

        var refused = TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-peer-refused")).Devices.Single();
        Assert.False(refused.EverAnswered);
        Assert.Equal("127.0.0.1:40001", refused.Address);
    }

    [Fact]
    public void EverAnsweredAbsentReadsTrueAndCostIncompleteAbsentReadsFalse()
    {
        var d = TbCore.DecodeEnvelope<RemoteDevice>("""{"ok":true,"data":{"id":"x","state":"fresh"}}""");
        Assert.True(d.EverAnswered);
        Assert.False(d.CostIncomplete);
    }

    [Fact]
    public void FlagsAndGraphMetaDecode()
    {
        var states = TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-states"));
        Assert.True(states.DuplicateWarning);
        Assert.False(states.Saturated);
        Assert.Equal(PricingMode.BestEffort, states.Payload.Meta.PricingMode);
        Assert.Equal(CostCoverage.Complete, states.Payload.Meta.CostCoverage);

        Assert.True(TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-saturated")).Saturated);
        Assert.True(TbCore.DecodeDeviceEnvelope<ModelReport>(Read("models-saturated")).Saturated);
        Assert.True(TbCore.DecodeDeviceEnvelope<HourlyReport>(Read("hourly-saturated")).Saturated);
        Assert.True(TbCore.DecodeDeviceEnvelope<AgentsReport>(Read("agents-saturated")).Saturated);
        Assert.True(TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-all-time")).RemoteRangeLimited);
        Assert.True(TbCore.DecodeDeviceEnvelope<AgentsReport>(Read("agents-all-time")).RemoteRangeLimited);
        Assert.False(TbCore.DecodeDeviceEnvelope<UsagePayload>(Read("graph-local-only")).RemoteRangeLimited);
    }

    [Fact]
    public void PairingShapesDecode()
    {
        var disclosure = TbCore.DecodeEnvelope<RemoteScopeDisclosure>(Read("remote-scope-disclosure"));
        Assert.Equal(64, disclosure.OwnerInputsSha256.Length);
        Assert.Contains(disclosure.EnvRoots, r => r.Client is null);
        Assert.Equal(3, TbCore.DecodeEnvelope<IReadOnlyList<RemoteGrant>>(Read("remote-grants")).Count);
        Assert.Equal("rejected", Assert.Throws<TbCoreException>(
            () => TbCore.DecodeEnvelope<RemotePaired>(Read("remote-pair-poll-rejected"))).Message);
    }

    [Fact]
    public void SecretsDoNotPrint()
    {
        var offerJson = Read("remote-offer-create");
        using var doc = JsonDocument.Parse(offerJson);
        var url = doc.RootElement.GetProperty("data").GetProperty("pairUrl").GetString()!;
        var offer = TbCore.DecodeEnvelope<RemoteOffer>(offerJson);
        Assert.Equal(url, offer.PairUrl);
        Assert.DoesNotContain(url, offer.ToString());
        Assert.DoesNotContain("syrtis://", offer.ToString());
        Assert.DoesNotContain("PairUrl", offer.ToString());
        Assert.DoesNotContain("syrtis://", $"{offer}");

        var pending = TbCore.DecodeEnvelope<IReadOnlyList<RemotePending>>(Read("remote-pending-list"))[0];
        Assert.DoesNotContain(pending.SasCode, pending.ToString());
        var claim = TbCore.DecodeEnvelope<RemoteClaim>(Read("remote-pair-claim"));
        Assert.DoesNotContain(claim.Sas, claim.ToString());
    }

    [Theory]
    [InlineData("Tokyo Standard Time", "Asia/Tokyo")]
    [InlineData("Taipei Standard Time", "Asia/Taipei")]
    public void WindowsZoneMapsToIana(string windowsId, string iana)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        Assert.Equal(iana, TbCore.LocalIanaTimeZoneId(zone));
    }

    [Fact]
    public void LocalZoneIsIanaOrThrowsFixedClass()
    {
        string id;
        try
        {
            id = TbCore.LocalIanaTimeZoneId();
        }
        catch (TbCoreException ex)
        {
            Assert.Equal("timezoneUnavailable", ex.Message);
            return;
        }

        // A Windows id ("Taipei Standard Time") must fail here.
        Assert.True(id.Contains('/') || id == "UTC" || id.StartsWith("Etc/"), id);
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _) || id == "UTC" || id.StartsWith("Etc/"), id);
    }

    [Fact]
    public void DstAdjustmentOffForADstZoneThrows()
    {
        // A Windows-style id whose zone has no adjustment rules, as when
        // "Adjust for daylight saving time" is off; its IANA zone observes DST.
        var noDst = TimeZoneInfo.CreateCustomTimeZone(
            "Pacific Standard Time", TimeSpan.FromHours(-8), "x", "x");
        Assert.False(noDst.SupportsDaylightSavingTime);
        Assert.Equal("timezoneUnavailable",
            Assert.Throws<TbCoreException>(() => TbCore.LocalIanaTimeZoneId(noDst)).Message);

        // Control: a no-DST zone with a no-DST IANA counterpart still maps.
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("Tokyo Standard Time", TimeSpan.FromHours(9), "x", "x");
        Assert.Equal("Asia/Tokyo", TbCore.LocalIanaTimeZoneId(tokyo));
    }
}
