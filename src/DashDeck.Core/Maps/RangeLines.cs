using System.Globalization;
using System.Text.Json;

namespace DashDeck.Core.Maps;

/// <summary>Which of the two range lines.</summary>
public enum RangeLevel
{
    /// <summary>Where the truck would have <see cref="RangeLines.ReserveKm"/> left. Orange.</summary>
    Low,

    /// <summary>Where the truck would run dry. Red, and everything past it is red.</summary>
    Empty,
}

/// <summary>A line on the map at one distance, and how it is labelled.</summary>
/// <param name="Level">Which line.</param>
/// <param name="RadiusKm">How far from the truck.</param>
/// <param name="Line">The ring itself, closed, as longitude/latitude pairs.</param>
public sealed record RangeRing(RangeLevel Level, double RadiusKm, IReadOnlyList<(double Longitude, double Latitude)> Line);

/// <summary>A route, cut where the fuel runs low and where it runs out.</summary>
/// <param name="Low">The stretch driven on the last <see cref="RangeLines.ReserveKm"/>. Empty when none of it is.</param>
/// <param name="Beyond">The stretch past empty. Empty when the tank reaches the end.</param>
public sealed record RouteRange(
    IReadOnlyList<(double Longitude, double Latitude)> Low,
    IReadOnlyList<(double Longitude, double Latitude)> Beyond)
{
    /// <summary>True when the whole route is inside the range, with more than the reserve to spare.</summary>
    public bool IsClear => Low.Count == 0 && Beyond.Count == 0;
}

/// <summary>
/// How far the fuel in the tank reaches, drawn on the map (ADR-0071).
/// </summary>
/// <remarks>
/// <b>Two lines from the truck's distance to empty.</b> Orange where it would have
/// <see cref="ReserveKm"/> left, red where it would have none. As rings around the truck, and —
/// once there is a route — along the route itself, which is the honest one of the two.
/// <para>
/// <b>A ring is a straight line, and roads are not.</b> A ring says what is out of reach for
/// certain — nothing past it can be driven to — and flatters everything inside it, because the
/// road there is longer than the crow's flight. The route says exactly where on the drive the
/// fuel runs low and out. Both are drawn because each answers a question the other cannot: the
/// ring before you have chosen where to go, the route once you have.
/// </para>
/// <para>
/// Pure and in Core, so the arithmetic is tested on any machine; the page only draws the lines it
/// is handed.
/// </para>
/// </remarks>
public static class RangeLines
{
    /// <summary>The distance to empty at which fuel is running low: the orange line, in km.</summary>
    public const double ReserveKm = 80;

    /// <summary>How many points draw a ring. Smooth at any zoom a ring is worth looking at.</summary>
    public const int RingPoints = 120;

    /// <summary>
    /// The rings around a position, nearest first: the low line, if the range is past the
    /// reserve, then the empty line. None when the range is not a usable number.
    /// </summary>
    public static IReadOnlyList<RangeRing> Rings(double latitude, double longitude, double rangeKm)
    {
        if (!IsUsable(rangeKm) || !double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return [];
        }

        var rings = new List<RangeRing>(2);

        // Already inside the reserve, there is no "80 km left" still ahead to draw.
        if (rangeKm > ReserveKm)
        {
            rings.Add(new RangeRing(RangeLevel.Low, rangeKm - ReserveKm, Ring(latitude, longitude, rangeKm - ReserveKm)));
        }

        rings.Add(new RangeRing(RangeLevel.Empty, rangeKm, Ring(latitude, longitude, rangeKm)));
        return rings;
    }

    /// <summary>
    /// A route cut by the range, from where the truck is on it, or null when it cannot be: no
    /// range, no line, or a truck too far off the line for "how far along" to mean anything.
    /// </summary>
    /// <remarks>
    /// Off the route the cut is refused, for the same reason the countdown refuses (ADR-0066): it
    /// would measure from a point on the line the truck is not at.
    /// </remarks>
    public static RouteRange? AlongRoute(Route route, double latitude, double longitude, double rangeKm)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (!IsUsable(rangeKm)
            || RouteProgress.Locate(route, latitude, longitude) is not { } at
            || at.OffRouteMetres > RouteProgress.OffRouteLimitMetres)
        {
            return null;
        }

