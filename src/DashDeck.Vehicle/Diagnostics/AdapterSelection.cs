namespace DashDeck.Vehicle.Diagnostics;

/// <summary>
/// Turns a configured adapter port into a decision: real adapter, or simulator.
/// </summary>
/// <remarks>
/// A pure helper rather than logic inside the Host, for two reasons. It is shared by the
/// shell and the bring-up tool, and the Host is WPF and cannot be unit-tested on the
/// machines this project is developed on (ADR-0010) — so anything that decides behaviour
/// belongs below it.
/// </remarks>
public static class AdapterSelection
{
    /// <summary>
    /// Decide whether a configured value names a real port.
    /// </summary>
    /// <param name="configured">Whatever is in settings, or on the command line.</param>
    /// <param name="port">The usable port name, trimmed.</param>
    /// <returns>True to open <paramref name="port"/>; false to run the simulator.</returns>
    /// <remarks>
    /// Blank must mean "simulate" rather than "open a device named nothing": the setting is
    /// a free-text field, and a stray space pasted into it would otherwise take the dash
    /// down on a desk, which is where most of this app's life happens.
    /// </remarks>
    public static bool TryResolvePort(string? configured, out string port)
    {
        port = string.Empty;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        // Trimmed but NOT case-folded. Windows COM names are case-insensitive so folding
        // would be harmless there, but this layer is cross-platform and a POSIX device path
        // is case-sensitive — upper-casing /dev/ttyUSB0 turns a working port into a missing
        // one.
        port = configured.Trim();
        return true;
    }
}
