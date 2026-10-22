using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DashDeck.Emulator;

/// <summary>
/// Keeping Windows from treating the emulator as background work (ADR-0075 addendum).
/// </summary>
/// <remarks>
/// The dash starts the emulator with its window hidden, and Windows 11 treats a process with no
/// window on screen as background: it may run it on the slowest, most efficient settings ("power
/// throttling", EcoQoS), and it ignores the timer resolution it asks for, so its timing coarsens to
/// sixteen milliseconds. Both cost an emulator drawing frames. A process's own starter may opt it
/// out, documented in <c>SetProcessInformation</c> — no driver, no registry, nothing left behind
/// when it ends.
/// </remarks>
public static class WindowsThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    /// <summary>
    /// Run the process at full speed with the timers it asks for, whatever Windows thinks of its
    /// window. False when it could not be — not Windows, gone, or not ours to change.
    /// </summary>
    public static bool KeepAtFullSpeed(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);

            // Controlled, and off: never throttled, and its timer resolution honoured.
            var state = new PowerThrottlingState
            {
                Version = CurrentVersion,
                ControlMask = ExecutionSpeed | IgnoreTimerResolution,
                StateMask = 0,
            };

            return SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
