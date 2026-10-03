using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// Keyboard navigation over the dashboard's tab row, ported from macOS
/// PopoverView.handleKeyDown (PopoverView.swift:948-985) and handleFlagsChanged
/// (:994-1010). Pure so the target rules are testable: DashboardView is WinUI
/// and compiles under no test project.
///
/// The tab list is [Overview] + the visible client tabs in the row's display
/// order — exactly what the row shows, never the lenses (the earlier Windows
/// binding, which macOS does not share).
/// </summary>
public static class TabShortcuts
{
    /// <summary>The 400 ms Ctrl-alone delay before number hints appear
    /// (PopoverView.swift:1006 <c>Task.sleep(for: .milliseconds(400))</c>).</summary>
    public const int HintDelayMs = 400;

    /// <summary>Hints are drawn for indexes 1..9 only (DashboardTabs.swift:
    /// <c>index &lt;= 9</c>), because Ctrl+N stops at 9.</summary>
    public const int MaxNumbered = 9;

    public static List<string> Tabs(IReadOnlyList<string> displayClients) =>
        [ClientRegistry.OverviewTab, .. displayClients];

    /// <summary>Ctrl+N: the Nth tab (1-based), or null when N is past the end
    /// (macOS <c>guard index &lt; tabs.count else { return true }</c> — the key
    /// is consumed and nothing happens).</summary>
    public static string? Target(IReadOnlyList<string> tabs, int number) =>
        number >= 1 && number <= tabs.Count ? tabs[number - 1] : null;

    /// <summary>Ctrl+] (step +1) and Ctrl+[ (step -1), wrapping at both ends.
    /// An active id not in the list counts as the first tab, as macOS's
    /// <c>firstIndex(of:) ?? 0</c>.</summary>
    public static string Step(IReadOnlyList<string> tabs, string active, int step)
    {
        var current = Math.Max(0, IndexOf(tabs, active));
        var forward = step > 0 ? 1 : tabs.Count - 1;
        return tabs[(current + forward) % tabs.Count];
    }

    private static int IndexOf(IReadOnlyList<string> tabs, string id)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>What the caller must do with its timer after a modifier change.</summary>
public enum HintStep
{
    None,
    StartTimer,
    CancelAndHide,
}

/// <summary>
/// The hint-visibility state machine (macOS cmdHintTask/cmdHeld). Time-free:
/// the caller owns a 400 ms timer, starts it on <see cref="HintStep.StartTimer"/>,
/// calls <see cref="Fire"/> when it elapses and stops it on
/// <see cref="HintStep.CancelAndHide"/>.
/// </summary>
public sealed class CtrlHintGate
{
    public bool Pending { get; private set; }

    public bool Visible { get; private set; }

    /// <summary>Report the current modifier state. <paramref name="ctrlAlone"/>
    /// is true only when Ctrl is down and Shift, Alt and Win are all up
    /// (macOS <c>mods == .command</c>).</summary>
    public HintStep Update(bool ctrlAlone)
    {
        if (ctrlAlone)
        {
            if (Pending || Visible)
            {
                return HintStep.None; // key-repeat of Ctrl, or already showing
            }

            Pending = true;
            return HintStep.StartTimer;
        }

        Pending = false;
        Visible = false;
        return HintStep.CancelAndHide;
    }

    /// <summary>The 400 ms timer elapsed. Ignored unless still pending, so a
    /// tick that raced a release cannot show hints.</summary>
    public void Fire()
    {
        if (Pending)
        {
            Pending = false;
            Visible = true;
        }
    }
}
