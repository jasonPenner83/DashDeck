using System.Management;
using System.Runtime.InteropServices;

namespace DashDeck.Host.Brightness;

/// <summary>
/// The built-in panel's backlight, through Windows' own WMI brightness classes (ADR-0068).
/// </summary>
/// <remarks>
/// <b>WMI because it is the only way Windows offers.</b> <c>SetMonitorBrightness</c> in dxva2
/// speaks DDC/CI, which external monitors answer and a laptop's own panel does not; the panel is
/// reached through <c>root\wmi</c>'s <c>WmiMonitorBrightness</c> and
/// <c>WmiMonitorBrightnessMethods</c>, the same classes the Action Center slider uses. They are
/// in-box: nothing is installed, no driver, no registry (constraint C1), and no elevation.
/// <para>
/// A screen without them — a desktop on an external monitor — has no instances, so
/// <see cref="Read"/> answers null and DashDeck says it cannot dim this one rather than pretend.
/// </para>
/// <para>
/// Every call is slow by UI standards — tens of milliseconds, and the first connection more —
/// so <see cref="ScreenBrightness"/> makes them on its own worker, never the dispatcher.
/// </para>
/// </remarks>
public sealed class WmiBacklight : IBacklight
{
    private const string Scope = @"root\wmi";

    /// <inheritdoc />
    public int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT Active, CurrentBrightness FROM WmiMonitorBrightness");
            using var monitors = searcher.Get();

            foreach (var monitor in monitors.OfType<ManagementObject>())
            {
                using (monitor)
                {
                    if (monitor["Active"] is true && monitor["CurrentBrightness"] is byte level)
                    {
                        return level;
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (Expected(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool Write(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            using var monitors = searcher.Get();

            foreach (var monitor in monitors.OfType<ManagementObject>())
            {
                using (monitor)
                {
                    if (monitor["Active"] is not true)
                    {
                        continue;
                    }

                    // Timeout, then level. The timeout is how long the setting may take to apply;
                    // zero is "now", which is what a dash changing at dusk wants.
                    monitor.InvokeMethod("WmiSetBrightness", [0u, (byte)Math.Clamp(percent, 0, 100)]);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (Expected(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// What WMI throws when the class is missing, access is refused or the service is unwell —
    /// all of which mean "this backlight is not ours to set", never a reason to stop the dash.
    /// </summary>
    private static bool Expected(Exception ex) =>
        ex is ManagementException or COMException or UnauthorizedAccessException or PlatformNotSupportedException;
}
