using System.Globalization;

namespace DashDeck.Core.Maps;

/// <summary>
/// Whether a destination may be typed right now (ADR-0058's distraction rule, ADR-0065).
/// </summary>
/// <remarks>
/// ADR-0058 settled this before any of it was built: "no destination entry above a speed
/// threshold — the same posture the warning popups took in ADR-0055". Typing an address needs both
/// hands' worth of attention, and a dash that lets you do it at 90 km/h is a dash that helped.
/// <para>
/// So it is a gate rather than a warning. The search opens when the truck is stopped and refuses
/// when it is not, and says which it is.
/// </para>
/// </remarks>
public static class SearchGate
{
    /// <summary>
    /// The speed above which a destination cannot be typed, in km/h.
    /// </summary>
    /// <remarks>
    /// Five, which is the number the dash already uses for "moving enough to trust a heading"
    /// (ADR-0027) — one threshold rather than two that nearly agree. It is above GPS noise at a
    /// standstill and below walking pace, so a truck held on a hill does not lock you out.
    /// </remarks>
    public const double MaxSpeedKmh = 5;

    /// <summary>
    /// Whether the keyboard may be opened.
    /// </summary>
    /// <remarks>
    /// <b>An unknown speed is allowed</b>, and that is a deliberate hole with a known shape. No
    /// position means no map, so the search is unreachable anyway; what is left is a fix that went
    /// stale while moving, which carries no speed and would be treated as stopped. The honest
    /// alternative — refusing to search whenever the speed is unknown — would lock out every desk
    /// and every parked truck with the phone off, which is where this will mostly be used.
    /// </remarks>
    /// <param name="speedKmh">How fast the truck is going, or null when nothing says.</param>
    /// <param name="whileMoving">
    /// The owner's switch in Settings (ADR-0072): true lets a destination be typed at any speed.
    /// Off unless turned on, so the gate is what a new dash does.
    /// </param>
    public static bool MayType(double? speedKmh, bool whileMoving = false) =>
        whileMoving || speedKmh is not { } speed || !double.IsFinite(speed) || speed <= MaxSpeedKmh;

    /// <summary>Why not, in the words the dash will say, or null when it may.</summary>
    public static string? WhyNot(double? speedKmh, bool whileMoving = false) => MayType(speedKmh, whileMoving)
        ? null
        : string.Create(
            CultureInfo.InvariantCulture,
            $"Stop first — a destination cannot be typed at {speedKmh!.Value:0} km/h. Settings ▸ Display ▸ TYPE WHILE MOVING lifts this.");
}
