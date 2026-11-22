using TokenBar.Core;
using TokenBar.Interop;
using C = TokenBar.Core.DeviceOverlayCopy;
using static TokenBar.Core.Tests.DeviceOverlayTestSupport;

namespace TokenBar.Core.Tests;

/// <summary>The status line, the short status, worst row, snapshot, names
/// and the notes under a lens, against the macOS self-tests (SelfTest.swift,
/// Syrtis f8a1a0ae). macOS mostly compares <c>X.localized(args)</c> with
/// itself, which would pass if the placeholders were never filled; the
/// literal-string tests in <see cref="DeviceOverlayCopyTableTests"/> cover
/// that. Every test runs in English, and the tests that name a zh-Hant
/// sentence load it explicitly.</summary>
public class DeviceOverlayStatusTests : IDisposable
{
    public DeviceOverlayStatusTests() => Load("en");

    public void Dispose() => Load("en");

    private static string Line(RemoteDevice d, bool inFlight = false, bool retry = false) =>
        C.StatusLine(d, C.Name(d), inFlight, retry, Now);

    private static IReadOnlyList<string> Lines(
        IReadOnlyList<RemoteDevice> rows, bool inFlight = false,
        IReadOnlyList<(string Id, string Name)>? peers = null) =>
        C.StatusLines(rows, peers ?? [], inFlight, false, Now);

    private static readonly string Name = Address;

    // Collection-expression expectations go through this: Assert.Equal would
    // see two applicable overloads for a collection expression.
    private static void Same(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected, actual);

    // ---- DR-2a R3, cell by cell (SelfTest.swift :24010-24063) -------------

    [Fact] // :24025
    public void UnreachableAndTimeoutWithDataSayCannotReachWithTheAge()
    {
        Assert.Equal(C.UnreachableData.Localized(Name, Age), Line(Row("lastGood", "unreachable")));
        Assert.Equal(C.UnreachableData.Localized(Name, Age), Line(Row("lastGood", "timeout")));
    }

    [Fact] // :24029
    public void UnreachableAndTimeoutWithoutDataSayNoDataYetCannotReach()
    {
        Assert.Equal(C.UnreachableNoData.Localized(Name), Line(Row("unavailable", "unreachable", data: false)));
        Assert.Equal(C.UnreachableNoData.Localized(Name), Line(Row("unavailable", "timeout", data: false)));
    }

    [Fact] // :24033
    public void BusyRetryClauseAppearsOnlyWhenARetryIsArmed()
    {
        Assert.Equal(C.BusyDataRetry.Localized(Name, Age), Line(Row("lastGood", "busy"), retry: true));
        Assert.Equal(C.BusyData.Localized(Name, Age), Line(Row("lastGood", "busy"), retry: false));
        Assert.Equal(C.BusyNoDataRetry.Localized(Name),
            Line(Row("unavailable", "busy", data: false), retry: true));
        Assert.Equal(C.BusyNoData.Localized(Name),
            Line(Row("unavailable", "busy", data: false), retry: false));
        Assert.NotEqual(C.BusyData, C.BusyDataRetry);
        Assert.NotEqual(C.BusyNoData, C.BusyNoDataRetry);
    }

    [Fact] // :24041
    public void StoppedIsOneSentenceWithOrWithoutData()
    {
        Assert.Equal(C.Stopped.Localized(Name), Line(Row("refused", "stopped", data: false)));
        Assert.Equal(C.Stopped.Localized(Name), Line(Row("lastGood", "stopped")));
    }

    [Fact] // :24045
    public void ErrorSaysCouldNotAnswerWithTheAgeOrWithoutData()
    {
        Assert.Equal(C.ErrorData.Localized(Name, Age), Line(Row("lastGood", "error")));
        Assert.Equal(C.ErrorNoData.Localized(Name), Line(Row("refused", "error", data: false)));
    }

    [Fact] // :24048
    public void StaleKeepsItsSentenceWhateverTheReason()
    {
        Assert.Equal(C.Stale.Localized(Name), Line(Row("stale", null, data: false)));
        Assert.Equal(C.Stale.Localized(Name), Line(Row("stale", "unreachable", data: false)));
    }

    [Fact] // :24051
    public void InvalidOverLimitIncompatibleAndAnUnreadablePeerListKeepTheirSentences()
    {
        Assert.Equal(C.InvalidReply.Localized(Name), Line(Row("invalidReply")));
        Assert.Equal(C.OverLimit.Localized(Name), Line(Row("overLimit")));
        Assert.Equal(C.Incompatible.Localized(Name), Line(Row("incompatible")));
        Assert.Equal(C.Unavailable.Localized(C.OtherDevices.Localized()),
            Line(Row("unavailable", null, data: false, id: "peers", label: null)));
    }

    [Fact] // :24043-24044 pin: lastGood with no reason says "no new data" with the age, else the stale sentence
    public void LastGoodSaysNoNewDataWithTheAgeElseFallsBackToStale()
    {
        Assert.Equal(C.LastGood.Localized(Name, Age), Line(Row("lastGood")));
        Assert.Equal(C.Stale.Localized(Name), Line(Row("lastGood", data: false)));
    }

    [Fact] // :24059
    public void UnavailableWithNoReasonIsNoDataYet() =>
        Assert.Equal(C.NoDataYet.Localized(Name), Line(Row("unavailable", null, data: false)));

