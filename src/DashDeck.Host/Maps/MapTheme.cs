using DashDeck.Core.Maps;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Maps;

/// <summary>
/// The theme's map colours, as the map wants them (ADR-0064).
/// </summary>
/// <remarks>
/// The whole of the bridge between the theme layer and the map: ten tokens off a resolved theme,
/// as plain hex. Everything on either side of it is already testable — the tokens and their night
/// rules in the theme suite, the style they build in the engine's — so this stays a lookup and
/// nothing more.
/// </remarks>
public static class MapTheme
{
    /// <summary>The map palette a resolved theme describes.</summary>
    public static MapPalette From(ResolvedTheme theme) => new(
        Land: Hex(theme, "mapLand"),
        Water: Hex(theme, "mapWater"),
        Green: Hex(theme, "mapGreen"),
        Building: Hex(theme, "mapBuilding"),
        Road: Hex(theme, "mapRoad"),
        RoadMajor: Hex(theme, "mapRoadMajor"),
        RoadCasing: Hex(theme, "mapRoadCasing"),
        Label: Hex(theme, "mapLabel"),
        LabelHalo: Hex(theme, "mapLabelHalo"),
        Marker: Hex(theme, "mapMarker"));

    /// <summary>
    /// One token as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Opaque, deliberately. A theme may write a colour with alpha, and MapLibre would honour it —
    /// but a semi-transparent road over a semi-transparent landcover over the background is how a
    /// map turns to mud, and the one thing a dash map has to be is legible at a glance.
    /// </remarks>
    private static string Hex(ResolvedTheme theme, string key)
    {
        var colour = theme.Colour(key);
        return new ThemeColour(colour.R, colour.G, colour.B).ToString();
    }
}
