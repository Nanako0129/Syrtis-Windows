namespace TokenBar.App;

/// <summary>
/// The Settings copy for extra Claude accounts. Every claim is checked against
/// <c>agent_usage.rs</c> (credential read, the three api.anthropic.com
/// requests, the header probe's model, prompt, token cap and 300 s cache,
/// <c>claude_account_requests</c>' primary profile lookup) and against the
/// registries' path rule; change them together. Kept out of SettingsWindow.cs,
/// which no test project compiles, so the reason mapping and the translations
/// can be asserted.
/// </summary>
internal static class ClaudeAccountsCopy
{
    internal const string Section = "Claude accounts";

    internal const string Add = "Add config dir…";

    internal const string Remove = "Remove";

    internal const string Missing =
        "Folder not found. Kept in case the drive is disconnected or the path needs fixing.";

    internal const string HowTo =
        "For another Claude account, run Claude Code with CLAUDE_CONFIG_DIR set to its own folder and add that folder here. Each folder gets its own quota card, and its usage joins your totals.";

    internal const string Reads =
        "For each folder, Syrtis reads the sign-in in .credentials.json and the transcripts under projects and transcripts. It never refreshes or rewrites that sign-in and keeps no copy of the token, only a one-way fingerprint to tell accounts apart. The folder path is saved in Syrtis's settings file.";

    internal const string Transport =
        "The token is sent only to api.anthropic.com, in requests that identify as Claude Code, for that account's usage and profile. If the sign-in can't read its usage, Syrtis sends one real request to Claude Haiku instead (a fixed one-word prompt, 1 output token, counted against that account), usually no more than once every 5 minutes. With any folder added, your main account's profile is also looked up.";

    internal const string Locations =
        "A folder on a network drive is read over the network on every refresh. Network paths (\\\\server\\share) can't be added, and folders inside WSL can't be added yet.";

    internal const string RemoveMeans =
        "Remove only stops Syrtis reading the folder. The folder and its sign-in stay as they are, nothing is revoked at Anthropic, and the card's history is kept: adding the same folder again continues it.";

    /// <summary>One sentence per fixed reason code from the C# UI rules
    /// (<c>ClaudeExtraRoots.UiRejection</c>) and the native registries
    /// (<c>ctb.h</c>); an unknown code gets the generic sentence.</summary>
    internal static string Reason(string code) => code switch
    {
        "homeDirectory" => "That's your user folder. Pick the Claude config folder inside it.",
        "defaultConfigDir" => "That folder overlaps your main Claude account's folder, which is already shown.",
        "duplicate" => "Already added.",
        "unsupportedPath" => "Only folders on a drive letter can be added, not network or WSL paths.",
        "rootDirectory" => "A drive's root can't be added.",
        "invalidComponent" => "Windows can't open this path as written.",
        "limitExceeded" => "Up to 8 folders can be added.",
        "notDirectory" => "This isn't a folder.",
        _ => "This folder can't be added.",
    };

    internal static readonly string[] ReasonCodes =
    [
        "homeDirectory", "defaultConfigDir", "duplicate", "unsupportedPath", "rootDirectory",
        "invalidComponent", "limitExceeded", "notDirectory", "empty",
    ];

    internal static IEnumerable<string> All() =>
        [Section, Add, Remove, Missing, HowTo, Reads, Transport, Locations, RemoveMeans,
            .. ReasonCodes.Select(Reason)];
}
