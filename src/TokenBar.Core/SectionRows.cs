namespace TokenBar.Core;

/// <summary>The decisions behind a Settings section that updates its rows in
/// place instead of rebuilding them (audit MA-43: a rebuild on every state
/// change dropped keyboard focus). The WinUI wiring that applies them lives in
/// SettingsWindow.ClaudeAccounts.cs and SettingsWindow.AntigravityAccounts.cs
/// and compiles under no test project.</summary>
public static class SectionRows
{
    /// <summary>One unique key per value, in order: a value seen again gets a
    /// <c>NUL + n</c> suffix (NUL cannot appear in a path) (a saved list can hold an exact duplicate, which the
    /// registry rejects but the page still shows), so each row keeps the same
    /// key across redraws.</summary>
    public static IReadOnlyList<string> Keys(IEnumerable<string> values)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var value in values)
        {
            seen.TryGetValue(value, out var n);
            seen[value] = n + 1;
            keys.Add(n == 0 ? value : $"{value}\0{n}");
        }

        return keys;
    }

    /// <summary>Where focus goes before the row <paramref name="focusedKey"/>
    /// is removed: <c>Move</c> is false when nothing is focused in a row or
    /// that row stays; otherwise the nearest surviving row after it, else
    /// before it, else <c>Key</c> null (the caller uses the section's own
    /// button). Focus must be handed on first because WinUI throws it out of
    /// the section when its holder leaves the tree.</summary>
    public static (bool Move, string? Key) FocusHandoff(
        IReadOnlyList<string> shown, IReadOnlyList<string> wanted, string? focusedKey)
    {
        var at = focusedKey is null ? -1 : shown.ToList().IndexOf(focusedKey);
        if (at < 0 || wanted.Contains(focusedKey!))
        {
            return (false, null);
        }

        for (var i = at + 1; i < shown.Count; i++)
        {
            if (wanted.Contains(shown[i]))
            {
                return (true, shown[i]);
            }
        }

        for (var i = at - 1; i >= 0; i--)
        {
            if (wanted.Contains(shown[i]))
            {
                return (true, shown[i]);
            }
        }

        return (true, null);
    }
}
