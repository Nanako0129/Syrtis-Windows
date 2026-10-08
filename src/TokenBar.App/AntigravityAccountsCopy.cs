namespace TokenBar.App;

/// <summary>
/// The Settings copy for captured Antigravity accounts (macOS SettingsPanel
/// antigravityAccountsSection, 945dbcc2, with Windows Credential Manager in
/// place of the login keychain). Each claim is checked against
/// <c>agent_antigravity.rs</c>'s captured-account section (what is read, where
/// the copy is kept, where it is sent, what Remove does); change them
/// together. Kept out of SettingsWindow so the error map and the translations
/// can be asserted.
/// </summary>
internal static class AntigravityAccountsCopy
{
    internal const string Section = "Antigravity accounts";

    internal const string Capture = "Capture current agy login";

    internal const string Capturing = "Capturing…";

    internal const string Remove = "Remove";

    /// <summary>The Remove button's accessible name: which account it removes.</summary>
    internal const string RemoveNamed = "Remove {0}";

    internal const string Toggle = "Capture accounts agy signs into automatically";

    internal const string Paused =
        "Automatic capture is paused because Credential Manager didn't answer. Press Capture to resume.";

    /// <summary>Shown only while automatic capture is off (macOS 2da830c9).
    /// "usually within an hour": measured on the Windows test machine
    /// (2026-10-04, 13 samples over 2 h): agy rewrote gemini:antigravity's
    /// LastWritten every hour at the same minute (18:43:47, 19:43:47,
    /// 20:43:47 UTC), plus one extra write about two minutes before each
    /// (19:41:25, 20:41:05). Text: shared Mac/Windows spec §6 (approved
    /// 2026-10-08) — plan E reads the primary through the captured sign-in
    /// only while automatic capture keeps the binding fresh.</summary>
    internal const string MergeOnlyUntilRefresh =
        "Turn this on to keep agy's current account on one card read through its captured sign-in (OAUTH), so Syrtis rarely needs to run agy. With it off, a Capture does this only until agy next refreshes its sign-in, usually within an hour; then the main card runs agy again.";

    internal const string WhenOn =
        "When on, Syrtis copies the sign-in of each account agy signs into to Windows Credential Manager: once when you turn this on, then each time agy rewrites its saved sign-in (including its routine refresh). Turning it off keeps the copies. An account you remove while this is on stays removed until you sign agy in to it and press Capture.";

    internal const string HowTo =
        "To add another Google account:\n1. Sign agy in to that account.\n2. Press Capture current agy login.\n3. Sign agy back in to your main account.";

    internal const string Reads =
        "Capture reads agy's saved Google sign-in once when you press the button, and, while automatic capture is on, again each time agy rewrites its saved sign-in. Windows does not ask before Syrtis reads it. The copy is kept in Windows Credential Manager for your user on this PC. Syrtis uses it only with Google's token service and Cloud Code quota service, and its requests identify as Antigravity.";

    internal const string RemoveMeans =
        "Remove deletes only the copy on this PC. Google still accepts it until you revoke access in Google Account → third-party access, which also signs Antigravity out of that account.";

    internal const string TwiceFromIde =
        "If Antigravity's own sign-in (not agy) supplies the main card, the same account can appear twice.";

    internal const string ConfirmTitle = "Turn on automatic capture?";

    internal const string ConfirmBody =
        "Syrtis captures agy's current account now, and each time agy rewrites its saved sign-in it copies that account to Windows Credential Manager without asking again. Accounts you remove while this is on stay removed until you sign agy in to them and press Capture.";

    internal const string ConfirmOk = "Turn on";

    internal const string ConfirmCancel = "Cancel";

    internal const string Generic = "Something went wrong. Try again.";

    /// <summary>A short sentence for a capture or remove failure. The core's
    /// error is a fixed code naming no account; it is mapped here and never
    /// shown raw (macOS <c>AntigravityAccounts.message(for:)</c>).</summary>
    internal static string Message(string? code) => code switch
    {
        "agy_not_signed_in" => "Couldn't read agy's login. Check that agy is signed in.",
        "agy_login_unreadable" => "agy's saved login is in a format Syrtis doesn't recognize.",
        "agy_login_missing_identity" =>
            "agy's saved login doesn't say which Google account it is. Sign agy in again, then try again.",
        "oauth_client_not_found" => "Couldn't match this login to the installed Antigravity or agy.",
        "oauth_client_rejected" or "refresh_rejected" =>
            "Google didn't accept this login. Sign agy in again, then try again.",
        "refresh_unreachable" => "Google couldn't be reached right now. Try again later.",
        "account_mismatch" => "Google answered for a different account. Nothing was saved.",
        "invalid_credential_format" => "The login had an unexpected format. Nothing was saved.",
        "keychain_write_failed" => "Couldn't save to Windows Credential Manager.",
        "keychain_delete_failed" => "Couldn't delete the copy from Windows Credential Manager.",
        _ => Generic,
    };

    /// <summary>Every error code ctb.h lists for <c>tb_antigravity_capture</c>
    /// and <c>tb_antigravity_remove</c> (<c>invalid_key</c> is never shown:
    /// Remove treats it as removed).</summary>
    internal static readonly string[] Codes =
    [
        "agy_not_signed_in", "agy_login_unreadable", "agy_login_missing_identity",
        "oauth_client_not_found", "oauth_client_rejected", "refresh_rejected",
        "refresh_unreachable", "account_mismatch", "invalid_credential_format",
        "keychain_write_failed", "keychain_delete_failed",
    ];

    internal static IEnumerable<string> All() =>
        [Section, "Antigravity account", "Antigravity account {0}", Capture, Capturing, Remove, RemoveNamed, Toggle, Paused, MergeOnlyUntilRefresh,
            WhenOn, HowTo, Reads, RemoveMeans, TwiceFromIde, ConfirmTitle, ConfirmBody, ConfirmOk,
            ConfirmCancel, Generic, .. Codes.Select(Message)];
}
