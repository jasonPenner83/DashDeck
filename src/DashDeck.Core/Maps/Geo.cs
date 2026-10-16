namespace DashDeck.Core.Maps;

/// <summary>
/// Distance and bearing between two points on the earth.
/// </summary>
/// <remarks>
/// Spherical, not ellipsoidal. A sphere is wrong about the earth by roughly a fifth of a percent
/// — forty metres in twenty kilometres — and this exists to answer "how far to the destination"
/// on a dash that writes that distance to a tenth of a kilometre. Vincenty's ellipsoidal formulae
/// would be more precise than the number is ever shown to, and would add an iteration that can
/// fail to converge for the privilege.
/// <para>
/// Every method returns NaN rather than throwing on a coordinate that is not a number, because
/// that is the dash's own convention for "nothing to show" and callers already handle it.
/// </para>
/// </remarks>
public static class Geo
{
    /// <summary>The IUGG mean earth radius, in metres.</summary>
    public const double EarthRadiusMetres = 6_371_008.8;

    /// <summary>Metres per degree of latitude, and of longitude at the equator.</summary>
    public const double MetresPerDegree = Math.PI * EarthRadiusMetres / 180;

    /// <summary>
    /// Great-circle distance between two points, in metres.
    /// </summary>
    /// <remarks>
    /// Haversine, which unlike the spherical law of cosines keeps its accuracy for the short
    /// hops that a route's own geometry is made of.
    /// </remarks>
    public static double DistanceMetres(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        if (!AreFinite(latitude1, longitude1, latitude2, longitude2))
        {
            return double.NaN;
        }

        var dLat = Radians(latitude2 - latitude1);
        var dLon = Radians(longitude2 - longitude1);
        var lat1 = Radians(latitude1);
        var lat2 = Radians(latitude2);

        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// The initial great-circle bearing from one point to another, degrees true, 0–360.
    /// </summary>
    /// <remarks>
    /// <b>Initial</b>, and it matters: a great circle's bearing changes along the way, so this is
    /// the direction to set off in rather than a constant heading to hold. Over the distances a
    /// truck drives, the difference is far below the degree the dash shows.
    /// </remarks>
    public static double BearingDegrees(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        if (!AreFinite(latitude1, longitude1, latitude2, longitude2))
        {
            return double.NaN;
        }

        var lat1 = Radians(latitude1);
        var lat2 = Radians(latitude2);
        var dLon = Radians(longitude2 - longitude1);

        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));

        var degrees = Math.Atan2(y, x) * 180 / Math.PI;

        return ((degrees % 360) + 360) % 360;
    }

    /// <summary>
    /// Distance between two nearby points, treating the ground as flat, in metres.
    /// </summary>
    /// <remarks>
    /// An equirectangular approximation, with longitude shrunk by the cosine of the mean
    /// latitude. Good to about a tenth of a percent over the few hundred metres between two
    /// vertices of a route, and three trig calls cheaper than haversine — which is what makes
    /// walking a long route's geometry four times a second free rather than noticeable.
    /// Not for use across a continent; <see cref="DistanceMetres"/> is there for that.
    /// </remarks>
    public static double FlatMetres(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        if (!AreFinite(latitude1, longitude1, latitude2, longitude2))
        {
            return double.NaN;
        }

        var scale = Math.Cos(Radians((latitude1 + latitude2) / 2));
        var y = (latitude2 - latitude1) * MetresPerDegree;
        var x = (longitude2 - longitude1) * MetresPerDegree * scale;

        return Math.Sqrt((x * x) + (y * y));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static bool AreFinite(double a, double b, double c, double d) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d);
}
