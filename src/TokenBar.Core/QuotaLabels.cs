namespace TokenBar.Core;

/// <summary>
/// The one place a quota window is named on screen — the window card's title and
/// tab pills, the strip card's row header, and the heatmap picker's entries.
/// <para>
/// Named static functions rather than composition inlined into the cards, which
/// is how macOS writes them and for the stated reason: <em>"static, and a named
/// function rather than composed inline, so the tests can assert the string
/// directly"</em> (<c>QuotaHeatmapCard.swift:130-132</c>). The cards live in
/// <c>DashboardView.*.cs</c>, which no test project compiles.
/// </para>
/// <para>
/// macOS qualifies the name by account label as well. Windows has no source for
/// one, so two scopes of a client still render alike here. <see cref="Window"/>
/// does not change that — a second Claude account's <c>session.v1</c> reads
/// "Session" beside the first account's "Session", where it used to read
/// "session.v1" beside "session.v1". The ambiguity is the multi-account gap and
/// closes with that feature, not here. They stay distinct in
/// <see cref="QuotaWindowIdentity"/>, which is the half that has to be right.
/// </para>
/// </summary>
public static class QuotaLabels
{
    public static string RowLabel(QuotaWindowSummary summary) =>
        Compose(summary.Id, summary.WindowLabel);

    public static string PickerLabel(QuotaHeatmapWindow window) =>
        Compose(window.Id, window.WindowLabel);

    /// <summary>
    /// What a window is called on screen: the live label when the join against
    /// the agent-usage payload found one, otherwise the store's own
    /// <paramref name="windowKey"/> rendered as words.
    /// <para>
    /// The fallback used to be the raw key, on the reasoning that
    /// <c>session.v1</c> is "meaningful and stable". It is — to someone reading
    /// the store. On screen it is an internal code, and a reader meets it in the
    /// ordinary case rather than an exotic one: every window card falls back
    /// here whenever the provider's live read is unavailable, which includes an
    /// expired login. Decided 2026-09-19 after exactly that: an expired Claude
    /// session left the card reading "session.v1 時間窗".
    /// </para>
    /// <para>
    /// A vocabulary rather than a lookup table of whole keys, because the keys
    /// compose and some are built at runtime — <c>main.weekly.v1</c>,
    /// <c>opus.weekly.v1</c>, and Codex's <c>additional.&lt;hash&gt;.primary.v1</c>
    /// are all real. Segments are translated one by one, so a key this
    /// vocabulary has never seen still reads as words instead of falling back to
    /// the whole raw key.
    /// </para>
    /// </summary>
    public static string Window(string? liveLabel, string windowKey) =>
        string.IsNullOrWhiteSpace(liveLabel)
            ? FromKey(windowKey)
            : liveLabel.Localized();

    /// <summary>
    /// A store window key as words: drop the trailing version and any opaque
    /// segment, translate what is left, join with <c>" · "</c>.
    /// <para>
    /// Public for the tests, and because <see cref="Window"/>'s callers pass a
    /// key they already hold; nothing else should reach for it directly.
    /// </para>
    /// </summary>
    public static string FromKey(string windowKey)
    {
        if (string.IsNullOrWhiteSpace(windowKey))
        {
            return string.Empty;
        }

        // Split on '|' as well as '.', because this does not always receive a
        // store key: `WindowCardText.Tabs` puts the live payload's CardId in
        // the identity's WindowKey slot for a window the store has nothing
        // under, and a CardId is `<client>|<window>`. No store key contains
        // '|' — that character is the identity separator — so admitting it
        // here cannot split a real key differently.
        var words = new List<string>();
        foreach (var segment in windowKey.Split(
            ['.', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsVersion(segment) || IsOpaque(segment))
            {
                continue;
            }

            words.Add(Word(segment));
        }

        // Every segment was dropped — a key that is nothing but a version, or
        // nothing but a hash. Better the raw key than an empty pill.
        return words.Count == 0 ? windowKey : string.Join(" · ", words);
    }

    /// <summary>The <c>v1</c> suffix every store key carries. It is a schema
    /// version, not a name, and it is on every key, so it distinguishes
    /// nothing.</summary>
    private static bool IsVersion(string segment) =>
        segment.Length > 1
        && (segment[0] is 'v' or 'V')
        && segment[1..].All(char.IsAsciiDigit);

    /// <summary>An account or tenant hash — Codex's
    /// <c>additional.&lt;64 hex&gt;.primary.v1</c> is the shape this exists for.
    /// Long and all-hex is deliberately narrow: it must not swallow a real word,
    /// and the only English words it could reach (<c>decade</c>, <c>defaced</c>)
    /// are nowhere near the length bound.</summary>
    private static bool IsOpaque(string segment) =>
        segment.Length >= 16 && segment.All(char.IsAsciiHexDigit);

    /// <summary>
    /// One segment as a word. Translated when the catalogue knows it; otherwise
    /// underscores become spaces and the first letter is capitalised, so an
    /// unknown segment still reads as text rather than as an identifier.
    /// <para>
    /// <see cref="Localization.Localized(string)"/> returns its input unchanged
    /// when the catalogue has no entry, so the two halves compose: the English
    /// word IS the translation key, exactly as everywhere else in this app.
    /// </para>
    /// </summary>
    private static string Word(string segment)
    {
        var spaced = segment.Replace('_', ' ');
        var titled = char.ToUpperInvariant(spaced[0]) + spaced[1..];
        return titled.Localized();
    }

    /// <summary>
    /// <c>"&lt;client&gt; · &lt;window&gt;"</c>.
    /// <para>
    /// The window label comes from a join against the live agent-usage payload
    /// that can miss, so it can be absent — and a missing half must not leave a
    /// dangling <c>" · "</c> on screen.
    /// </para>
    /// </summary>
    private static string Compose(QuotaWindowIdentity id, string? label)
    {
        var name = ClientRegistry.Style(id.ProviderId).DisplayName;
        var window = Window(label, id.WindowKey);
        return string.IsNullOrWhiteSpace(window) ? name : $"{name} · {window}";
    }
}
