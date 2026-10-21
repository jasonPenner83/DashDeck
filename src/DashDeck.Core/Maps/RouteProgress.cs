namespace DashDeck.Core.Maps;

/// <summary>
/// How much of a route is left, worked out from where the truck is now.
/// </summary>
/// <remarks>
/// <b>This is not turn-by-turn guidance.</b> It finds the nearest point of the route's own
/// geometry and adds up what comes after it — nothing more. It does not know which way the truck
/// is pointing, does not re-route, and will not announce a turn. ADR-0058's bundled engine is
/// still the thing that does all of that; this is the arithmetic that lets a card count down.
/// <para>
/// A position far enough off the line is refused rather than answered, because the number would
/// be confidently wrong in the one way that matters: a truck that has left the route reads a
/// cheerfully shrinking distance to a place it is driving away from.
/// </para>
/// </remarks>
/// <param name="RemainingMetres">Distance still to drive along the route's geometry.</param>
/// <param name="RemainingSeconds">The router's duration, pro-rated by the distance left.</param>
/// <param name="OffRouteMetres">How far the position is from the nearest point of the line.</param>
public readonly record struct RouteProgress(
    double RemainingMetres,
    double RemainingSeconds,
    double OffRouteMetres)
{
    /// <summary>
    /// How far off the line is too far, in metres.
    /// </summary>
    /// <remarks>
    /// A hundred and fifty metres: wide enough for a divided highway drawn down its centre line,
    /// a service road beside it, and the GPS's own error on a bad fix; narrow enough that a wrong
    /// turn is caught within a block or two.
    /// </remarks>
    public const double OffRouteLimitMetres = 150;

    /// <summary>True when the position is close enough to the line to believe the rest.</summary>
    public bool IsOnRoute => OffRouteMetres <= OffRouteLimitMetres;

    /// <summary>
    /// Work out what is left of <paramref name="route"/> from a position, or null when it cannot
    /// be worked out at all — no geometry, no position, or a line with no length.
    /// </summary>
    /// <remarks>
    /// Null and off-route are different answers and the caller should treat them differently.
    /// Null means the question could not be asked; off-route means it was asked and the answer
    /// is not to be trusted.
    /// <para>
    /// The nearest point is found by projecting onto each segment in turn. One pass, so the cost
    /// is the length of the geometry — a few hundred points for a town errand, and cheap enough
    /// at that size to do on every poll rather than cache and invalidate.
    /// </para>
    /// </remarks>
    public static RouteProgress? Along(Route route, double latitude, double longitude)
    {
        if (Locate(route, latitude, longitude) is not { } at)
        {
            return null;
        }

        var remaining = at.Total - at.Travelled;

        // The duration is the router's, shared out by how much line is left. It is not a fresh
        // estimate: this knows nothing about the traffic or the speed limits that produced it,
        // so stretching the number it was given is the only honest thing to do with it.
        var seconds = route.Seconds * (remaining / at.Total);

        return new RouteProgress(remaining, seconds, at.OffRouteMetres);
    }

    /// <summary>Where a position falls on a route's line.</summary>
    /// <param name="Segment">The segment the nearest point is on.</param>
    /// <param name="Along">How far along that segment, 0 to 1.</param>
    /// <param name="OffRouteMetres">How far the position is from that point.</param>
    /// <param name="Lengths">Every segment's length, in metres.</param>
    /// <param name="Total">The whole line's length.</param>
    /// <param name="Travelled">How much of the line comes before the nearest point.</param>
    internal readonly record struct Position(
        int Segment,
        double Along,
        double OffRouteMetres,
        double[] Lengths,
        double Total,
        double Travelled);

    /// <summary>
    /// The nearest point of the route's line to a position, or null when there is no line to find
    /// it on. Shared with the range lines (ADR-0071), which cut the same line where fuel runs low.
    /// </summary>
    internal static Position? Locate(Route route, double latitude, double longitude)
    {
        if (!route.HasShape || !double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return null;
        }

        var shape = route.Shape;

        // Each segment's length, and the total, in one pass. Flat arithmetic: a route vertex is
        // tens of metres from the next one, which is well inside where that holds.
        var lengths = new double[shape.Count - 1];
        var total = 0.0;

        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = Geo.FlatMetres(
                shape[i].Latitude, shape[i].Longitude,
                shape[i + 1].Latitude, shape[i + 1].Longitude);

            total += lengths[i];
        }

        if (!(total > 0))
        {
            return null;
        }

        // The nearest segment, and how far along it the nearest point lies.
        var nearest = double.PositiveInfinity;
        var segment = 0;
        var along = 0.0;

        var scale = Math.Cos(latitude * Math.PI / 180);

        for (var i = 0; i < lengths.Length; i++)
        {
            var (ax, ay) = Flat(shape[i].Latitude, shape[i].Longitude);
            var (bx, by) = Flat(shape[i + 1].Latitude, shape[i + 1].Longitude);

            var dx = bx - ax;
            var dy = by - ay;
            var square = (dx * dx) + (dy * dy);

            // A repeated vertex has no direction to project onto; the distance to it still counts.
            var t = square > 0 ? Math.Clamp(-((ax * dx) + (ay * dy)) / square, 0, 1) : 0;

            var px = ax + (t * dx);
            var py = ay + (t * dy);
            var distance = Math.Sqrt((px * px) + (py * py));

            if (distance < nearest)
            {
                nearest = distance;
                segment = i;
                along = t;
            }
        }

        var travelled = along * lengths[segment];

        for (var i = 0; i < segment; i++)
        {
            travelled += lengths[i];
        }

        return new Position(segment, along, nearest, lengths, total, travelled);

        // Metres from the position, east and north. The position is the origin, so the projected
        // point's own distance from the origin is the distance off the route.
        (double X, double Y) Flat(double pointLatitude, double pointLongitude) =>
            ((pointLongitude - longitude) * Geo.MetresPerDegree * scale,
             (pointLatitude - latitude) * Geo.MetresPerDegree);
    }
}
