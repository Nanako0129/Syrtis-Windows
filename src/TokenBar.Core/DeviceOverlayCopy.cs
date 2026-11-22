using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Copy and pure decisions for the dashboard device overlay, ported
/// from macOS <c>DeviceOverlayCopy</c> (DeviceSelectionMenu.swift :11-318,
/// Syrtis f8a1a0ae). English is the localization key, as everywhere in this
/// port; the macOS Cocoa placeholders are .NET ones here (<c>%@</c> /
/// <c>%1$@</c> become <c>{0}</c> / <c>{1}</c> in the same argument order) and
/// the five "Mac" strings say "PC". Text that comes from another device (a
/// label, a fingerprint) is only ever a format argument, never a key.
/// Nothing here holds state: the overlay model (selection, polling, retry)
/// is SA-4e2.</summary>
public static class DeviceOverlayCopy
{
    // The device menu and its lens (SA-3d).
    public const string MenuHelp = "Choose which devices to include";
    public const string ThisPc = "This PC";
    public const string DeviceCount = "{0} devices";
    public const string OtherDevices = "Other devices";
    public const string Loading = "Loading usage from the selected devices…";
    public const string FailedNoData = "Couldn't load usage from the selected devices.";
    public const string KeptLast = "Couldn't update from the selected devices. Showing the last data received.";
    public const string ModelsHidden = "Model details aren't available yet for the selected devices.";
    public const string LastGood = "{0}: no new data this time. Showing older data (last updated {1}).";
    public const string Stale = "{0}: showing older data. The time of its last update isn't known.";
    public const string Unavailable = "{0} has no data to show.";
    public const string OverLimit = "{0} has too much data to include.";
    public const string Refused = "{0} refused the request. Check Devices in Settings.";
    public const string InvalidReply = "{0} sent a reply Syrtis couldn't use.";
    public const string Incompatible = "{0} can't answer this request. It may need an update.";
    public const string Duplicate = "Devices that sync their usage history can be counted more than once.";
    public const string Saturated = "Some totals reached their limit and are capped.";
    public const string RangeLimited = "For other devices, All years covers only the last 370 days.";
    public const string AboutCombined = "About combined usage";
    public const string GettingUsage = "◌ Getting usage from {0}…";
    public const string GettingUsageFirstScan = "◌ Getting usage from {0}… The first time needs to scan its records, about 1 minute";
    public const string CostIncomplete = "The cost for {0} may be incomplete (its price data is incomplete). Tokens and message counts are included in the totals.";
    public const string UnreachableData = "Can't reach {0}. Make sure it has Syrtis open and is on the same network. Showing data from {1}.";
    public const string UnreachableNoData = "No data from {0} yet. Can't reach it. Make sure it has Syrtis open and is on the same network.";
    public const string BusyData = "{0} is busy for now. Showing data from {1}.";
    public const string BusyDataRetry = "{0} is busy for now and will be retried in 1 minute. Showing data from {1}.";
    public const string BusyNoData = "No data from {0} yet. It is busy for now.";
    public const string BusyNoDataRetry = "No data from {0} yet. It is busy for now and will be retried in 1 minute.";
    public const string Stopped = "{0} stopped sharing. Reconnect it in Settings → Devices.";
    public const string ErrorData = "{0} couldn't answer this request. Showing data from {1}.";
    public const string ErrorNoData = "No data from {0} yet. It couldn't answer this request.";
    public const string NoDataYet = "No data from {0} yet.";
    public const string Combined = "{0} devices combined";
    public const string ThisPcLive = "(this PC)";
    public const string QuotaThisPcOnly = "Quota counts this PC only";
    public const string ThisPcOnly = "This PC only";
    public const string WindowUsageThisPcOnly = "Usage inside windows counts this PC only";
    public const string PerDevice = "Per device";
    public const string UpdatedAt = "Updated {0}";
    public const string ShortNotIncluded = "Not included";
    public const string ShortUpdating = "Updating…";
    public const string ShortConnectedAt = "Connected · updated {0}";
    public const string ShortConnected = "Connected";
    public const string ShortStopped = "Stopped sharing";
    public const string ShortRefused = "Refused · check Devices in Settings";
    public const string ShortIncompatible = "Needs an update";
    public const string ShortUnusable = "Data can't be used";
    public const string ShortUnreachable = "Can't reach · {0}";
    public const string ShortBusy = "Busy for now · {0}";
    public const string ShortError = "Can't answer · {0}";
    public const string ShortEarlier = "Showing earlier data";
    public const string ShortNoData = "No data yet";
    public const string ShortFrom = "Showing data from {0}";
    public const string TailFrom = "showing data from {0}";
    public const string TailEarlier = "showing earlier data";
    public const string TailNone = "no data yet";

