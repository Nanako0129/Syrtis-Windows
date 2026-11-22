using System.Globalization;
using System.Text;

namespace TokenBar.Core;

/// <summary>The typed device name as it is sent (macOS
/// <c>DevicesModel.cleanedName</c>, DevicesModel.swift :233-262). The 128-byte
/// limit is not here: it applies to the cleaned name in the apply path (4f).</summary>
public static class DeviceNameCleaning
{
    /// <summary>Characters a device name may not contain, besides the control
    /// characters: line and paragraph separators and the bidirectional marks,
    /// embeddings, overrides and isolates. The agent refuses a name with any
    /// of them, and another device shows the name to its owner.</summary>
    private static readonly HashSet<int> Forbidden =
    [
        0x2028, 0x2029, 0x061C, 0x200E, 0x200F,
        .. Enumerable.Range(0x202A, 5),
        .. Enumerable.Range(0x2066, 4),
    ];

    /// <summary>Characters that draw nothing (the agent's
    /// <c>INVISIBLE_LABEL_CHARS</c>): a name made only of these and whitespace
    /// has nothing visible.</summary>
    private static readonly HashSet<int> Invisible =
    [
        0x00AD, 0x115F, 0x1160, 0x200B, 0x200C, 0x200D, 0x2060, 0x3164, 0xFEFF, 0x2800,
    ];

    /// <summary>Control and the forbidden separator and bidi characters
    /// removed, whitespace trimmed at both ends; null (no name) when nothing
    /// visible remains. Inner spaces and emoji joiners stay.</summary>
    public static string? CleanedName(string typed)
    {
        var kept = new StringBuilder();
        foreach (var rune in typed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control && !Forbidden.Contains(rune.Value))
            {
                kept.Append(rune.ToString());
            }
        }
        var trimmed = kept.ToString().Trim();
        var visible = trimmed.EnumerateRunes().Any(r => !Rune.IsWhiteSpace(r) && !Invisible.Contains(r.Value));
        return visible ? trimmed : null;
    }
}