    [Fact] // :24062-24067
    public void ARefreshInFlightCoversEverySentence()
    {
        RemoteDevice[] devices =
        [
            Row("lastGood", "busy"), Row("unavailable", null, data: false), Row("refused", "stopped", data: false),
            Row("lastGood", "unreachable"), Row("refused", "error", data: false), Row("stale", null, data: false),
            Row("invalidReply", null),
        ];
        foreach (var device in devices)
        {
            Assert.True(C.GettingUsage.Localized(Name) == Line(device, inFlight: true),
                $"{device.State}/{device.Reason ?? "-"}");
        }
    }

    [Fact] // :24069
    public void WhenEveryDeviceIsFreshThereIsNoLineInFlightOrNot()
    {
        RemoteDevice[] rows = [new(Id: "local", State: "local"), Row("fresh")];
        Assert.Empty(C.StatusLines(rows, [(Fp, Name)], true, false, Now));
    }

    [Fact] // :24074
    public void ASelectedDeviceNoPageListsYetReadsAsNoDataOrGettingUsage()
    {
        (string, string)[] peers = [("bbbbbbbbbbbbbbbb", "[::1]:47000")];
        Same([C.NoDataYet.Localized("[::1]:47000")], C.StatusLines([], peers, false, false, Now));
        Same([C.GettingUsage.Localized("[::1]:47000")], C.StatusLines([], peers, true, false, Now));
    }

    [Fact] // :24085
    public void AnotherDevicesTextIsAFormatArgumentShownVerbatim()
    {
        const string nasty = "100% %@ %lld {x} {0}";
        Assert.Contains(nasty, Line(Row("lastGood", "unreachable", label: nasty)));
        Assert.Contains(nasty, Line(Row("lastGood", "busy", label: nasty), retry: true));
    }

    [Fact] // :24089
    public void AddressIsHostPortWithIpv6InBrackets()
    {
        Assert.Equal("10.0.0.2:47000", C.Address("10.0.0.2", 47000));
        Assert.Equal("[::1]:47000", C.Address("::1", 47000));
    }

    // ---- DR-2c short status (SelfTest.swift :24103-24141) ------------------

    private static C.ShortStatusResult Short(RemoteDevice? d, bool selected = true, bool inFlight = false) =>
        C.ShortStatus(d, selected, inFlight, Now);

    private static RemoteDevice SRow(string state, string? reason = null, long? at = T0, string id = Fp) =>
        new(Id: id, State: state, Label: "Studio Mac", LastSuccessAt: at, Reason: reason);

    [Fact] // :24118-24141: one case per row of the table, in its order
    public void ShortStatusTable()
    {
        var age = Age;
        (string Name, RemoteDevice? Device, bool Selected, bool InFlight, C.ShortDot Dot, string Text)[] table =
        [
            ("not selected wins over everything", SRow("fresh"), false, true, C.ShortDot.Grey, C.ShortNotIncluded.Localized()),
            ("in flight", SRow("fresh"), true, true, C.ShortDot.Progress, C.ShortUpdating.Localized()),
            ("fresh with time", SRow("fresh"), true, false, C.ShortDot.Green, C.ShortConnectedAt.Localized(age)),
            ("fresh without time", SRow("fresh", at: null), true, false, C.ShortDot.Green, C.ShortConnected.Localized()),
            ("stopped", SRow("lastGood", "stopped"), true, false, C.ShortDot.Grey, C.ShortStopped.Localized()),
            ("refused", SRow("refused"), true, false, C.ShortDot.Grey, C.ShortRefused.Localized()),
            ("incompatible", SRow("incompatible"), true, false, C.ShortDot.Orange, C.ShortIncompatible.Localized()),
            ("invalidReply", SRow("invalidReply"), true, false, C.ShortDot.Orange, C.ShortUnusable.Localized()),
            ("overLimit", SRow("overLimit"), true, false, C.ShortDot.Orange, C.ShortUnusable.Localized()),
            ("unreachable, lastGood with time", SRow("lastGood", "unreachable"), true, false, C.ShortDot.Orange,
                C.ShortUnreachable.Localized(C.TailFrom.Localized(age))),
            ("timeout, lastGood without time", SRow("lastGood", "timeout", null), true, false, C.ShortDot.Orange,
                C.ShortUnreachable.Localized(C.TailEarlier.Localized())),
            ("unreachable, stale", SRow("stale", "unreachable", null), true, false, C.ShortDot.Orange,
                C.ShortUnreachable.Localized(C.TailEarlier.Localized())),
            ("unreachable, no data", SRow("unavailable", "unreachable", null), true, false, C.ShortDot.Orange,
                C.ShortUnreachable.Localized(C.TailNone.Localized())),
            ("busy, lastGood with time", SRow("lastGood", "busy"), true, false, C.ShortDot.Orange,
                C.ShortBusy.Localized(C.TailFrom.Localized(age))),
            ("busy, no data", SRow("unavailable", "busy", null), true, false, C.ShortDot.Orange,
                C.ShortBusy.Localized(C.TailNone.Localized())),
            ("error, lastGood with time", SRow("lastGood", "error"), true, false, C.ShortDot.Orange,
                C.ShortError.Localized(C.TailFrom.Localized(age))),
            ("error, no data", SRow("unavailable", "error", null), true, false, C.ShortDot.Orange,
                C.ShortError.Localized(C.TailNone.Localized())),
            ("stale without reason", SRow("stale", null, null), true, false, C.ShortDot.Orange, C.ShortEarlier.Localized()),
            ("lastGood without time or reason", SRow("lastGood", null, null), true, false, C.ShortDot.Orange,
                C.ShortEarlier.Localized()),
            ("lastGood with time, no reason", SRow("lastGood"), true, false, C.ShortDot.Orange, C.ShortFrom.Localized(age)),
            ("fresh, older than ConnectedFor", SRow("fresh", null, T0 - 600), true, false, C.ShortDot.Grey,
                C.ShortFrom.Localized(Format.RelativeTime((ulong)(T0 - 600), Now))),
            ("fresh, exactly ConnectedFor old (not greater)", SRow("fresh", null, T0 - 300), true, false, C.ShortDot.Green,
                C.ShortConnectedAt.Localized(Format.RelativeTime((ulong)(T0 - 300), Now))),
            ("fresh, one second past ConnectedFor", SRow("fresh", null, T0 - 301), true, false, C.ShortDot.Grey,
                C.ShortFrom.Localized(Format.RelativeTime((ulong)(T0 - 301), Now))),
            ("unavailable, no reason", SRow("unavailable", null, null), true, false, C.ShortDot.Orange, C.ShortNoData.Localized()),
            ("no row, selected", null, true, false, C.ShortDot.Orange, C.ShortNoData.Localized()),
            ("no row, in flight", null, true, true, C.ShortDot.Progress, C.ShortUpdating.Localized()),
        ];
        foreach (var (name, device, selected, inFlight, dot, text) in table)
        {
            var got = Short(device, selected, inFlight);
            Assert.True(got.Dot == dot && got.Text == text, $"DR2C table: {name} (got {got.Dot} {got.Text})");
        }
        // :24143 the three tails are three different phrases; no phrase holds a name.
        Assert.Equal(3, new[] { C.TailFrom.Localized(age), C.TailEarlier.Localized(), C.TailNone.Localized() }
            .Distinct().Count());
        Assert.All(table, row => Assert.DoesNotContain("Studio Mac", row.Text));
    }

