namespace TokenBar.Core;

/// <summary>Clients that have had local usage records in ANY graph this
/// install has accepted, persisted across launches. The graph is per year, so
/// its <c>Summary.Clients</c> covers only the selected year, while a window
/// card's local scan covers its quota history whatever the year: gating the
/// card on the year's clients made a client whose records all fall in another
/// year read "can't be attributed" (macOS cb77fa36, #462 review). Client ids
/// only; the set only grows, which errs toward scanning (a scan of a client
/// with no records shows zero rather than hiding real usage).</summary>
public static class LocalRecordClients
{
    /// <summary>Comma-separated ids, the same shape as the tab order/hidden
    /// keys (<see cref="ClientRegistry.ParseIdSet"/>).</summary>
    public const string Key = "tokenbar.localRecordClients";

    /// <summary>Merge one accepted graph's clients into the persisted set;
    /// writes only when something new appears.</summary>
    public static void Record(SettingsStore store, IEnumerable<string> clients)
    {
        var known = ClientRegistry.ParseIdSet(store.GetString(Key) ?? "");
        var union = new SortedSet<string>(known, StringComparer.Ordinal);
        foreach (var id in clients)
        {
            if (!string.IsNullOrWhiteSpace(id) && !id.Contains(','))
            {
                union.Add(id);
            }
        }

        if (union.Count != known.Count)
        {
            store.SetString(Key, string.Join(',', union));
        }
    }

    /// <summary>The year-independent "has local records" set the window card
    /// reads: the persisted union plus the current graph's clients.</summary>
    public static IReadOnlyCollection<string> Union(SettingsStore store, IEnumerable<string> present)
    {
        var union = new SortedSet<string>(ClientRegistry.ParseIdSet(store.GetString(Key) ?? ""), StringComparer.Ordinal);
        union.UnionWith(present);
        return union;
    }
}
