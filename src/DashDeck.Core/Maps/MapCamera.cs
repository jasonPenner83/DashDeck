using System.Globalization;
using DashDeck.Core.Location;

namespace DashDeck.Core.Maps;

/// <summary>
/// Where the map is looking: centre, zoom and which way is up.
/// </summary>
/// <remarks>
/// A value, and in Core, for one reason that is not tidiness: the map is a WebView2, and moving
/// it between the three placements rebuilds it (Q43). A rebuilt map reloads its page, so without
/// something outside the page holding the camera, every resize would throw you back to wherever
/// the map first opened. This is that something.
/// <para>
/// Clamping lives here too, because the values arrive from JavaScript across a string bridge and
/// a <c>NaN</c> handed to MapLibre is a blank canvas with no error — the worst kind of failure
/// for a dash, since it looks like a map that is still loading.
/// </para>
/// </remarks>
public readonly record struct MapCamera(double Longitude, double Latitude, double Zoom, double Bearing)
{
    /// <summary>Furthest out worth drawing. Below this the tiles say nothing useful at speed.</summary>
    public const double MinZoom = 2;

    /// <summary>
    /// Closest in. The tiles themselves stop at 14 and MapLibre overzooms past that, which stays
    /// sharp because they are vector — so this is a legibility limit, not a data one.
    /// </summary>
    public const double MaxZoom = 19;

    /// <summary>
    /// The latitude Web Mercator can draw. Beyond it the projection runs to infinity.
    /// </summary>
    public const double MaxLatitude = 85.051129;

    /// <summary>Zoom for following a vehicle: close enough to read the next turn.</summary>
    public const double DrivingZoom = 15;

    /// <summary>The camera centred on a fix, at the driving zoom, north up.</summary>
    /// <remarks>
    /// North up rather than course up. Rotating the map to the heading is what a phone does and
    /// it is a decision of its own — it needs the heading to be steady, which below about 5 km/h
    /// it is not (ADR-0027). The marker carries the heading instead.
    /// </remarks>
    public static MapCamera Over(LocationFix fix, double zoom = DrivingZoom) =>
        new MapCamera(fix.Longitude, fix.Latitude, zoom, 0).Clamped();

    /// <summary>
    /// True when every number is finite. A camera that fails this is never sent to the page.
    /// </summary>
    public bool IsUsable =>
        double.IsFinite(Longitude) && double.IsFinite(Latitude)
        && double.IsFinite(Zoom) && double.IsFinite(Bearing);

    /// <summary>The same camera with every value inside what the projection can draw.</summary>
    public MapCamera Clamped() => new(
        WrapLongitude(Longitude),
        Math.Clamp(Latitude, -MaxLatitude, MaxLatitude),
        Math.Clamp(Zoom, MinZoom, MaxZoom),
        WrapBearing(Bearing));

    /// <summary>
    /// The four numbers, in the order the page's bridge sends and expects them.
    /// </summary>
    /// <remarks>
    /// Invariant culture, explicitly: the dash runs in a locale where the decimal separator is a
    /// comma, and <c>49,8951</c> in a comma-separated list is two numbers.
    /// </remarks>
    public string ToWire() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Longitude:0.######},{Latitude:0.######},{Zoom:0.##},{Bearing:0.#}");

    /// <summary>Read a camera the page sent back. False for anything malformed.</summary>
    public static bool TryParse(string? wire, out MapCamera camera)
    {
        camera = default;

        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var parts = wire.Split(',');

        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var bearing))
        {
            return false;
        }

        var read = new MapCamera(lon, lat, zoom, bearing);

        if (!read.IsUsable)
        {
            return false;
        }

        camera = read.Clamped();
        return true;
    }

    /// <summary>Where the map is, for <c>--shot</c> and the trail.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Latitude:0.0000}, {Longitude:0.0000} @ z{Zoom:0.#}");

    /// <summary>
    /// Longitude into -180..180.
    /// </summary>
    /// <remarks>
    /// It wraps rather than clamps: a map dragged east past the date line should keep going, where
    /// clamping would stick it against an invisible wall at 180.
    /// </remarks>
    private static double WrapLongitude(double value) =>
        double.IsFinite(value) ? Positive(value + 180, 360) - 180 : 0;

    /// <summary>Bearing into 0..360, where north is 0 and the numbers only ever go up.</summary>
    private static double WrapBearing(double value) =>
        double.IsFinite(value) ? Positive(value, 360) : 0;

    /// <summary><paramref name="value"/> modulo <paramref name="period"/>, never negative.</summary>
    private static double Positive(double value, double period)
    {
        var shifted = value % period;
        return shifted < 0 ? shifted + period : shifted;
    }
}