    [Fact] // :24170-24174
    public void WorstRowTheLeastDataWinsAsInTheStatusLine()
    {
        RemoteDevice[] rows =
        [
            SRow("fresh"), SRow("lastGood", "busy"), SRow("unavailable", "unreachable", null),
            SRow("fresh", id: "bbbbbbbbbbbbbbbb"),
        ];
        Assert.Equal("unavailable", C.WorstRow(Fp, rows)?.State);
        Assert.Equal("unavailable", C.WorstRow(Fp, rows.AsEnumerable().Reverse())?.State);
        Assert.Equal("fresh", C.WorstRow("bbbbbbbbbbbbbbbb", rows)?.State);
        Assert.Null(C.WorstRow("cccccccccccccccc", rows));
    }

    [Fact] // :24178-24186 (the menu, the card and Settings read one string)
    public void MenuCardAndSettingsObtainTheIdenticalStatus()
    {
        var snap = new DeviceOverlaySnapshot(
            [SRow("fresh"), SRow("lastGood", "unreachable")],
            new Dictionary<string, bool> { [Fp] = true }, false, new Dictionary<string, string>());
        var direct = Short(C.WorstRow(Fp, snap.Rows));
        Assert.Equal(direct, snap.ShortStatus(Fp, Now));
        Assert.Equal(direct, snap.SettingsStatus(Peer(), Now));
        Assert.Equal(direct, Short(snap.Rows[1]));
        Assert.Equal(C.ShortUnreachable.Localized(C.TailFrom.Localized(Age)), direct.Text);
    }

