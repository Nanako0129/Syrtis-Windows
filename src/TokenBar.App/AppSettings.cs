using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Process-wide settings store at %APPDATA%\TokenBar\settings.json
/// (the plan's unpackaged-app stand-in for UserDefaults; keys stay
/// `tokenbar.*` for macOS parity).</summary>
internal static class AppSettings
{
    public static SettingsStore Store { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TokenBar", "settings.json"));

    /// <summary>The one composer that owns both credential-read answers
    /// (Grok Bot and Cursor), so each installs one composed core payload
    /// (C3).</summary>
    public static KeychainConsentComposer KeychainConsent { get; } =
        new(Store, KeychainConsentComposer.NativeSetter);

    /// <summary>The Grok Bot consent answer over <see cref="Store"/>, wired to
    /// the core's registry through the shared composer.</summary>
    public static GrokBotConsent GrokBotConsent { get; } = new(Store, KeychainConsent);
}
