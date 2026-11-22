using TokenBar.App;
using TokenBar.Interop;
using F = TokenBar.Core.Tests.FakeDevicesClient;

namespace TokenBar.Core.Tests;

/// <summary>Settings → Devices model against a fake agent, a fake clipboard
/// and a clock the test moves, ported from the macOS SA3C self-tests
/// (SelfTest.swift :22844-23281, Syrtis f8a1a0ae; each test cites its lines),
/// plus the Windows security contracts R4/R5/R6/R9 (plan § "SA-4c2 security
/// review"). "Wait" polls a condition for up to 5 s; "nothing happens" is a
/// short quiet period after the state that would start it was reached.</summary>
public class DevicesModelTests
{
    private readonly F _fake = new();
    private readonly FakeDevicesClipboard _board = new();
    private readonly FakeDeviceClock _clock = new();

    private DevicesModel Model(
        Func<(DeviceOverlaySnapshot, IReadOnlyDictionary<string, string>)>? overlay = null) =>
        new(_fake, _board, _clock, overlay);

    private static bool Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (condition()) { return true; }
            Thread.Sleep(5);
        }

        return condition();
    }

    private static void Quiet() => Thread.Sleep(150);

    private static async Task<DevicesModel> Confirming(DevicesModel model)
    {
        await model.Link("syrtis-test-invitation");
        Assert.Equal(PairingKind.Confirming, model.Pairing.Kind);
        return model;
    }

    // ---- Start and sharing (:22844-22872) ---------------------------------

    [Fact] // :22848-22851
    public async Task AgentNotRunningShowsRetryAndTheNotRunningLine()
    {
        var model = Model();
        _fake.Set(s => s.Running = false);
        await model.RefreshAsync();
        Assert.True(model.ShowsRetry);
        Assert.Null(model.StartFailure);
        Assert.False(model.CanToggleSharing);
    }

    [Theory] // :22852-22854, plus W4: unsafeState (another process holding Nyanako with DELETE)
    [InlineData("busy", DevicesCopy.StartBusy)]
    [InlineData("unsafeState", DevicesCopy.StartUnsafe)]
    [InlineData("homeRequired", DevicesCopy.StartFailed)]
    public async Task AFailedRetryShowsItsClassLine(string reason, string line)
    {
        var model = Model();
        _fake.Set(s => { s.Running = false; s.StartError = reason; });
        await model.RetryStart();
        Assert.True(model.ShowsRetry);
        Assert.Equal(line, model.StartFailure);
        Assert.False(model.Working);
    }

    [Fact] // :22855-22872
    public async Task SharingNeedsTheScopeFirstThenFollowsTheCore()
    {
        var model = Model();
        await model.RetryStart();
        Assert.False(model.ShowsRetry);
        Assert.Null(model.StartFailure);
        Assert.False(model.ScopeApproved);
        Assert.False(model.CanToggleSharing);

        await model.ApproveScope(model.Disclosure!.OwnerInputsSha256);
        Assert.True(model.ScopeApproved);
        Assert.True(model.CanToggleSharing);
        Assert.Equal([F.DigestA], _fake.Get(s => s.ApproveCalls.ToList()));

        _fake.Set(s => s.SharingError = "busy");
        await model.SetSharing(true);
        Assert.Equal(DevicesCopy.SharingBusy, model.SharingError);
        Assert.False(model.SharingOn);

        _fake.Set(s => s.SharingError = null);
        await model.SetSharing(true);
        Assert.Null(model.SharingError);
        Assert.True(model.SharingOn);
    }

    [Fact] // R6: off is sharing_set(false), never a stop; then a fresh read, no optimistic state
    public async Task TurningSharingOffCallsSharingSetFalseAndRereadsEverything()
    {
        var model = Model();
        _fake.Set(s => { s.Approved = true; s.Sharing = true; });
        await model.RefreshAsync();
        Assert.True(model.SharingOn);
        var before = _fake.Get(s => s.Calls.Count);

        await model.SetSharing(false);

        Assert.Equal([false], _fake.Get(s => s.SharingCalls.ToList()));
        Assert.Equal(["setSharing", "status", "pendingList", "grants", "peers"],
            _fake.Get(s => s.Calls.Skip(before).ToList()));
        Assert.DoesNotContain("start", _fake.Get(s => s.Calls.ToList()));
        Assert.False(model.SharingOn);

        // A refused change shows what the core says, not what was asked.
        _fake.Set(s => s.SharingError = "panic");
        await model.SetSharing(true);
        Assert.False(model.SharingOn);
        Assert.Equal(DevicesCopy.GenericFailure, model.SharingError);
    }

    // ---- Address and name (:22888-22923) ----------------------------------

    [Fact] // :22888-22900
    public async Task ListenAddressPortAndNameLimits()
    {
        var model = Model();
        await model.RefreshAsync();
        Assert.True(model.Unreachable);
        await model.ApplyListen("127.0.0.1", 47_000, "desk");
        Assert.False(model.Unreachable);
        Assert.Equal("desk", model.Status!.NodeLabel);
        _fake.Set(s => s.Listening = false);
        await model.RefreshAsync();
        Assert.True(model.Unreachable);
        await model.ApplyListen("127.0.0.1", 80, "");
        Assert.Equal(DevicesCopy.PortRangeLine, model.ListenError);
        await model.ApplyListen("127.0.0.1", 47_000, new string('a', 129));
        Assert.Equal(DevicesCopy.NameTooLong, model.ListenError);
    }

    private async Task<(bool Sent, string? Label)> SentLabel(DevicesModel model, string typed)
    {
        _fake.Set(s => s.NodeLabel = "unset");
        await model.ApplyListen("127.0.0.1", 47_000, typed);
        return model.ListenError is null ? (true, _fake.Get(s => s.NodeLabel)) : (false, null);
    }

    [Fact] // :22903-22917 (DR2B2)
    public async Task TheNameIsSentCleaned()
    {
        var model = Model();
        Assert.Equal((true, "desk"), await SentLabel(model, "  desk  "));
        Assert.Equal((true, "desk"), await SentLabel(model, "de\u0007s\tk\u2028\u2029"));
        Assert.Equal((true, "desk"), await SentLabel(model, "\u202Edesk\u202A\u202B\u202C\u202D\u2066\u2067\u2068\u2069"));
        Assert.Equal((true, "desk"), await SentLabel(model, "d\u061Ce\u200Es\u200Fk"));
        Assert.Equal((true, "a b"), await SentLabel(model, "a b"));
        Assert.Equal((true, "Caf\u00e9 \U0001F469\u200D\U0001F4BB"), await SentLabel(model, "Caf\u00e9 \U0001F469\u200D\U0001F4BB"));
        Assert.Equal((true, null), await SentLabel(model, ""));
        Assert.Equal((true, null), await SentLabel(model, "   \t\n "));
        Assert.Equal((true, null), await SentLabel(model, "\u202E\u0007"));
        Assert.Equal((true, null), await SentLabel(model, "\u200B \uFEFF\u2800"));
    }

    [Fact] // :22920-22923: the 128-byte limit counts the cleaned name
    public async Task TheNameLimitCountsTheCleanedName()
    {
        var model = Model();
        Assert.Equal((true, new string('a', 128)), await SentLabel(model, new string('a', 128) + " \u202E"));
        Assert.Equal((false, null), await SentLabel(model, new string('a', 129) + "\u202E"));
        Assert.Equal(DevicesCopy.NameTooLong, model.ListenError);
        // UTF-8 bytes, not characters: 43 three-byte characters are 129 bytes.
        Assert.Equal((false, null), await SentLabel(model, new string('桌', 43)));
        Assert.Equal((true, new string('桌', 42)), await SentLabel(model, new string('桌', 42)));
    }

    // ---- Scope approval (R4; :22926-22962) --------------------------------

    [Fact] // :22926-22938
    public async Task AnApprovalAnsweredInvalidSentTheShownDigestOnceAndReloads()
    {
        var model = Model();
        await model.RefreshAsync();
        var shown = model.Disclosure!.OwnerInputsSha256;
        _fake.Set(s => s.Digest = F.DigestB);
        await model.ApproveScope(shown);
        Assert.Equal(F.DigestA, shown);
        Assert.Equal([F.DigestA], _fake.Get(s => s.ApproveCalls.ToList()));
        Assert.Equal(DevicesCopy.ScopeChanged, model.ScopeNotice);
        Assert.Equal(F.DigestB, model.Disclosure!.OwnerInputsSha256);
        Assert.False(model.ScopeApproved);
    }

    [Fact] // R4: the digest of the disclosure on screen, even when a newer one has arrived since
    public async Task ApproveSendsTheShownDigestEvenWhenANewerDisclosureArrived()
    {
        var model = Model();
        await model.RefreshAsync();
        var shown = model.Disclosure!.OwnerInputsSha256; // what the page rendered the cards with
        _fake.Set(s => s.Digest = F.DigestB);
        await model.LoadDisclosureAsync();
        Assert.Equal(F.DigestB, model.Disclosure!.OwnerInputsSha256);
        var disclosuresBefore = _fake.Count("scopeDisclosure");

        await model.ApproveScope(shown);

        Assert.Equal([F.DigestA], _fake.Get(s => s.ApproveCalls.ToList()));
        // Re-read only after the refusal, to show again; never before sending.
        Assert.Equal(disclosuresBefore + 1, _fake.Count("scopeDisclosure"));
        Assert.False(model.ScopeApproved);
        Assert.Equal(DevicesCopy.ScopeChanged, model.ScopeNotice);
    }

    [Fact] // :22946-22962
    public async Task ApprovalLostWhileOpenShowsTheNewDisclosureOnce()
    {
        var model = Model();
        await model.RefreshAsync();
        await model.ApproveScope(model.Disclosure!.OwnerInputsSha256);
        var loadsBefore = _fake.Count("scopeDisclosure");
        await model.RefreshAsync();
        var loadsUnflipped = _fake.Count("scopeDisclosure");
        _fake.Set(s => { s.Digest = F.DigestB; s.Approved = false; });
        await model.RefreshAsync();
        var flipped = (model.Disclosure!.OwnerInputsSha256, model.ScopeNotice, model.ScopeApproved);
        await model.ApproveScope(model.Disclosure!.OwnerInputsSha256);
        Assert.True(model.ScopeApproved);
        Assert.Equal(loadsBefore, loadsUnflipped);
        Assert.Equal((F.DigestB, DevicesCopy.ScopeChanged, false), flipped);
        Assert.Equal([F.DigestA, F.DigestB], _fake.Get(s => s.ApproveCalls.ToList()));
    }

    [Fact] // brief: XdgDataHome from the fixture is shown in the Data folder card; SourceCacheDir is not
    public async Task XdgDataHomeIsASecondPathInTheDataFolderCard()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "remote", "remote-scope-disclosure.json"));
        var fixture = TbCore.DecodeEnvelope<RemoteScopeDisclosure>(json);
        _fake.Set(s => s.Disclosure = fixture);
        var model = Model();
        await model.RefreshAsync();
        var paths = DevicesModel.DataFolderPaths(model.Disclosure!);
        Assert.Equal([@"C:\fixture\dataDir", @"C:\fixture\xdgDataHome"], paths);
        Assert.DoesNotContain(fixture.SourceCacheDir, paths);
        // Control: without XdgDataHome the card has the one path.
        Assert.Equal([@"C:\fixture\data"], DevicesModel.DataFolderPaths(F.DisclosureWith(F.DigestA)));
    }

    // ---- Polling and page lifetime (:22967-22989) -------------------------

    [Fact] // :22967-22989
    public async Task ClosingThePageMidRefreshMakesNoFurtherCallAndWritesNothing()
    {
        var model = Model();
        await model.RefreshAsync();
        var before = (model.Status, model.Pending.Count, model.Grants.Count, model.Peers.Count);
        _fake.Set(s =>
        {
            s.NodeLabel = "late";
            s.Pending = [F.Request("p-late", "7777-8888")];
            s.Peers = [F.ProducerA];
            s.HoldStatus = true;
        });
        model.PageAppeared();
        Assert.True(Eventually(() => _fake.Get(s => s.StatusEntered)));
        model.PageDisappeared();
        var callsAtLeave = _fake.Get(s => s.Calls.Count);
        _fake.ReleaseStatus();
        Quiet();
        Assert.Equal(callsAtLeave, _fake.Get(s => s.Calls.Count));
        Assert.Equal(before, (model.Status, model.Pending.Count, model.Grants.Count, model.Peers.Count));
    }

    [Fact] // brief: appear → rebuild → appear runs one poll loop and keeps a waiting pairing
    public async Task AppearTwiceRunsOnePollLoopAndKeepsTheWait()
    {
        var model = Model();
        await Confirming(model);
        await model.ConfirmLink();
        Assert.Equal(PairingKind.Waiting, model.Pairing.Kind);

        model.PageAppeared();
        Assert.True(Eventually(() => _clock.PendingCount == 1));
        model.PageAppeared(); // the Settings window rebuilt and showed the page again
        Quiet();
        Assert.Equal(1, _clock.PendingCount);
        Assert.Equal(1, _fake.Count("status"));
        Assert.Equal(PairingKind.Waiting, model.Pairing.Kind);

        // The loop goes on every 2 s, one refresh per tick.
        _clock.Advance(DevicesModel.RefreshInterval);
        Assert.True(Eventually(() => _fake.Count("status") == 2 && _clock.PendingCount == 1));

        model.PageDisappeared();
        Assert.True(Eventually(() => _clock.PendingCount == 0));
        Assert.False(model.Polling);
        Assert.Equal(PairingKind.Waiting, model.Pairing.Kind);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Quiet();
        Assert.Equal(2, _fake.Count("status"));
        _fake.ReleasePollFailure("expired");
    }

    // ---- Invitations (:22994-23053) ---------------------------------------

    [Fact] // :22994-23002
    public async Task CreateInvitationShowsTheCoresRefusalLine()
    {
        var model = Model();
        await model.CreateOffer();
        var noAddress = model.OfferError;
        _fake.Set(s => { s.BindHost = "127.0.0.1"; s.Listening = true; });
        await model.CreateOffer();
        Assert.Equal(DevicesCopy.OfferNeedsAddress, noAddress);
        Assert.Equal(DevicesCopy.OfferNeedsSharing, model.OfferError);
        Assert.Null(model.Offer);
    }

    [Fact] // :23003-23019
    public async Task OrphanedAndOlderCreatesAreCancelledNotKept()
    {
        var model = Model();
        _fake.Set(s => { s.BindHost = "127.0.0.1"; s.Sharing = true; s.HoldOffers = true; });
        var first = model.CreateOffer();
        Assert.True(Eventually(() => _fake.Get(s => s.OffersEntered) == 1));
        model.PageDisappeared();
        _fake.ReleaseOffers(1);
        await first;
        Assert.True(Eventually(() => _fake.Get(s => s.CancelledOffers.SequenceEqual(["offer-1"]))));
        Quiet();
        Assert.Null(model.Offer);

        var second = model.CreateOffer();
        Assert.True(Eventually(() => _fake.Get(s => s.OffersEntered) == 2));
        var third = model.CreateOffer();
        Assert.True(Eventually(() => _fake.Get(s => s.OffersEntered) == 3));
        _fake.ReleaseOffers(2);
        await Task.WhenAll(second, third);
        Assert.True(Eventually(() => model.Offer?.OfferId == "offer-3"));
        Assert.True(Eventually(() => _fake.Get(s => s.CancelledOffers.SequenceEqual(["offer-1", "offer-2"]))));
        model.PageDisappeared();
    }

    private async Task<DevicesModel> WithOffer()
    {
        var model = Model();
        _fake.Set(s => { s.Approved = true; s.Sharing = true; });
        await model.ApplyListen("127.0.0.1", 47_000, "desk");
        await model.CreateOffer();
        Assert.NotNull(model.Offer);
        return model;
    }

    [Fact] // :23020-23035 (R9): nothing printable holds the Pair URL
    public async Task NoPrintedFormHoldsThePairUrl()
    {
        var model = await WithOffer();
        var url = model.Offer!.PairUrl;
        Assert.NotEmpty(url);
        Assert.DoesNotContain(url, model.ToString());
        Assert.DoesNotContain(url, $"{model}");
        Assert.DoesNotContain(url, model.Offer!.ToString());
        model.CopyOffer();
        Assert.DoesNotContain(url, _board.LastRequest!.ToString());
        Assert.Contains("AllowHistory = False", _board.LastRequest!.ToString()); // control: the record does print
        model.PageDisappeared();
    }

    [Fact] // R9: nothing printable holds the SAS (a pending row, a waiting claim)
    public async Task NoPrintedFormHoldsTheSas()
    {
        var model = Model();
        _fake.Set(s => s.Pending = [F.Request("p-1", "1234-5678")]);
        await model.RefreshAsync();
        Assert.DoesNotContain("1234-5678", model.Pending[0].ToString());
        Assert.Contains("p-1", model.Pending[0].ToString()); // control
        await Confirming(model);
        await model.ConfirmLink();
        Assert.Equal("5555-6666", model.Pairing.Claim!.Sas);
        Assert.DoesNotContain("5555-6666", model.Pairing.ToString());
        Assert.DoesNotContain("5555-6666", model.ToString());
        _fake.ReleasePollFailure("expired");
    }

    [Fact] // :23036-23037; brief: not in clipboard history, not roaming
    public async Task CopyWritesThePairUrlOutOfHistoryAndOffCloudSync()
    {
        var model = await WithOffer();
        model.CopyOffer();
        Assert.Equal(model.Offer!.PairUrl, _board.Text);
        Assert.False(_board.LastRequest!.AllowHistory);
        Assert.False(_board.LastRequest!.Roamable);
        Assert.True(model.Copied);
        model.PageDisappeared();
    }

    [Fact] // review of #296: a write the clipboard refused does not show "Copied"
    public async Task ARefusedClipboardWriteIsNotCopied()
    {
        var model = await WithOffer();
        _board.Refuses = true;
        model.CopyOffer();
        Assert.False(model.Copied);
        _board.Refuses = false;
        model.CopyOffer();
        Assert.True(model.Copied); // control
        model.PageDisappeared();
    }

    [Fact] // :23038-23044
    public async Task OnExpiryTheOfferAndAnUnchangedClipboardAreCleared()
    {
        var model = await WithOffer();
        model.CopyOffer();
        var offer = model.Offer!;
        model.ExpireOfferIfDue(offer.ExpiresAt - 1);
        Assert.NotNull(model.Offer); // control: kept until it expires
        model.ExpireOfferIfDue(offer.ExpiresAt);
        Assert.Null(model.Offer);
        Assert.Null(_board.Text);
        Assert.True(Eventually(() => _fake.Get(s => s.CancelledOffers.SequenceEqual([offer.OfferId]))));
    }

    [Fact] // :23045-23053; brief: Cancel clears an unchanged clipboard, keeps a changed one
    public async Task CancelAndDisappearClearTheClipboardOnlyIfUnchanged()
    {
        var model = await WithOffer();
        model.CopyOffer();
        model.ClearOffer(); // Cancel invitation
        Assert.Null(_board.Text);
        Assert.Null(model.Offer);

        await model.CreateOffer();
        model.CopyOffer();
        _board.CopiedElsewhere("other");
        var left = model.Offer!.OfferId;
        model.PageDisappeared();
        Assert.Null(model.Offer);
        Assert.Equal("other", _board.Text);
        Assert.True(Eventually(() => _fake.Get(s => s.CancelledOffers.Count == 2 && s.CancelledOffers[^1] == left)));
    }

    // ---- Linking (:23057-23224) -------------------------------------------

    [Fact] // :23057-23078
    public async Task LinkPreviewsThenOnlyTheConfirmClaimsOnce()
    {
        var model = Model();
        await model.Link("  syrtis-test-invitation\n");
        Assert.Equal(new RemotePairDestination("100.73.2.33", 47_000), model.Pairing.Destination);
        Assert.True(model.HoldsPairUrl);
        Assert.Equal(0, _fake.Get(s => s.Claims));
        Assert.Equal(["syrtis-test-invitation"], _fake.Get(s => s.PreviewedUrls.ToList()));

        model.CancelLink();
        Assert.Equal(PairingKind.Idle, model.Pairing.Kind);
        Assert.False(model.HoldsPairUrl);
        await model.ConfirmLink();
        Assert.Equal(0, _fake.Get(s => s.Claims));
        Assert.Equal(PairingKind.Idle, model.Pairing.Kind);

        await model.Link("\tsyrtis-test-invitation  \n");
        await Task.WhenAll(model.ConfirmLink(), model.ConfirmLink());
        Assert.Equal(PairingKind.Waiting, model.Pairing.Kind);
        Assert.Equal(1, _fake.Get(s => s.Claims));
        Assert.Equal(["syrtis-test-invitation"], _fake.Get(s => s.ClaimedUrls.ToList()));
        Assert.False(model.HoldsPairUrl);
        _fake.ReleasePollFailure("expired");
    }

    [Fact] // R9: the held invitation is emptied after the claim, after Cancel and when the page disappears
    public async Task TheHeldInvitationIsEmptiedAfterClaimCancelAndDisappear()
    {
        var model = await Confirming(Model());
        await model.ConfirmLink();
        Assert.False(model.HoldsPairUrl);
        model.CancelWait();

        await Confirming(model);
        model.CancelLink();
        Assert.False(model.HoldsPairUrl);

        await Confirming(model);
        model.PageDisappeared();
        Assert.False(model.HoldsPairUrl);
        Assert.Equal(PairingKind.Idle, model.Pairing.Kind);
        _fake.ReleasePoll(F.ProducerB);
        Assert.True(Eventually(() => _fake.Get(s => s.RemovedPeers.Count) == 1));
    }

    [Fact] // :23079-23083
    public async Task AWaitThatSucceedsAfterLeavingShowsLinkedOnReturn()
    {
        var model = await Confirming(Model());
        await model.ConfirmLink();
        model.PageDisappeared();
        _fake.ReleasePoll(F.ProducerA);
        Assert.True(Eventually(() => model.Pairing is { Kind: PairingKind.Linked } p && p.Fingerprint == F.FingerprintA));
        Assert.True(Eventually(() => model.Peers.Any(p => p.ProducerStatic == F.ProducerA)));
    }

    [Fact] // :23084-23121 (DR2B2 D2): a link and a removal tell the dashboard; a plain refresh does not
    public async Task LinkAndRemoveRaisePeersChangedAndARefreshDoesNot()
    {
        var model = Model();
        var raised = 0;
        model.PeersChanged += () => Interlocked.Increment(ref raised);
        await Confirming(model);
        await model.ConfirmLink();
        _fake.ReleasePoll(F.ProducerA);
        Assert.True(Eventually(() => Volatile.Read(ref raised) == 1));
        Assert.True(Eventually(() => model.Peers.Count == 1));
        await model.RemovePeer(model.Peers[0]);
        Assert.Equal(2, Volatile.Read(ref raised));
        Assert.Empty(model.Peers);
        await model.RefreshAsync();
        Quiet();
        Assert.Equal(2, Volatile.Read(ref raised));
    }

    [Fact] // :23192-23201
    public async Task ACancelledWaitWhosePollSucceedsRemovesThatDeviceOnce()
    {
        var model = await Confirming(Model());
        await model.ConfirmLink();
        model.CancelWait();
        _fake.ReleasePoll(F.ProducerB);
        Assert.True(Eventually(() => _fake.Get(s => s.RemovedPeers.SequenceEqual([F.ProducerB]))));
        Assert.True(Eventually(() => !model.Peers.Any(p => p.ProducerStatic == F.ProducerB)));
        Assert.Equal(PairingKind.Idle, model.Pairing.Kind);
    }

    [Fact] // :23202-23204: the 16384-byte pre-check, in UTF-8 bytes
    public async Task AnInvitationOver16384BytesIsRefusedWithoutACall()
    {
        var model = Model();
        await model.Link(new string('x', DevicesModel.PairUrlMaxLength + 1));
        Assert.Equal(DevicesCopy.InvalidInvitation, model.Pairing.Failure);
        await model.Link(new string('é', DevicesModel.PairUrlMaxLength / 2 + 1)); // 16386 bytes, 8193 chars
        Assert.Equal(DevicesCopy.InvalidInvitation, model.Pairing.Failure);
        await model.Link("   ");
        Assert.Equal(DevicesCopy.InvalidInvitation, model.Pairing.Failure);
        Assert.Empty(_fake.Get(s => s.PreviewedUrls.ToList()));
        // Control: exactly 16384 bytes is previewed.
        await model.Link(new string('x', DevicesModel.PairUrlMaxLength));
        Assert.Equal(PairingKind.Confirming, model.Pairing.Kind);
    }

    [Theory] // :23205-23214 and every failure line (DM :851-862)
    [InlineData("invalidOffer", DevicesCopy.InvalidInvitation)]
    [InlineData("expired", DevicesCopy.InvitationExpired)]
    [InlineData("selfPairing", DevicesCopy.OwnInvitation)]
    [InlineData("transport", DevicesCopy.UnreachableDevice)]
    [InlineData("offerUnusable", DevicesCopy.UnusableInvitation)]
    public async Task ARefusedPreviewShowsItsLineHoldsNothingAndClaimsNothing(string reason, string line)
    {
        var model = Model();
        _fake.Set(s => s.DestinationError = reason);
        await model.Link("syrtis-test-bad-invitation");
        Assert.Equal(new DevicesPairing(PairingKind.Failed, Failure: line), model.Pairing);
        Assert.False(model.HoldsPairUrl);
        Assert.Equal(0, _fake.Get(s => s.Claims));
    }

    [Theory] // the poll's failure classes
    [InlineData("rejected", DevicesCopy.Declined)]
    [InlineData("identityChanged", DevicesCopy.IdentityChanged)]
    [InlineData("unauthorized", DevicesCopy.UnusableInvitation)]
    [InlineData("revoked", DevicesCopy.UnusableInvitation)]
    [InlineData("panic", DevicesCopy.LinkFailed)]
    public async Task AFailedWaitShowsItsLine(string reason, string line)
    {
        var model = await Confirming(Model());
        await model.ConfirmLink();
        _fake.ReleasePollFailure(reason);
        Assert.True(Eventually(() => model.Pairing == new DevicesPairing(PairingKind.Failed, Failure: line)));
    }

    [Fact] // R9: failure text comes only from class strings, never from an exception's own message
    public async Task FailureTextComesOnlyFromClassStrings()
    {
        var model = await Confirming(Model());
        _fake.Set(s => s.ClaimException = new TbCoreException("syrtis-test-invitation leaked"));
        await model.ConfirmLink();
        Assert.Equal(new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.LinkFailed), model.Pairing);

        model.DismissPairingResult();
        await Confirming(model);
        _fake.Set(s => s.ClaimException = new InvalidOperationException("syrtis-test-invitation leaked"));
        await model.ConfirmLink();
        Assert.Equal(new DevicesPairing(PairingKind.Failed, Failure: DevicesCopy.LinkFailed), model.Pairing);
        Assert.Contains(model.Pairing.Failure, DevicesCopy.All);
    }

    [Fact] // :23215-23224
    public async Task ThePageLeavingBeforeThePreviewReturnsLeavesNothingHeld()
    {
        var model = Model();
        _fake.Set(s => s.HoldDestination = true);
        var preview = model.Link("syrtis-test-invitation");
        Assert.True(Eventually(() => _fake.Get(s => s.DestinationEntered)));
        model.PageDisappeared();
        _fake.ReleaseDestination();
        await preview;
        Assert.Equal(PairingKind.Idle, model.Pairing.Kind);
        Assert.False(model.HoldsPairUrl);
        Assert.Equal(0, _fake.Get(s => s.Claims));
    }

    // ---- Requests (R5; :23227-23246) --------------------------------------

    [Fact] // :23227-23237
    public async Task ApproveAndRejectSendTheShownRowsPendingIdWhenARowInsertsAboveIt()
    {
        var model = Model();
        _fake.Set(s => s.Pending = [F.Request("p-shown", "1111-2222")]);
        await model.RefreshAsync();
        var shownRow = model.Pending[0];
        _fake.Set(s => s.Pending.Insert(0, F.Request("p-new", "3333-4444")));
        await model.RefreshAsync();
        await model.Decide(shownRow, approve: true);
        Assert.Equal(["p-new", "p-shown"], model.Pending.Select(p => p.PendingId));
        Assert.Equal(["p-shown"], _fake.Get(s => s.ApprovedIds.ToList()));
        await model.Decide(shownRow, approve: false);
        Assert.Equal(["p-shown"], _fake.Get(s => s.Rejected.ToList()));
        Assert.False(model.IsDeciding("p-shown"));
    }

    [Fact] // :23241-23246
    public async Task ApprovingPutsTheShownInvitationAwayRejectingKeepsIt()
    {
        var model = await WithOffer();
        var row = F.Request("p-shown", "1111-2222");
        await model.Decide(row, approve: false);
        var keptAfterReject = model.Offer is not null;
        await model.Decide(row, approve: true);
        Assert.True(keptAfterReject);
        Assert.Null(model.Offer);
    }

    // ---- Linked device rows (:23278-23281, :24272-24274) ------------------

    [Fact] // :23278-23281
    public async Task APeersRowShowsHostAndPort()
    {
        _fake.Set(s => s.Peers = [F.ProducerA]);
        var model = Model();
        await model.RefreshAsync();
        var peer = model.Peers[0];
        Assert.Equal(("192.0.2.10", 47_001), (peer.Host, peer.Port));
        Assert.Equal("192.0.2.10:47001", DevicesModel.Address(peer));
    }

    [Fact] // :24272-24274 (DR2B2), plus IPv6 brackets and the learned name first
    public void NameLineAndAddress()
    {
        var named = new RemotePeer(F.ProducerA, F.FingerprintA, "10.0.0.2", 47_000, 1, F.Far, "active", "Studio PC");
        var bare = named with { NodeLabel = null };
        Assert.Equal("Studio PC", DevicesModel.NameLineFor(named, null));
        Assert.Null(DevicesModel.NameLineFor(bare, null));
        Assert.Equal("10.0.0.2:47000", DevicesModel.Address(named));
        Assert.Equal("[fd7a::1]:47000", DevicesModel.Address("fd7a::1", 47_000));
        var learned = new Dictionary<string, string> { [F.FingerprintA] = "Desk" };
        Assert.Equal("Desk", DevicesModel.NameLineFor(named, learned));
        Assert.Equal("Desk", DevicesModel.NameLineFor(bare, learned));
    }

    [Fact] // brief: a stub snapshot provider shows the short status; null hides it
    public void ShortStatusShowsWithAProviderAndHidesWithout()
    {
        var peer = new RemotePeer(F.ProducerA, F.FingerprintA, "10.0.0.2", 47_000, 1, F.Far, "active");
        Assert.Null(Model().ShortStatus(peer));
        Assert.Null(Model().NameLine(peer));

        var snapshot = new DeviceOverlaySnapshot(
            [], new Dictionary<string, bool> { [F.FingerprintA] = false }, false, new Dictionary<string, string>());
        var model = Model(() => (snapshot, new Dictionary<string, string> { [F.FingerprintA] = "Desk" }));
        var status = model.ShortStatus(peer);
        Assert.NotNull(status);
        Assert.Equal(DeviceOverlayCopy.ShortDot.Grey, status!.Dot);
        Assert.Equal(DeviceOverlayCopy.ShortNotIncluded, status.Text);
        Assert.Equal("Desk", model.NameLine(peer));
        // A device the dashboard does not offer: nothing is made up.
        var other = peer with { Fingerprint = new string('9', 16) };
        Assert.Null(model.ShortStatus(other));
    }

    [Fact] // DSV :471-474
    public void RemainingIsMinutesAndSecondsRoundedUp()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        Assert.Equal("10:00", DevicesModel.Remaining(1_000_000 + 600_000, now));
        Assert.Equal("0:01", DevicesModel.Remaining(1_000_000 + 1, now));
        Assert.Equal("0:00", DevicesModel.Remaining(999_000, now));
        Assert.Equal("1:05", DevicesModel.Remaining(1_000_000 + 64_500, now));
    }
}