    [Fact] // macOS Equatable: equal contents are equal, a changed row is not
    public void SnapshotsCompareByContent()
    {
        DeviceOverlaySnapshot Make(string state) => new(
            [SRow(state)], new Dictionary<string, bool> { [Fp] = true }, false,
            new Dictionary<string, string> { [Fp] = "n" });
        var a = Make("fresh");
        var b = Make("fresh");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Make("stale"));
        Assert.NotEqual(a, a with { InFlight = true });
        Assert.NotEqual(a, a with { Selection = new Dictionary<string, bool> { [Fp] = false } });
        Assert.NotEqual(a, a with { Names = new Dictionary<string, string>() });
    }

    [Fact] // :24189-24195
    public void SettingsShowsNoStatusWithoutAResultForASelectedDevice()
    {
        var none = new DeviceOverlaySnapshot(
            [], new Dictionary<string, bool> { [Fp] = true }, true, new Dictionary<string, string>());
        var notSelected = new DeviceOverlaySnapshot(
            [], new Dictionary<string, bool> { [Fp] = false }, false, new Dictionary<string, string>());
        Assert.Null(none.SettingsStatus(Peer(), Now));
        Assert.Null(DeviceOverlaySnapshot.Empty.SettingsStatus(Peer(), Now));
        Assert.Equal(C.ShortNotIncluded.Localized(), notSelected.SettingsStatus(Peer(), Now)?.Text);
        Assert.Equal(C.ShortUpdating.Localized(), none.ShortStatus(Fp, Now).Text);
    }

    [Fact] // :24196-24200 (the C.displayName half; nameLine is a Settings view function, 4f)
    public void SettingsAndMenuNameADeviceByTheSameRule()
    {
        var learned = new Dictionary<string, string> { [Fp] = "New" };
        var none = new Dictionary<string, string>();
        Assert.Equal("New", C.DisplayName(Peer("Old"), learned));
        Assert.Equal("Old", C.DisplayName(Peer("Old"), none));
    }

    // ---- DR-2b2 names in the four places (SelfTest.swift :24252-24271) ----

    [Fact] // :24253
    public void StatusLineNamesTheDeviceElseHostPort()
    {
        var named = Peer("Studio Mac");
        var bare = Peer();
        Assert.Equal("Studio Mac", C.Name(named));
        Assert.Equal(Address, C.Name(bare));
        Same([C.NoDataYet.Localized("Studio Mac")],
            Lines([], peers: [("bbbbbbbbbbbbbbbb", C.Name(named))]));
        Same([C.NoDataYet.Localized(Address)],
            Lines([], peers: [("bbbbbbbbbbbbbbbb", C.Name(bare))]));
        Same([C.UnreachableData.Localized("Studio Mac", Format.RelativeTime((ulong)T0, Now))],
            Lines([Row("lastGood", "unreachable", label: "Studio Mac")]));
    }

    [Fact] // :24262
    public void MenuButtonNamesTheOneSelectedDeviceElseHostPort()
    {
        var named = Peer("Studio Mac");
        var bare = Peer();
        Assert.Equal("Studio Mac", C.MenuTitle(1, false, C.Name(named)));
        Assert.Equal(Address, C.MenuTitle(1, false, C.Name(bare)));
        Assert.Equal(C.ThisPc.Localized(), C.MenuTitle(1, true, C.Name(named)));
        Assert.Equal(C.DeviceCount.Localized(2), C.MenuTitle(2, true, C.Name(named)));
    }

    // ---- DR-2b2 same name, cost sentence, first scan, hostile -------------

    [Fact] // :24321-24326
    public void SameNameTwoIdsTwoLinesSameIdTwoPagesOneLine()
    {
        RemoteDevice[] twin =
        [
            Row("lastGood", "busy", id: "aaaaaaaaaaaaaaaa", label: "Studio Mac"),
            Row("lastGood", "busy", id: "bbbbbbbbbbbbbbbb", label: "Studio Mac"),
        ];
        var lines = Lines(twin);
        Assert.Equal(2, lines.Count);
        Assert.Equal(lines[0], lines[1]);
        Assert.Equal(2, Lines([.. twin, .. twin]).Count);
    }

    [Fact] // :24330-24336
    public void CostSentenceNeedsDataAndTheFlagAndFollowsItsOwnStateLine()
    {
        var cost = C.CostIncomplete.Localized("Studio Mac");
        Same([cost], Lines([Row("fresh", label: "Studio Mac", incomplete: true)]));
        Same(
            [C.BusyData.Localized("Studio Mac", Format.RelativeTime((ulong)T0, Now)), cost],
            Lines([Row("lastGood", "busy", label: "Studio Mac", incomplete: true)]));
        Same([C.Stale.Localized("Studio Mac"), cost],
            Lines([Row("stale", label: "Studio Mac", incomplete: true)]));
    }

    [Fact] // :24337-24343
    public void NoCostSentenceWithoutTheFlagOrWithoutData()
    {
        var cost = C.CostIncomplete.Localized("Studio Mac");
        Assert.Empty(Lines([Row("fresh", label: "Studio Mac", incomplete: false)]));
        Assert.DoesNotContain(cost,
            Lines([Row("refused", "stopped", label: "Studio Mac", incomplete: true)]));
        Assert.DoesNotContain(cost, Lines([Row("unavailable", label: "Studio Mac", incomplete: true)]));
        // a fresh device's stays while another refresh runs
        Same([cost], Lines([Row("fresh", label: "Studio Mac", incomplete: true)], inFlight: true));
    }

    [Fact] // :24344-24346
    public void OnePageSayingTheCostIsIncompleteIsEnoughOncePerDevice() =>
        Same([C.CostIncomplete.Localized("Studio Mac")],
            Lines([
                Row("fresh", id: "aaaaaaaaaaaaaaaa", label: "Studio Mac", incomplete: true),
                Row("fresh", id: "aaaaaaaaaaaaaaaa", label: "Studio Mac", incomplete: false),
            ]));

    [Fact] // :24349-24359
    public void FirstScanSentenceOnlyWhenTheDeviceNeverAnsweredAndAFetchIsInFlight()
    {
        var first = C.GettingUsageFirstScan.Localized("Studio Mac");
        var plain = C.GettingUsage.Localized("Studio Mac");
        Same([first], Lines([Row("unavailable", label: "Studio Mac", answered: false)], inFlight: true));
        Same([plain], Lines([Row("unavailable", label: "Studio Mac", answered: true)], inFlight: true));
        Same([C.NoDataYet.Localized("Studio Mac")],
            Lines([Row("unavailable", label: "Studio Mac", answered: false)], inFlight: false));
        Assert.NotEqual(plain, first);
        Assert.StartsWith(plain, first);
    }

    [Fact] // :24361-24373 (everAnswered combined over all of a device's rows)
    public void ADeviceThatAnsweredOnOnePageDoesNotGetTheFirstScanSentence()
    {
        var first = C.GettingUsageFirstScan.Localized("Studio Mac");
        var plain = C.GettingUsage.Localized("Studio Mac");
        Same([plain], Lines([
            Row("fresh", label: "Studio Mac", answered: true),
            Row("unavailable", label: "Studio Mac", answered: false)], inFlight: true));
        Same([plain], Lines([
            Row("unavailable", label: "Studio Mac", answered: false),
            Row("fresh", label: "Studio Mac", answered: true)], inFlight: true));
        Same([first], Lines([
            Row("fresh", label: "Studio Mac", answered: false),
            Row("unavailable", label: "Studio Mac", answered: false)], inFlight: true));
    }

    [Fact] // :24505-24514
    public void AHostileNameAppearsLiterallyEverywhere()
    {
        const string hostile = "A%@ %d %lld{0}\u202EB";
        string[] lines =
        [
            C.StatusLine(Row("lastGood", "unreachable", label: hostile),
                C.Name(Row("lastGood", label: hostile)), false, false, Now),
            C.StatusLine(Row("unavailable", label: hostile, answered: false), hostile, true, false, Now),
            .. Lines([Row("fresh", label: hostile, incomplete: true)]),
        ];
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains(hostile, line));

        var peer = Peer(hostile);
        Assert.Equal(hostile, C.Name(peer));
        Assert.Equal(hostile, C.MenuTitle(1, false, C.Name(peer)));
        Assert.Equal(hostile, DeviceTotals.Rows([Row("fresh", label: hostile)], [])[0].Name);
        Same([C.NoDataYet.Localized(hostile)],
            Lines([], peers: [("cccccccccccccccc", C.Name(peer))]));
    }

    // ---- SA-3d notes under a lens (SelfTest.swift :23625-23648, :23723-23753)

    private static IReadOnlyList<RemoteDevice> StatsDevices(string state) =>
    [
        new(Id: "local", State: "local"),
        new(Id: Fp, State: state, Label: Address, LastSuccessAt: T0),
    ];

    [Fact] // :23638
    public void TwoDevicesWithTheSameLabelAndStateKeepOneLineEach() =>
        Assert.Equal(2, Lines([
            new(Id: "aaaaaaaaaaaaaaaa", State: "unavailable", Label: Address),
            new(Id: "bbbbbbbbbbbbbbbb", State: "unavailable", Label: Address)]).Count);

    [Theory] // :23640-23648
    [InlineData("lastGood", "unavailable")]
    [InlineData("unavailable", "lastGood")]
    public void OneDeviceLineAndTheSaturatedLineOnce(string first, string second)
    {
        var rows = new[] { first, second }.SelectMany(s => StatsDevices(s)).ToList();
        var status = Lines(rows);
        var notes = C.Lines(new[] { first, second }.Select(s => (StatsDevices(s), true, true, true)));
        Same([C.NoDataYet.Localized(Address)], status);
        Same([C.Saturated.Localized()], notes);
        Assert.DoesNotContain(C.Duplicate.Localized(), status.Concat(notes));
        Assert.DoesNotContain(C.RangeLimited.Localized(), status.Concat(notes));
    }

    [Fact] // :23723-23735 (the "old key is gone" half: the macOS key as .NET placeholders)
    public void TheLastGoodLineSaysNoNewDataNotThatTheDeviceDidNotRespond()
    {
        Assert.Equal("{0}: no new data this time. Showing older data (last updated {1}).", C.LastGood);
        Load("zh-Hant");
        Assert.Contains("沒有取得新資料", C.LastGood.Localized());
        Load("zh-Hans");
        Assert.Contains("没有取得新数据", C.LastGood.Localized());
        Assert.Equal("{0} didn't respond. Showing older data (last updated {1}).",
            "{0} didn't respond. Showing older data (last updated {1}).".Localized());
    }

    [Fact] // :23736-23742
    public void EachNonFreshStateHasItsOwnLineThisPcAndAFreshDeviceHaveNone()
    {
        string[] states = ["lastGood", "stale", "unavailable", "overLimit", "refused", "invalidReply", "incompatible"];
        var stateLines = states.Select(s => Lines([
            new RemoteDevice(Id: Fp, State: s, Label: Address, LastSuccessAt: T0)])).ToList();
        Assert.All(stateLines, l => Assert.Single(l));
        Assert.Equal(states.Length, stateLines.SelectMany(l => l).Distinct().Count());
        Assert.Empty(Lines(StatsDevices("fresh")));
        Assert.Empty(Lines(StatsDevices("local")));
    }

    [Fact] // :23743-23751
    public void ADevicesOwnTextIsVerbatimInItsLineAndSaturatedAddsOne()
    {
        const string nasty = "100% %@ %lld {x}";
        RemoteDevice[] hostile =
        [
            new(Id: "local", State: "local"),
            new(Id: "ffffffffffffffff", State: "refused", Label: nasty),
            new(Id: "eeeeeeeeeeeeeeee", State: "lastGood", Label: nasty, LastSuccessAt: T0),
        ];
        var lines = Lines(hostile).Concat(C.Lines([(hostile, true, true, true)])).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(2, lines.Count(l => l.Contains(nasty)));
        Assert.Contains(C.Saturated.Localized(), lines);
    }

    [Fact] // :23752-23760
    public void DuplicateAndRangeLimitedNeverMakeALineSaturatedDoes()
    {
        var fresh = StatsDevices("fresh");
        Assert.Empty(C.Lines([(fresh, true, false, true)]));
        Same([C.Saturated.Localized()], C.Lines([(fresh, true, true, true)]));
        Same([C.NoDataYet.Localized(Address)], Lines(StatsDevices("unavailable")));
    }

    [Fact] // :23761-23765
    public void TheInfoPopoverHasTheDuplicateSentencePlusTheRangeOneOnlyWhenReported()
    {
        Same([C.Duplicate.Localized()], C.Explanations(false));
        Same([C.Duplicate.Localized(), C.RangeLimited.Localized()], C.Explanations(true));
    }
}

