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
    /// Config dir: "Claude · &lt;directory name&gt;".</summary>
    public static string Of(AccountIdentity account, bool full = false)
    {
        if (account.AccountKey is null)
        {
            return full
                ? ClientRegistry.Style(account.ClientId).DisplayName
                : ClientRegistry.ShortName(account.ClientId);
        }

        return account.AccountKey == ClaudeDesktopKey
            ? "Claude Desktop"
            : $"{ClientRegistry.ShortName(account.ClientId)} · {Basename(account.AccountKey)}";
    }

    public static string Of(AgentUsageSnapshot agent, bool full = false) => Of(agent.Account, full);

    /// <summary>The full path for a config-dir account's tooltip; null for
    /// the primary and for Desktop (nothing more to say).</summary>
    public static string? Detail(AccountIdentity account) =>
        account.AccountKey is { } key && key != ClaudeDesktopKey ? key : null;

    // Either separator: the key is a Windows path, but this must not depend
    // on the OS the tests run on.
    private static string Basename(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length == 0 ? path : name;
    }
}
