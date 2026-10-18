namespace DashDeck.Core.Brightness;

/// <summary>What the person has chosen for the backlight.</summary>
/// <param name="Enabled">Whether DashDeck sets the backlight at all. Off leaves the tablet alone.</param>
/// <param name="DayPercent">The level by day, 0 to 100.</param>
/// <param name="NightPercent">The level by night, 0 to 100.</param>
public readonly record struct BrightnessChoice(bool Enabled, int DayPercent, int NightPercent)
{
    /// <summary>Full by day, because a dash too dim in sunlight is unreadable; low by night.</summary>
    public static BrightnessChoice Default => new(true, 100, 30);
}

/// <summary>
/// The level the backlight should be at (ADR-0068).
/// </summary>
/// <remarks>
/// Day or night is decided elsewhere — <c>ThemeService</c>, from sunrise and sunset today and
/// from the truck's headlights once that signal exists (F10) — so the palette and the backlight
/// can never disagree about which one it is. This only turns that answer into a number.
/// <para>
/// When the console dimmer is found it becomes the night level, here and nowhere else; the
/// person's own night level stays as the answer while the truck has not said.
/// </para>
/// </remarks>
public static class BrightnessPolicy
{
    /// <summary>
    /// The floor. A backlight at zero on a Surface is a black screen with a working touch layer,
    /// and a settings file is editable by hand — so a level that cannot be read is never offered.
    /// </summary>
    public const int MinimumPercent = 5;

    public const int MaximumPercent = 100;

    /// <summary>The levels offered by day. Nothing below 40: daylight in a cab washes out anything less.</summary>
    public static readonly IReadOnlyList<int> DayChoices = [40, 50, 60, 70, 80, 90, 100];

    /// <summary>The levels offered by night. Weighted low, because that is where the choice matters.</summary>
    public static readonly IReadOnlyList<int> NightChoices = [5, 10, 15, 20, 30, 40, 50, 60];

    /// <summary>Bring a stored or supplied level into the range the screen can be read at.</summary>
    public static int Clamp(int percent) => Math.Clamp(percent, MinimumPercent, MaximumPercent);

    /// <summary>
    /// The level wanted now, or null when DashDeck should not be setting it.
    /// </summary>
    public static int? Target(BrightnessChoice choice, bool isNight) =>
        choice.Enabled
            ? Clamp(isNight ? choice.NightPercent : choice.DayPercent)
            : null;
}
