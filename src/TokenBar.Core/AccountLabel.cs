using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// The one place a quota card's account becomes words (macOS
/// <c>AccountIdentity.accountLabel</c>). Every surface that names a card —
/// menus, pickers, section headers, summary lines, chart labels — calls this,
/// so they cannot disagree.
/// </summary>
public static class AccountLabel
{
    /// <summary>The core's fixed key for Claude Desktop. Not an absolute path,
    /// so it cannot collide with a configured directory.</summary>
    public const string ClaudeDesktopKey = "claude-desktop";

    /// <summary>Primary: the client's existing name (<paramref name="full"/>
    /// picks the display name over the short one, as each caller always did),
    /// so a single-account label is unchanged. Desktop: "Claude Desktop".
    /// Config dir: "Claude · &lt;directory name&gt;". When
    /// <paramref name="payload"/> holds another config-dir card of the same
    /// client whose directory name is the same (case-insensitive), both widen
    /// to "&lt;parent&gt;\&lt;name&gt;", further up only while still equal.
    /// Without a payload (the summary lines carry none) the bare directory
    /// name is used.</summary>
    public static string Of(AccountIdentity account, AgentUsagePayload? payload = null, bool full = false)
    {
        if (account.AccountKey is null)
        {
            return full
                ? ClientRegistry.Style(account.ClientId).DisplayName
                : ClientRegistry.ShortName(account.ClientId);
        }

        if (account.AccountKey == ClaudeDesktopKey)
        {
            return "Claude Desktop";
        }

        if (account.ClientId == AntigravityClientId)
        {
            // Never the key: it is derived from the Google account id. A key
            // the registry no longer holds (removed while an older payload is
            // still on screen) gets the generic label (macOS accountLabel).
            return AntigravityLabel(account.AccountKey) is { } label
                ? $"{ClientRegistry.ShortName(account.ClientId)} · {label}"
                : "Antigravity account".Localized();
        }

        var others = (payload?.Agents ?? [])
            .Select(a => a.Account)
            .Where(a => a.ClientId == account.ClientId && a.AccountKey is not null
                && a.AccountKey != ClaudeDesktopKey && a.AccountKey != account.AccountKey)
            .Select(a => Parts(a.AccountKey!))
            .ToList();
        var mine = Parts(account.AccountKey);
        var depth = 1;
        while (depth < mine.Length
            && others.Any(o => SameTail(mine, o, depth)))
        {
            depth++;
        }

        return $"{ClientRegistry.ShortName(account.ClientId)} · {string.Join('\\', mine[^depth..])}";
    }

    public static string Of(AgentUsageSnapshot agent, AgentUsagePayload? payload = null, bool full = false) =>
        Of(agent.Account, payload, full);

    /// <summary>The full path for a config-dir account's tooltip; null for
    /// the primary, for Desktop and for a captured Antigravity account
    /// (nothing more to say, and its key must not be shown).</summary>
    public static string? Detail(AccountIdentity account) =>
        account.AccountKey is { } key && key != ClaudeDesktopKey && account.ClientId != AntigravityClientId
            ? key
            : null;

    public const string AntigravityClientId = "antigravity";

    /// <summary>A captured Antigravity account's label (its email) by key,
    /// from the app's account list; null when the key is not listed. Set once
    /// at launch (<c>AntigravityAccounts.Label</c>).</summary>
    public static Func<string, string?> AntigravityLabel { get; set; } = _ => null;

    /// <summary>A captured Antigravity account's 1-based position in the
    /// app's account list, by key; null when the key is not listed. Set once
    /// at launch beside <see cref="AntigravityLabel"/>.</summary>
    public static Func<string, int?> AntigravityOrdinal { get; set; } = _ => null;

    /// <summary>The label for surfaces a passer-by can read (the tray
    /// tooltip): <see cref="Of"/> except that a captured Antigravity account
    /// is "Antigravity account {n}" (its list position) instead of its email,
    /// and an unlisted one the generic "Antigravity account". Never the email
    /// or the key.</summary>
    public static string OfPublic(AccountIdentity account, AgentUsagePayload? payload = null) =>
        account.AccountKey is { } key && key != ClaudeDesktopKey && account.ClientId == AntigravityClientId
            ? AntigravityOrdinal(key) is { } n
                ? "Antigravity account {0}".Localized(n)
                : "Antigravity account".Localized()
            : Of(account, payload);

    // Either separator: the key is a Windows path, but this must not depend
    // on the OS the tests run on. A key that is all separators keeps itself.
    private static string[] Parts(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? [path] : parts;
    }

    private static bool SameTail(string[] a, string[] b, int depth) =>
        b.Length >= depth
        && a[^depth..].SequenceEqual(b[^depth..], StringComparer.OrdinalIgnoreCase);
}
