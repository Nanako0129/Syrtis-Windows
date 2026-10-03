using System.Globalization;

namespace TokenBar.Core;

/// <summary>
/// Middle truncation, "anthropic.claude-son…-20250929-v1:0" (macOS
/// <c>.truncationMode(.middle)</c> on model names and account labels).
/// WinUI's <c>TextTrimming</c> only trims the end, which hides exactly the
/// part that tells two dated model ids, or two config directories, apart.
/// </summary>
public static class MiddleEllipsis
{
    public const string Ellipsis = "…";

    /// <summary>The longest middle-truncated form of <paramref name="text"/>
    /// that <paramref name="fits"/> accepts: the head keeps the extra element
    /// when the kept count is odd, the tail the rest, joined by "…". The
    /// text unchanged when it fits; just "…" when nothing else does. Cuts on
    /// text elements, so a surrogate pair or combining sequence is never
    /// split. <paramref name="fits"/> must be monotonic in length (a real
    /// width measure is).</summary>
    public static string Fit(string text, Func<string, bool> fits)
    {
        if (fits(text))
        {
            return text;
        }

        var elements = Elements(text);
        // Binary search the largest kept count k in [0, n-1] whose form fits.
        int lo = 0, hi = elements.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (fits(Join(elements, mid)))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Join(elements, lo);
    }

    /// <summary><paramref name="kept"/> elements around "…": ceil(kept/2)
    /// from the start, floor(kept/2) from the end.</summary>
    private static string Join(string[] elements, int kept)
    {
        var head = (kept + 1) / 2;
        var tail = kept / 2;
        return string.Concat(elements[..head]) + Ellipsis
            + string.Concat(elements[(elements.Length - tail)..]);
    }

    private static string[] Elements(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            list.Add(e.GetTextElement());
        }

        return [.. list];
    }
}