    /// <summary>Every overlay key, for the presence check (macOS
    /// <c>all</c> :79-89).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        MenuHelp, ThisPc, DeviceCount, OtherDevices,
        Loading, FailedNoData, KeptLast, ModelsHidden,
        LastGood, Stale, Unavailable, OverLimit,
        Refused, InvalidReply, Incompatible, Duplicate,
        Saturated, RangeLimited, AboutCombined, GettingUsage,
        GettingUsageFirstScan, CostIncomplete, UnreachableData, UnreachableNoData,
        BusyData, BusyDataRetry, BusyNoData, BusyNoDataRetry,
        Stopped, ErrorData, ErrorNoData, NoDataYet,
        Combined, ThisPcLive, QuotaThisPcOnly, ThisPcOnly,
        WindowUsageThisPcOnly, PerDevice, UpdatedAt, ShortNotIncluded,
        ShortUpdating, ShortConnectedAt, ShortConnected, ShortStopped,
        ShortRefused, ShortIncompatible, ShortUnusable, ShortUnreachable,
        ShortBusy, ShortError, ShortEarlier, ShortNoData,
        ShortFrom, TailFrom, TailEarlier, TailNone,
    ];

    /// <summary>What names a device in a line: the label the core gave (the
    /// owner's name for it, else its <c>host:port</c>), else a fixed word for
    /// the unreadable-peer-list row, else its fingerprint (:94-96).</summary>
    public static string Name(RemoteDevice device) =>
        device.Label ?? (device.Id == "peers" ? OtherDevices.Localized() : device.Id);

    /// <summary>The same for a linked device that has no <c>devices</c> row
    /// yet: the name it last sent, else the address it was linked at
    /// (:100-102).</summary>
    public static string Name(RemotePeer peer) =>
        peer.NodeLabel ?? Address(peer.Host, peer.Port);

    /// <summary>The device menu's button: the one selected device's name,
    /// "This PC", or a count (:106-110).</summary>
    public static string MenuTitle(int selectedCount, bool local, string? peerName)
    {
        if (selectedCount == 1 && !local && peerName is not null) { return peerName; }
        if (selectedCount == 1) { return ThisPc.Localized(); }
        return DeviceCount.Localized(selectedCount);
    }

    /// <summary>Least data first: a device that is <c>unavailable</c> on one
    /// page and <c>lastGood</c> on another gets the <c>unavailable</c>
    /// sentence only (:114). An unknown state ranks 0, as macOS
    /// <c>firstIndex ?? 0</c>.</summary>
    private static readonly string[] Severity =
        ["unavailable", "overLimit", "refused", "incompatible", "invalidReply", "stale", "lastGood"];

    private static int Rank(string state) => Math.Max(0, Array.IndexOf(Severity, state));

    /// <summary>The sentences in the info popover beside the device menu:
    /// the two general notes about combining devices, which are not trouble
    /// and so are not lines under the lens. The range note only when the page
    /// reports it (:119-121).</summary>
    public static IReadOnlyList<string> Explanations(bool rangeLimited) =>
        rangeLimited
            ? [Duplicate.Localized(), RangeLimited.Localized()]
            : [Duplicate.Localized()];

    /// <summary>The dot in front of a short status. Never the only signal:
    /// the text carries the state (:125).</summary>
    public enum ShortDot { Green, Orange, Grey, Progress }

    public sealed record ShortStatusResult(ShortDot Dot, string Text);

    /// <summary>How long a fresh answer still reads as "Connected": two polls
    /// of the open popover (300 s each). An older answer means nothing is
    /// asking (the popover is closed) and the device's state is not known
    /// (:180).</summary>
    public const double ConnectedFor = 600;

    /// <summary>One device in one short phrase, no name. <paramref name="device"/>
    /// is the device's row (the worst one when several pages list it, see
    /// <see cref="WorstRow"/>), null when no page has listed it. The
    /// conditions are tested in the order of the DR-2c table (:132-174).
    /// <paramref name="now"/> is required (macOS defaults to Date()).</summary>
    public static ShortStatusResult ShortStatus(
        RemoteDevice? device, bool selected, bool inFlight, DateTimeOffset now)
    {
        if (!selected) { return new(ShortDot.Grey, ShortNotIncluded.Localized()); }
        if (inFlight) { return new(ShortDot.Progress, ShortUpdating.Localized()); }
        ulong? at = device?.LastSuccessAt is >= 0 and var secs ? (ulong)secs : null;
        var state = device?.State;
        if (state == "fresh")
        {
            if (at is not { } fresh) { return new(ShortDot.Green, ShortConnected.Localized()); }
            var age = Format.RelativeTime(fresh, now);
            // A fresh row older than two polls (the popover is closed, so
            // nothing asks) no longer says the device is connected.
            if (now.ToUnixTimeMilliseconds() / 1000.0 - fresh > ConnectedFor)
            {
                return new(ShortDot.Grey, ShortFrom.Localized(age));
            }
            return new(ShortDot.Green, ShortConnectedAt.Localized(age));
        }
        if (device?.Reason == "stopped") { return new(ShortDot.Grey, ShortStopped.Localized()); }
        if (state == "refused") { return new(ShortDot.Grey, ShortRefused.Localized()); }
        if (state == "incompatible") { return new(ShortDot.Orange, ShortIncompatible.Localized()); }
        if (state is "invalidReply" or "overLimit")
        {
            return new(ShortDot.Orange, ShortUnusable.Localized());
        }
        // Older data exists (lastGood, stale); how old is known only for a
        // lastGood with a time.
        var tail = state switch
        {
            "lastGood" => at is { } good
                ? TailFrom.Localized(Format.RelativeTime(good, now))
                : TailEarlier.Localized(),
            "stale" => TailEarlier.Localized(),
            _ => TailNone.Localized(),
        };
        switch (device?.Reason)
        {
            case "unreachable" or "timeout": return new(ShortDot.Orange, ShortUnreachable.Localized(tail));
            case "busy": return new(ShortDot.Orange, ShortBusy.Localized(tail));
            case "error": return new(ShortDot.Orange, ShortError.Localized(tail));
        }
        if (state == "lastGood" && at is { } last)
        {
            return new(ShortDot.Orange, ShortFrom.Localized(Format.RelativeTime(last, now)));
        }
        if (state is "stale" or "lastGood") { return new(ShortDot.Orange, ShortEarlier.Localized()); }
        return new(ShortDot.Orange, ShortNoData.Localized());
    }

    /// <summary>Of the rows <paramref name="rows"/> holds for device
    /// <paramref name="id"/>, the one to describe: the worst non-fresh one by
    /// severity (the status line's rule), else the first row. Null when no row
    /// is for <paramref name="id"/> (:185-190).</summary>
    public static RemoteDevice? WorstRow(string id, IEnumerable<RemoteDevice> rows)
    {
        var mine = rows.Where(r => r.Id == id).ToList();
        RemoteDevice? worst = null;
        // Strict "<": of equal ranks the first stays, as Swift min(by:).
        foreach (var row in mine.Where(r => r.State != "fresh"))
        {
            if (worst is null || Rank(row.State) < Rank(worst.State)) { worst = row; }
        }
        return worst ?? mine.FirstOrDefault();
    }

    /// <summary>The name shown for a linked device: the newest name a page
    /// reported (<paramref name="learned"/>, by fingerprint), else the peer
    /// list's, else its address (:194-196).</summary>
    public static string DisplayName(RemotePeer peer, IReadOnlyDictionary<string, string> learned) =>
        learned.TryGetValue(peer.Fingerprint, out var name) ? name : Name(peer);

    /// <summary>A producer's address as the core labels it: IPv6 hosts in
    /// brackets (:199-201).</summary>
    public static string Address(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    /// <summary>The status line: one sentence for each selected device that
    /// is not fresh, in the order the pages list them (selected devices that
    /// no page mentions yet follow, as "no data yet" or "getting usage"), each
    /// followed by its cost sentence when a page merged a body that said the
    /// device's price table was incomplete (:203-270).
    ///
    /// <paramref name="rows"/> are the <c>devices</c> rows of the pages the
    /// lens shows; a device on several pages gets its worst row, and one line
    /// per device id: two devices with the same name and state stay two lines.
    /// <paramref name="inFlight"/> replaces every state sentence with "getting
    /// usage from X" (with the first-scan half-sentence when the core says X
    /// never answered); <paramref name="retryPending"/> is whether a retry is
    /// actually armed, the only case that may say "again in a minute".
    /// <paramref name="names"/> (the newest real name per device id) wins over
    /// a row's own label, which may come from an older page.</summary>
    public static IReadOnlyList<string> StatusLines(
        IReadOnlyList<RemoteDevice> rows,
        IReadOnlyList<(string Id, string Name)> peers,
        bool inFlight,
        bool retryPending,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? names = null)
    {
        string NameOf(RemoteDevice d) =>
            names is not null && names.TryGetValue(d.Id, out var n) ? n : Name(d);

        // Per device id, in first-appearance order: its worst non-fresh row,
        // and whether a row with data says its cost is incomplete.
        var order = new List<string>();
        var worst = new Dictionary<string, RemoteDevice>();
        var costly = new Dictionary<string, RemoteDevice>();
        // True when ANY visible row of the device says it has answered,
        // whichever row is the worst: Graph may have answered while Models is
        // pending.
        var answered = new Dictionary<string, bool>();
        foreach (var row in rows.Where(r => r.State != "local"))
        {
            answered[row.Id] = answered.GetValueOrDefault(row.Id) || row.EverAnswered;
            if (row.State != "fresh")
            {
                if (worst.TryGetValue(row.Id, out var seen))
                {
                    if (Rank(row.State) < Rank(seen.State)) { worst[row.Id] = row; }
                }
                else
                {
                    worst[row.Id] = row;
                }
            }
            if (row.CostIncomplete && row.State is "fresh" or "lastGood" or "stale"
                && !costly.ContainsKey(row.Id))
            {
                costly[row.Id] = row;
            }
            if ((worst.ContainsKey(row.Id) || costly.ContainsKey(row.Id)) && !order.Contains(row.Id))
            {
                order.Add(row.Id);
            }
        }
        // A selected device no page lists yet has had no answer.
        var listed = rows.Select(r => r.Id).ToHashSet();
        foreach (var peer in peers.Where(p => !listed.Contains(p.Id)))
        {
            worst[peer.Id] = new RemoteDevice(
                Id: peer.Id, State: "unavailable", Label: peer.Name, LastSuccessAt: null);
            order.Add(peer.Id);
        }
        var output = new List<string>();
        foreach (var id in order)
        {
            if (worst.TryGetValue(id, out var row))
            {
                var device = row with { EverAnswered = answered.GetValueOrDefault(id, row.EverAnswered) };
                output.Add(StatusLine(device, NameOf(device), inFlight, retryPending, now));
            }
            if (costly.TryGetValue(id, out var costDevice))
            {
                output.Add(CostIncomplete.Localized(NameOf(costDevice)));
            }
        }
        return output;
    }

    /// <summary>One device's sentence; the R3 table of the plan, cell by cell
    /// (:273-309).</summary>
    public static string StatusLine(
        RemoteDevice device, string name, bool inFlight, bool retryPending, DateTimeOffset now)
    {
        if (inFlight)
        {
            return device.EverAnswered
                ? GettingUsage.Localized(name)
                : GettingUsageFirstScan.Localized(name);
        }
        string? age = device.State == "lastGood" && device.LastSuccessAt is >= 0 and var at
            ? Format.RelativeTime((ulong)at, now)
            : null;
        switch (device.State, device.Reason)
        {
            case ("stale", _): return Stale.Localized(name);
            case ("invalidReply", _): return InvalidReply.Localized(name);
            case ("overLimit", _): return OverLimit.Localized(name);
            case ("incompatible", _): return Incompatible.Localized(name);
            case (_, "unreachable" or "timeout"):
                return age is null ? UnreachableNoData.Localized(name) : UnreachableData.Localized(name, age);
            case (_, "busy"):
                return (age, retryPending) switch
                {
                    ({ } a, true) => BusyDataRetry.Localized(name, a),
                    ({ } a, false) => BusyData.Localized(name, a),
                    (null, true) => BusyNoDataRetry.Localized(name),
                    (null, false) => BusyNoData.Localized(name),
                };
            case (_, "stopped"): return Stopped.Localized(name);
            case (_, "error"):
                return age is null ? ErrorNoData.Localized(name) : ErrorData.Localized(name, age);
            case ("refused", _): return Refused.Localized(name);
            case ("lastGood", _):
                return age is null ? Stale.Localized(name) : LastGood.Localized(name, age);
            case ("unavailable", _) when device.Id == "peers": return Unavailable.Localized(name);
            default: return NoDataYet.Localized(name);
        }
    }

    /// <summary>The lines under a lens that are not about one device: the
    /// capped-totals warning, at most once across the pages (:313-317).
    /// <c>Duplicate</c> and <c>RangeLimited</c> never make a line (they are in
    /// <see cref="Explanations"/>).</summary>
    public static IReadOnlyList<string> Lines(
        IEnumerable<(IReadOnlyList<RemoteDevice> Devices, bool Duplicate, bool Saturated, bool RangeLimited)> pages) =>
        pages.Any(p => p.Saturated) ? [Saturated.Localized()] : [];
}