/// <summary>Per-key checks of the copy and of the two Windows tables: the
/// literal strings (so an unfilled or mis-indexed placeholder fails, which a
/// comparison of <c>X.Localized(args)</c> with itself would not catch), the
/// approved zh-Hant sentences, and the presence checks (SelfTest.swift
/// :23716-23722, :24151-24165, :24521-24530, :24147-24165).</summary>
public class DeviceOverlayCopyTableTests : IDisposable
{
    public DeviceOverlayCopyTableTests() => Load("en");

    public void Dispose() => Load("en");

    private sealed record Lit(string Key, string Arg0, string? Arg1, string En, string Hant, string Hans);

    // Fixed name "Studio" and fixed time "5m ago" (count 3 for the two count
    // keys); one row for every key with a placeholder, expected strings typed
    // out in full.
    private static readonly Lit[] Literals =
    [
        new(DeviceOverlayCopy.DeviceCount, "3", null, "3 devices", "3 台裝置", "3 台设备"),
        new(DeviceOverlayCopy.LastGood, "Studio", "5m ago", "Studio: no new data this time. Showing older data (last updated 5m ago).", "Studio：這次沒有取得新資料，顯示的是舊資料（上次更新：5m ago）。", "Studio：这次没有取得新数据，显示的是旧数据（上次更新：5m ago）。"),
        new(DeviceOverlayCopy.Stale, "Studio", null, "Studio: showing older data. The time of its last update isn't known.", "Studio：顯示的是舊資料，不知道上次更新的時間。", "Studio：显示的是旧数据，不知道上次更新的时间。"),
        new(DeviceOverlayCopy.Unavailable, "Studio", null, "Studio has no data to show.", "Studio 沒有可顯示的資料。", "Studio 没有可显示的数据。"),
        new(DeviceOverlayCopy.OverLimit, "Studio", null, "Studio has too much data to include.", "Studio 的資料量太大，無法納入。", "Studio 的数据量太大，无法纳入。"),
        new(DeviceOverlayCopy.Refused, "Studio", null, "Studio refused the request. Check Devices in Settings.", "Studio 拒絕了這個請求。請到「設定」的「裝置」查看。", "Studio 拒绝了这个请求。请到“设置”的“设备”查看。"),
        new(DeviceOverlayCopy.InvalidReply, "Studio", null, "Studio sent a reply Syrtis couldn't use.", "Studio 回傳的內容 Syrtis 無法使用。", "Studio 返回的内容 Syrtis 无法使用。"),
        new(DeviceOverlayCopy.Incompatible, "Studio", null, "Studio can't answer this request. It may need an update.", "Studio 無法回應這個請求，可能需要更新。", "Studio 无法响应这个请求，可能需要更新。"),
        new(DeviceOverlayCopy.GettingUsage, "Studio", null, "◌ Getting usage from Studio…", "◌ 正在向 Studio 取得用量…", "◌ 正在向 Studio 获取用量…"),
        new(DeviceOverlayCopy.GettingUsageFirstScan, "Studio", null, "◌ Getting usage from Studio… The first time needs to scan its records, about 1 minute", "◌ 正在向 Studio 取得用量…首次需要掃描紀錄，約 1 分鐘", "◌ 正在向 Studio 获取用量…首次需要扫描记录，约 1 分钟"),
        new(DeviceOverlayCopy.CostIncomplete, "Studio", null, "The cost for Studio may be incomplete (its price data is incomplete). Tokens and message counts are included in the totals.", "Studio 的費用可能不完整（那台的價格資料不齊）。token 與訊息數已合計。", "Studio 的费用可能不完整（那台设备的价格数据不全）。token 数与消息数已合计。"),
        new(DeviceOverlayCopy.UnreachableData, "Studio", "5m ago", "Can't reach Studio. Make sure it has Syrtis open and is on the same network. Showing data from 5m ago.", "連不上 Studio。請確認那台有開 Syrtis 而且在同一個網路。顯示的是 5m ago的資料。", "连不上 Studio。请确认那台设备已打开 Syrtis，并且在同一个网络。显示的是 5m ago的数据。"),
        new(DeviceOverlayCopy.UnreachableNoData, "Studio", null, "No data from Studio yet. Can't reach it. Make sure it has Syrtis open and is on the same network.", "還沒有 Studio 的資料。連不上那台，請確認它有開 Syrtis 而且在同一個網路。", "还没有 Studio 的数据。连不上那台设备，请确认它已打开 Syrtis，并且在同一个网络。"),
        new(DeviceOverlayCopy.BusyData, "Studio", "5m ago", "Studio is busy for now. Showing data from 5m ago.", "Studio 暫時忙碌。顯示的是 5m ago的資料。", "Studio 暂时忙碌。显示的是 5m ago的数据。"),
        new(DeviceOverlayCopy.BusyDataRetry, "Studio", "5m ago", "Studio is busy for now and will be retried in 1 minute. Showing data from 5m ago.", "Studio 暫時忙碌，1 分鐘後自動再試。顯示的是 5m ago的資料。", "Studio 暂时忙碌，1 分钟后自动再试。显示的是 5m ago的数据。"),
        new(DeviceOverlayCopy.BusyNoData, "Studio", null, "No data from Studio yet. It is busy for now.", "還沒有 Studio 的資料。那台暫時忙碌。", "还没有 Studio 的数据。那台设备暂时忙碌。"),
        new(DeviceOverlayCopy.BusyNoDataRetry, "Studio", null, "No data from Studio yet. It is busy for now and will be retried in 1 minute.", "還沒有 Studio 的資料。那台暫時忙碌，1 分鐘後自動再試。", "还没有 Studio 的数据。那台设备暂时忙碌，1 分钟后自动再试。"),
        new(DeviceOverlayCopy.Stopped, "Studio", null, "Studio stopped sharing. Reconnect it in Settings → Devices.", "Studio 已停止分享。到「設定 → 裝置」重新連結。", "Studio 已停止分享。到“设置 → 设备”重新连接。"),
        new(DeviceOverlayCopy.ErrorData, "Studio", "5m ago", "Studio couldn't answer this request. Showing data from 5m ago.", "Studio 無法回應這次請求。顯示的是 5m ago的資料。", "Studio 无法响应这次请求。显示的是 5m ago的数据。"),
        new(DeviceOverlayCopy.ErrorNoData, "Studio", null, "No data from Studio yet. It couldn't answer this request.", "還沒有 Studio 的資料。那台無法回應這次請求。", "还没有 Studio 的数据。那台设备无法响应这次请求。"),
        new(DeviceOverlayCopy.NoDataYet, "Studio", null, "No data from Studio yet.", "還沒有 Studio 的資料。", "还没有 Studio 的数据。"),
        new(DeviceOverlayCopy.Combined, "3", null, "3 devices combined", "3 台裝置合計", "3 台设备合计"),
        new(DeviceOverlayCopy.UpdatedAt, "Studio", null, "Updated Studio", "更新於 Studio", "更新于 Studio"),
        new(DeviceOverlayCopy.ShortConnectedAt, "Studio", null, "Connected · updated Studio", "已連線 · Studio更新", "已连接 · Studio更新"),
        new(DeviceOverlayCopy.ShortUnreachable, "Studio", null, "Can't reach · Studio", "連不上 · Studio", "连不上 · Studio"),
        new(DeviceOverlayCopy.ShortBusy, "Studio", null, "Busy for now · Studio", "暫時忙碌 · Studio", "暂时繁忙 · Studio"),
        new(DeviceOverlayCopy.ShortError, "Studio", null, "Can't answer · Studio", "無法回應 · Studio", "无法响应 · Studio"),
        new(DeviceOverlayCopy.ShortFrom, "Studio", null, "Showing data from Studio", "顯示 Studio的資料", "显示 Studio的数据"),
        new(DeviceOverlayCopy.TailFrom, "Studio", null, "showing data from Studio", "顯示 Studio的資料", "显示 Studio的数据"),
    ];

