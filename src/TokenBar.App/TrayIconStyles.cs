using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The tray icon styles, in picker order, with their labels. The one
/// list both pickers read — Settings → Tray icon and the setup card's
/// "Menu-bar icon" — as macOS reads its single <c>TrayAnimator.iconStyleOptions</c>.
/// Two copies could drift: a style added to one only would leave a user who
/// picked it with no option highlighted on the other.</summary>
internal static class TrayIconStyles
{
    internal static IReadOnlyList<(string Raw, string Label)> Options =>
    [
        ("cat", "Spinning cat".Localized()),
        ("parrot", "Party parrot".Localized()),
        (SandShoal.Style, SandShoal.Label),
        ("bars", "Signal bars".Localized()),
        ("ring", "Ring gauge".Localized()),
        ("popsicle", "Melting popsicle".Localized()),
    ];
}
