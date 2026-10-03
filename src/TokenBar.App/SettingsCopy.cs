namespace TokenBar.App;

/// <summary>Settings copy shared word for word with macOS: each string is the
/// English key of macOS <c>Localizable.strings</c>, so the two apps read the
/// same and the translations stay in step. A pure file so the test project can
/// pin the wording — SettingsWindow.cs compiles under no test project.</summary>
internal static class SettingsCopy
{
    // macOS SettingsPanel.swift:1188.
    internal const string About =
        "Syrtis started as a fork of tokcat by handlecusion. Log parsing and pricing come "
        + "from tokscale by Junho Yeo, the menu bar design draws on CodexBar by Peter "
        + "Steinberger, and the running cat comes from RunCat by Takuto Nakamura. MIT licensed.";

    // macOS SettingsPanel.swift:301.
    internal const string TextColorHint =
        "Custom colors follow remaining quota: normal above 25%, low at 25% or less, very "
        + "low at 10% or less. A stale reading always turns grey.";

    // macOS SettingsPanel.swift:321.
    internal const string GaugeColoringHint =
        "Gauge icons empty as quota is used. \"Color on warning only\" keeps one color "
        + "until 25% is left, then amber, and red at 10%.";

    // macOS SettingsPanel.swift:668-669.
    internal const string LiveTraceToggle = "Split by agent / model";
    internal const string LiveTraceHint =
        "On: the live session card gives each agent and model its own row. Off: one row per app.";

    // macOS SettingsPanel.swift:938. True here as well: TrayFeed's 300 s slow
    // timer requests an unforced read between the full ones.
    internal const string RefreshHint =
        "How often Syrtis rereads all logs in full. In between, new activity still shows up "
        + "within 5 minutes.";

    internal static IReadOnlyList<string> All =>
        [About, TextColorHint, GaugeColoringHint, LiveTraceToggle, LiveTraceHint, RefreshHint];
}
