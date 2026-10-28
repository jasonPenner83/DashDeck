namespace DashDeck.Core.Screen;

/// <summary>Where the tablet's power is coming from, as Windows reports it.</summary>
public enum PowerLine
{
    /// <summary>Windows did not say.</summary>
    Unknown,

    /// <summary>On its battery.</summary>
    Battery,

    /// <summary>Plugged in: charging, or full and on the charger.</summary>
    Mains,
}

/// <summary>Whether the screen is kept on, and the line Settings ▸ DISPLAY shows for it.</summary>
/// <param name="KeepOn">True to ask Windows to keep the display on.</param>
/// <param name="Status">What is happening, for the person.</param>
public readonly record struct ScreenAwakeDecision(bool KeepOn, string Status);

/// <summary>
/// The screen stays on while DashDeck runs and the tablet is plugged in (ADR-0080).
/// </summary>
/// <remarks>
/// <para>
/// <b>Plugged in, not "charging".</b> A full battery on the charger has stopped charging, and the
/// screen going dark in the truck because the battery got to 100 % would be the bug this fixes.
/// </para>
/// <para>
/// <b>On battery, Windows decides.</b> Once the truck stops powering the charger, the
/// tablet's own sleep settings apply as they always did. So does an unknown power source.
/// </para>
/// <para>Pure: the dash reads the power line and makes the request.</para>
/// </remarks>
public static class ScreenAwake
{
    /// <summary>Decide.</summary>
    public static ScreenAwakeDecision Decide(PowerLine line) => line switch
    {
        PowerLine.Mains => new(true, "Kept on: the tablet is plugged in"),
        PowerLine.Battery => new(false, "On battery: the screen sleeps as Windows is set"),
        _ => new(false, "Windows did not say whether the tablet is plugged in: the screen sleeps as Windows is set"),
    };
}
