using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TokenBar.App;

/// <summary>
/// Windows may power-throttle a background process (system-managed
/// EcoQoS). The boost is scoped: heavy FFI work opts the process out of
/// throttling, and the last scope to close hands the decision back to Windows
/// so the idle tray process stays power-friendly. (The cold-parse slowdown
/// this was written for was measured while H.NotifyIcon's ForceCreate default
/// had put the process in Efficiency Mode, now turned off in TrayService;
/// how much system-managed throttling alone costs has not been measured.)
/// </summary>
internal static class ProcessPower
{
    // The depth transition and the SetProcessInformation call must be one
    // atomic step: with bare Interlocked counters, a disposer preempted
    // between "decrement to 0" and "throttle on" can re-throttle the process
    // after a new scope already boosted it — the parse then runs slow with
    // no trace. The syscall is microseconds, so a lock is fine.
    private static readonly object Gate = new();
    private static int _boostDepth;

    /// <summary>Startup fixes, once per process: log the inherited power
    /// state, and lift a BELOW_NORMAL/IDLE priority class back to NORMAL —
    /// schtasks-launched processes can inherit a background priority that
    /// starves the parse behind every foreground app.</summary>
    public static void EnsureNormalPriority()
    {
        try
        {
            var throttle = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            };
            var queried = GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref throttle, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

            using var process = Process.GetCurrentProcess();
            DevLog.Write($"power: priority={process.PriorityClass} throttling " +
                (queried ? $"control=0x{throttle.ControlMask:X} state=0x{throttle.StateMask:X}" : "query failed"));

            if (process.PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
            {
                process.PriorityClass = ProcessPriorityClass.Normal;
                DevLog.Write("power: raised priority to normal");
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"power: startup check failed: {ex.Message}");
        }
    }

    /// <summary>Opt out of EcoQoS for the duration of the returned scope.
    /// Scopes nest (the lanes overlap); the last one out restores
    /// system-managed throttling.</summary>
    public static IDisposable Boost()
    {
        lock (Gate)
        {
            if (++_boostDepth == 1)
            {
                SetThrottling(optOut: true);
            }
        }

        return new BoostScope();
    }

    private sealed class BoostScope : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                if (--_boostDepth == 0)
                {
                    SetThrottling(optOut: false);
                }
            }
        }
    }

    private static void SetThrottling(bool optOut)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            // EXECUTION_SPEED in ControlMask with StateMask 0 = never throttle;
            // ControlMask 0 = system-managed again (Windows may re-apply EcoQoS).
            ControlMask = optOut ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = 0,
        };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            DevLog.Write($"power: SetProcessInformation optOut={optOut} " +
                $"failed, err={Marshal.GetLastWin32Error()}");
        }
    }

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        nint process, int informationClass,
        ref PROCESS_POWER_THROTTLING_STATE information, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(
        nint process, int informationClass,
        ref PROCESS_POWER_THROTTLING_STATE information, uint size);
}