    private static string Fill(string key, Lit lit) =>
        lit.Arg1 is null ? key.Localized(lit.Arg0) : key.Localized(lit.Arg0, lit.Arg1);

    [Fact]
    public void EveryFormattedKeyFamilyFillsItsPlaceholdersInEnglish()
    {
        foreach (var lit in Literals)
        {
            Assert.Equal(lit.En, Fill(lit.Key, lit));
        }
    }

    [Fact]
    public void EveryFormattedKeyFamilyFillsItsPlaceholdersInZhHant()
    {
        Load("zh-Hant");
        foreach (var lit in Literals)
        {
            Assert.Equal(lit.Hant, Fill(lit.Key, lit));
        }
    }

    [Fact] // two-argument keys prove the {0}/{1} order under the zh-Hans table too
    public void EveryFormattedKeyFamilyFillsItsPlaceholdersInZhHans()
    {
        Load("zh-Hans");
        foreach (var lit in Literals)
        {
            Assert.Equal(lit.Hans, Fill(lit.Key, lit));
        }
        Assert.Equal(
            "连不上 Studio。请确认那台设备已打开 Syrtis，并且在同一个网络。显示的是 5m ago的数据。",
            C.UnreachableData.Localized("Studio", "5m ago"));
    }

    [Fact]
    public void TheLiteralTableCoversEveryKeyWithAPlaceholder()
    {
        var formatted = C.All.Where(k => k.Contains("{0}")).ToHashSet();
        Assert.Equal(formatted, Literals.Select(l => l.Key).ToHashSet());
    }

