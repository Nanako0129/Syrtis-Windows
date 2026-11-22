using System.Globalization;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>One row of the "Per device" card (macOS DeviceTotals.Row,
/// DeviceTotalsCard.swift :10-21). <c>Messages</c>, <c>Tokens</c> and
/// <c>Cost</c> are null when none of this device's part was merged into the
/// page; <c>UpdatedAt</c> is Unix seconds of the last fresh pull, null for
/// this PC.</summary>
public sealed record DeviceTotalsRow(
    string Id,
    string Name,
    bool IsLocal,
    long? Messages,
    long? Tokens,
    double? Cost,
    long? UpdatedAt);

/// <summary>The "Per device" subtotals under Overview, Monthly and Daily (macOS
/// DeviceTotalsCard.swift :9-45 and :115-129). The numbers come from the
/// core's <c>devices[].totals</c> (one entry per client); the only sum made
/// here is over the entries of the clients the page shows.</summary>
public static class DeviceTotals
{
    /// <summary>One row per device of the page. A device's figures are its
    /// core-computed per-client entries for <paramref name="clientIds"/> added
    /// together, so they equal the combined card above them when that card
    /// shows the same clients. <paramref name="names"/>: the newest real name
    /// per device id, which wins over the row's own label so the card agrees
    /// with the menu.</summary>
    public static IReadOnlyList<DeviceTotalsRow> Rows(
        IReadOnlyList<RemoteDevice> devices,
        IReadOnlyList<string> clientIds,
        IReadOnlyDictionary<string, string>? names = null)
    {
        var shown = clientIds.ToHashSet(StringComparer.Ordinal);
        return [.. devices.Select(device =>
        {
            var isLocal = device.Id == "local";
            var parts = device.Totals?.Where(t => shown.Contains(t.Client)).ToList();
            return new DeviceTotalsRow(
                device.Id,
                isLocal
                    ? DeviceOverlayCopy.ThisPc.Localized()
                    : names is not null && names.TryGetValue(device.Id, out var learned)
                        ? learned
                        : DeviceOverlayCopy.Name(device),
                isLocal,
                parts?.Aggregate(0L, (sum, t) => SaturatingAdd(sum, t.Messages)),
                parts?.Aggregate(0L, (sum, t) => SaturatingAdd(sum, t.Tokens)),
                parts?.Sum(t => t.CostUsd),
                isLocal ? null : device.LastSuccessAt);
        })];
    }

    /// <summary>Overflow clamps to the bound of the addend's sign (macOS
    /// <c>saturatingAdd</c> :47-51).</summary>
    public static long SaturatingAdd(long a, long b)
    {
        var sum = unchecked(a + b);
        // Overflow iff both operands differ in sign from the result.
        return ((a ^ sum) & (b ^ sum)) < 0 ? (b < 0 ? long.MinValue : long.MaxValue) : sum;
    }

    /// <summary>The card's detail line: "N msgs · T tokens · Updated A"
    /// (macOS DeviceTotalsCard.detail :115-129). The message count is grouped
    /// like macOS's <c>.formatted()</c>.</summary>
    public static string Detail(DeviceTotalsRow row, DateTimeOffset now, bool showsUpdated = true)
    {
        var parts = new List<string>();
        if (row.Messages is { } messages && row.Tokens is { } tokens)
        {
            parts.Add("{0} msgs".Localized(messages.ToString("N0", CultureInfo.CurrentCulture)));
            parts.Add("{0} tokens".Localized(Format.CompactTokens(tokens)));
        }
        else
        {
            parts.Add("—");
        }
        if (showsUpdated && row.UpdatedAt is >= 0 and var at)
        {
            parts.Add(DeviceOverlayCopy.UpdatedAt.Localized(Format.RelativeTime((ulong)at, now)));
        }
        return string.Join(" · ", parts);
    }
}