        var lowFrom = at.Travelled + (Math.Max(0, rangeKm - ReserveKm) * 1000);
        var emptyAt = at.Travelled + (rangeKm * 1000);

        return new RouteRange(
            Stretch(route.Shape, at.Lengths, lowFrom, Math.Min(emptyAt, at.Total)),
            Stretch(route.Shape, at.Lengths, emptyAt, at.Total));
    }

    /// <summary>
    /// The rings as GeoJSON, for the page: one line feature each, with <c>level</c> and
    /// <c>label</c> properties to colour and name it by.
    /// </summary>
    public static string RingsJson(IReadOnlyList<RangeRing> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);

        return JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = rings.Select(ring => new
            {
                type = "Feature",
                properties = new { level = Name(ring.Level), label = Label(ring) },
                geometry = new { type = "LineString", coordinates = Coordinates(ring.Line) },
            }),
        });
    }

    /// <summary>A cut route as GeoJSON, for the page: the low and the beyond stretches.</summary>
    public static string RouteJson(RouteRange? cut)
    {
        var features = new List<object>(2);

        if (cut is not null)
        {
            Add(RangeLevel.Low, cut.Low);
            Add(RangeLevel.Empty, cut.Beyond);
        }

        return JsonSerializer.Serialize(new { type = "FeatureCollection", features });

        void Add(RangeLevel level, IReadOnlyList<(double Longitude, double Latitude)> line)
        {
            if (line.Count > 1)
            {
                features.Add(new
                {
                    type = "Feature",
                    properties = new { level = Name(level) },
                    geometry = new { type = "LineString", coordinates = Coordinates(line) },
                });
            }
        }
    }

    /// <summary>What a ring says on itself: <c>80 KM LEFT</c>, or <c>EMPTY</c>.</summary>
    public static string Label(RangeRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);

        return ring.Level == RangeLevel.Low
            ? string.Create(CultureInfo.InvariantCulture, $"{ReserveKm:0} KM LEFT")
            : "EMPTY";
    }

    /// <summary>A range worth drawing: a number, above nought, and not past what any tank holds.</summary>
    public static bool IsUsable(double rangeKm) => double.IsFinite(rangeKm) && rangeKm > 0 && rangeKm <= 5000;

    private static string Name(RangeLevel level) => level == RangeLevel.Low ? "low" : "empty";

    private static IEnumerable<double[]> Coordinates(IReadOnlyList<(double Longitude, double Latitude)> line) =>
        line.Select(p => new[] { Math.Round(p.Longitude, 6), Math.Round(p.Latitude, 6) });

    private static List<(double Longitude, double Latitude)> Ring(double latitude, double longitude, double radiusKm)
    {
        var ring = new List<(double, double)>(RingPoints + 1);

        for (var i = 0; i <= RingPoints; i++)
        {
            // The last point is the first again, so the ring closes.
            var (lat, lon) = Geo.Destination(latitude, longitude, 360.0 * (i % RingPoints) / RingPoints, radiusKm * 1000);
            ring.Add((lon, lat));
        }

        return ring;
    }

    /// <summary>The part of a line between two distances along it, cut exactly at both ends.</summary>
    private static List<(double Longitude, double Latitude)> Stretch(
        IReadOnlyList<(double Longitude, double Latitude)> shape, double[] lengths, double from, double to)
    {
        var stretch = new List<(double, double)>();

        if (!(to > from))
        {
            return stretch;
        }

        var start = 0.0;

        for (var i = 0; i < lengths.Length; i++)
        {
            var end = start + lengths[i];

            if (end > from && start < to && lengths[i] > 0)
            {
                if (stretch.Count == 0)
                {
                    stretch.Add(Between(shape[i], shape[i + 1], (Math.Max(from, start) - start) / lengths[i]));
                }

                stretch.Add(end <= to ? shape[i + 1] : Between(shape[i], shape[i + 1], (to - start) / lengths[i]));
            }

            if (end >= to)
            {
                break;
            }

            start = end;
        }

        return stretch;
    }

    private static (double Longitude, double Latitude) Between(
        (double Longitude, double Latitude) a, (double Longitude, double Latitude) b, double t) =>
        (a.Longitude + ((b.Longitude - a.Longitude) * t), a.Latitude + ((b.Latitude - a.Latitude) * t));
}