    [Fact] // :24521
    public void TheUnreachableLineHasNoSpaceBetweenTheTimeAndTheNextWordInEnglishAndZh()
    {
        // The literal check: the zh-Hant sentence ends "...顯示的是 5m ago的資料。" (time, then 的).
        Load("zh-Hant");
        Assert.Equal(
            "連不上 Studio。請確認那台有開 Syrtis 而且在同一個網路。顯示的是 5m ago的資料。",
            C.UnreachableData.Localized("Studio", "5m ago"));
    }

    [Fact] // :24983-24995 (screen 3)
    public void NoSpaceFollowsTheRelativeTimeInTheStatusSentences()
    {
        var spaced = new List<string>();
        foreach (var tag in new[] { "zh-Hant", "zh-Hans" })
        {
            var table = Table(tag);
            foreach (var key in new[] { C.UnreachableData, C.BusyData, C.BusyDataRetry, C.ErrorData })
            {
                if (table[key].Contains("{1} ")) { spaced.Add($"{tag} {key[..20]}"); }
            }
            foreach (var key in new[] { C.ShortConnectedAt, C.ShortFrom, C.TailFrom })
            {
                if (table[key].Contains("{0} ")) { spaced.Add($"{tag} {key[..10]}"); }
            }
        }
        Assert.Empty(spaced);
    }

