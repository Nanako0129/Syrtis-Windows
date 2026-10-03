using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The flyout's lenses, ported from the macOS AppView router. macOS
/// order matches macOS, so the number-key positions line up with it.</summary>
public enum AppView
{
    Overview,
    // Second, as on macOS. Declaration order IS the lens tab order. The
    // numeric accelerators (Ctrl+1..9) no longer address lenses: as on macOS
    // they select tabs of the client row (see TabShortcuts).
    Quota,
    Models,
    Monthly,
    Daily,
    Hourly,
    Stats,
    Agents,
}

/// <summary>Hidden-lens policy for the flyout's tab row (macOS AppView's
/// toggleable/visible/effective trio). Ids are the lowercase enum names, the
/// same shape macOS persists, and the hidden set reuses
/// <see cref="ClientRegistry.ParseIdSet"/> — that parser is a generic CSV-id
/// reader, not client-specific in implementation.</summary>
public static class AppViews
{
    public const string HiddenKey = "tokenbar.views.hidden";

    /// <summary>Lenses the user may hide. Overview is the fallback target for
    /// every hidden lens (see <see cref="Effective"/>) so it can never be
    /// hidden itself; Models is the other fixed anchor, matching macOS.</summary>
    public static readonly IReadOnlyList<AppView> Toggleable =
        [.. Enum.GetValues<AppView>()
            .Where(v => v is not (AppView.Overview or AppView.Models))];

    public static string Id(AppView view) => view.ToString().ToLowerInvariant();

    /// <summary>Display name for a lens. The enum name is the translation key,
    /// matching what macOS persists and shows for the same seven lenses.</summary>
    public static string Label(AppView view) => view.ToString().Localized();

    /// <summary>Lenses to show in the tab row. Only <see cref="Toggleable"/>
    /// lenses can ever be dropped, so even a hand-edited settings file cannot
    /// remove Overview and leave the fallback target missing.</summary>
    public static List<AppView> Visible(string hiddenRaw)
    {
        var hidden = ClientRegistry.ParseIdSet(hiddenRaw);
        return [.. Enum.GetValues<AppView>()
            .Where(v => !Toggleable.Contains(v) || !hidden.Contains(Id(v)))];
    }

    /// <summary>The lens to actually render. A hidden lens never survives, so
    /// callers that carry a stale selection — the numeric accelerators bind
    /// one fixed lens each at construction — cannot land on a tab that is not
    /// in the row. Guarded to Toggleable for the same tamper-resistance reason
    /// as <see cref="Visible"/>.</summary>
    public static AppView Effective(AppView view, string hiddenRaw) =>
        !Toggleable.Contains(view)
            ? view
            : ClientRegistry.ParseIdSet(hiddenRaw).Contains(Id(view))
                ? AppView.Overview
                : view;

    public static List<AppView> Visible(SettingsStore store) =>
        Visible(store.GetString(HiddenKey) ?? string.Empty);

    public static AppView Effective(AppView view, SettingsStore store) =>
        Effective(view, store.GetString(HiddenKey) ?? string.Empty);
}