/// <summary>What the overlay knows about its devices, in the shape the three
/// places that show a short status read it (macOS DeviceOverlaySnapshot,
/// :324-347). Pure: the overlay model that publishes one is SA-4e2.</summary>
public sealed record DeviceOverlaySnapshot(
    IReadOnlyList<RemoteDevice> Rows,
    IReadOnlyDictionary<string, bool> Selection,
    bool InFlight,
    IReadOnlyDictionary<string, string> Names)
{
    public static DeviceOverlaySnapshot Empty { get; } = new(
        [], new Dictionary<string, bool>(), false, new Dictionary<string, string>());

    public DeviceOverlayCopy.ShortStatusResult ShortStatus(string fingerprint, DateTimeOffset now) =>
        DeviceOverlayCopy.ShortStatus(
            DeviceOverlayCopy.WorstRow(fingerprint, Rows),
            Selection.GetValueOrDefault(fingerprint), InFlight, now);

    /// <summary>Settings shows nothing for a device this process has no
    /// result for (no state is made up), or one the dashboard does not
    /// offer.</summary>
    public DeviceOverlayCopy.ShortStatusResult? SettingsStatus(RemotePeer peer, DateTimeOffset now)
    {
        if (!Selection.TryGetValue(peer.Fingerprint, out var selected)) { return null; }
        if (selected && DeviceOverlayCopy.WorstRow(peer.Fingerprint, Rows) is null) { return null; }
        return ShortStatus(peer.Fingerprint, now);
    }
}