    [Theory] // :24147-24151 and :23716 style: the approved zh-Hant copy, as .NET placeholders
    [InlineData(C.GettingUsage, "◌ 正在向 {0} 取得用量…")]
    [InlineData(C.UnreachableData, "連不上 {0}。請確認那台有開 Syrtis 而且在同一個網路。顯示的是 {1}的資料。")]
    [InlineData(C.UnreachableNoData, "還沒有 {0} 的資料。連不上那台，請確認它有開 Syrtis 而且在同一個網路。")]
    [InlineData(C.BusyDataRetry, "{0} 暫時忙碌，1 分鐘後自動再試。顯示的是 {1}的資料。")]
    [InlineData(C.BusyData, "{0} 暫時忙碌。顯示的是 {1}的資料。")]
    [InlineData(C.BusyNoDataRetry, "還沒有 {0} 的資料。那台暫時忙碌，1 分鐘後自動再試。")]
    [InlineData(C.BusyNoData, "還沒有 {0} 的資料。那台暫時忙碌。")]
    [InlineData(C.Stopped, "{0} 已停止分享。到「設定 → 裝置」重新連結。")]
    [InlineData(C.ErrorData, "{0} 無法回應這次請求。顯示的是 {1}的資料。")]
    [InlineData(C.ErrorNoData, "還沒有 {0} 的資料。那台無法回應這次請求。")]
    [InlineData(C.NoDataYet, "還沒有 {0} 的資料。")]
    [InlineData(C.Combined, "{0} 台裝置合計")]
    [InlineData(C.QuotaThisPcOnly, "額度只計這台電腦")]
    [InlineData(C.ThisPcOnly, "只計這台電腦")]
    [InlineData(C.PerDevice, "各裝置")]
    [InlineData(C.ShortNotIncluded, "未納入")]
    [InlineData(C.ShortUpdating, "更新中…")]
    [InlineData(C.ShortConnectedAt, "已連線 · {0}更新")]
    [InlineData(C.ShortConnected, "已連線")]
    [InlineData(C.ShortStopped, "已停止分享")]
    [InlineData(C.ShortRefused, "已拒絕 · 到設定的裝置查看")]
    [InlineData(C.ShortIncompatible, "需要更新版本")]
    [InlineData(C.ShortUnusable, "資料無法使用")]
    [InlineData(C.ShortUnreachable, "連不上 · {0}")]
    [InlineData(C.ShortBusy, "暫時忙碌 · {0}")]
    [InlineData(C.ShortError, "無法回應 · {0}")]
    [InlineData(C.ShortEarlier, "顯示先前的資料")]
    [InlineData(C.ShortNoData, "還沒有資料")]
    [InlineData(C.TailFrom, "顯示 {0}的資料")]
    [InlineData(C.TailEarlier, "顯示先前的資料")]
    [InlineData(C.TailNone, "還沒有資料")]
    [InlineData(C.GettingUsageFirstScan, "◌ 正在向 {0} 取得用量…首次需要掃描紀錄，約 1 分鐘")]
    [InlineData(C.CostIncomplete, "{0} 的費用可能不完整（那台的價格資料不齊）。token 與訊息數已合計。")]
    [InlineData(C.ThisPc, "這台電腦")]
    [InlineData(C.ThisPcLive, "(這台)")]
    [InlineData(C.WindowUsageThisPcOnly, "窗內用量只計這台電腦")]
    public void TheZhHantSentenceIsTheApprovedCopy(string key, string expected) =>
        Assert.Equal(expected, Table("zh-Hant")[key]);

    private static IReadOnlyDictionary<string, string> Table(string tag) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"strings-{tag}.json")))!;

    [Fact] // :23716-23722, :24151-24165, :24525-24530
    public void EveryOverlayKeyHasAZhHantAndZhHansValueDifferentFromTheKey()
    {
        var missing = new List<string>();
        foreach (var tag in new[] { "zh-Hant", "zh-Hans" })
        {
            var table = Table(tag);
            foreach (var key in C.All)
            {
                if (!table.TryGetValue(key, out var value) || value == key || string.IsNullOrEmpty(value))
                {
                    missing.Add($"{tag}: {key}");
                }
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryZhValueCarriesTheSamePlaceholdersAsItsKey()
    {
        var wrong = new List<string>();
        foreach (var tag in new[] { "zh-Hant", "zh-Hans" })
        {
            var table = Table(tag);
            foreach (var key in C.All)
            {
                if (Placeholders(table[key]) != Placeholders(key)) { wrong.Add($"{tag}: {key}"); }
            }
        }
        Assert.Empty(wrong);
    }

    private static string Placeholders(string s) =>
        string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order());

    [Fact]
    public void NoOverlayKeyOrValueHoldsMacACocoaPlaceholderOrANonPcWord()
    {
        var bad = new List<string>();
        foreach (var tag in new[] { "zh-Hant", "zh-Hans" })
        {
            var table = Table(tag);
            foreach (var key in C.All)
            {
                foreach (var text in new[] { key, table[key] })
                {
                    if (new[] { "Mac", "%@", "%l", "$@" }.Any(text.Contains)) { bad.Add($"{tag}: {text}"); }
                }
            }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void TheKeyListHasFiftySixDistinctKeysAndThePcWording()
    {
        Assert.Equal(56, C.All.Count);
        Assert.Equal(C.All.Count, C.All.Distinct().Count());
        Assert.Equal("This PC", C.ThisPc);
        Assert.Equal("(this PC)", C.ThisPcLive);
        Assert.Equal("Quota counts this PC only", C.QuotaThisPcOnly);
        Assert.Equal("This PC only", C.ThisPcOnly);
        Assert.Equal("Usage inside windows counts this PC only", C.WindowUsageThisPcOnly);
    }
}
