using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// The arithmetic behind the navigation readouts (ADR-0066): how far, which way, and how much of
/// a route is left.
/// </summary>
/// <remarks>
/// All of it pure, which is the point of it living in Core. The dash cannot be run on this
/// machine, but a distance that is wrong by a factor of a thousand, a bearing that comes out
/// negative, and a remaining distance that grows as you drive are all catchable here.
/// </remarks>
public sealed class NavigationMathTests
{
    /// <summary>A degree of latitude, in metres, on the sphere this uses.</summary>
    private const double Degree = Geo.MetresPerDegree;

    // ---- Distance ----

    [Fact]
    public void A_degree_of_latitude_is_a_hundred_and_eleven_kilometres() =>
        Assert.Equal(Degree, Geo.DistanceMetres(0, 0, 1, 0), 1.0);

    /// <summary>
    /// The same degree of longitude is shorter the further north you go, and this is the reason
    /// a flat approximation has to scale it. At Winnipeg's latitude it is about two thirds.
    /// </summary>
    [Fact]
    public void A_degree_of_longitude_shrinks_towards_the_pole()
    {
        Assert.Equal(Degree, Geo.DistanceMetres(0, 0, 0, 1), 1.0);
        Assert.Equal(Degree * 0.5, Geo.DistanceMetres(60, 0, 60, 1), 1.0);
        // Within a metre of the cosine shortcut, which is the whole reason the flat
        // approximation below is allowed to use it.
        Assert.Equal(Degree * Math.Cos(49.9 * Math.PI / 180), Geo.DistanceMetres(49.9, 0, 49.9, 1), 1.0);
    }

    [Fact]
    public void The_distance_from_a_point_to_itself_is_zero() =>
        Assert.Equal(0, Geo.DistanceMetres(49.8951, -97.1384, 49.8951, -97.1384));

    [Fact]
    public void Distance_does_not_care_which_end_you_start_from() =>
        Assert.Equal(
            Geo.DistanceMetres(49.8951, -97.1384, 50.4452, -104.6189),
            Geo.DistanceMetres(50.4452, -104.6189, 49.8951, -97.1384),
            6);

    /// <summary>Winnipeg to Regina: about 536 km by air, against 571 by road.</summary>
    [Fact]
    public void A_known_pair_of_cities_comes_out_right()
    {
        var metres = Geo.DistanceMetres(49.8951, -97.1384, 50.4452, -104.6189);

        Assert.InRange(metres, 534_000, 538_000);
    }

    [Fact]
    public void A_coordinate_that_is_not_a_number_gives_no_distance() =>
        Assert.True(double.IsNaN(Geo.DistanceMetres(49.9, double.NaN, 50, -97)));

    // ---- Bearing ----

    [Theory]
    [InlineData(1, 0, 0)]      // due north
    [InlineData(0, 1, 90)]     // due east
    [InlineData(-1, 0, 180)]   // due south
    [InlineData(0, -1, 270)]   // due west
    public void The_four_cardinal_bearings(double dLat, double dLon, double expected) =>
        Assert.Equal(expected, Geo.BearingDegrees(0, 0, dLat, dLon), 6);

    [Fact]
    public void A_bearing_is_never_negative() =>
        Assert.InRange(Geo.BearingDegrees(49.9, -97.1, 49.8, -97.2), 0, 360);

    /// <summary>
    /// The bearing is the one to set off on, not one to hold: a great circle between two points
    /// on the same parallel leaves to the north of due east, because it bulges towards the pole.
    /// </summary>
    [Fact]
    public void An_eastward_leg_at_high_latitude_sets_off_north_of_east()
    {
        var bearing = Geo.BearingDegrees(60, 0, 60, 10);

        Assert.InRange(bearing, 80, 90);
    }

    [Fact]
    public void A_coordinate_that_is_not_a_number_gives_no_bearing() =>
        Assert.True(double.IsNaN(Geo.BearingDegrees(49.9, -97.1, double.PositiveInfinity, -97)));

    // ---- The flat approximation ----

    /// <summary>
    /// Why it is allowed: over the distance between two vertices of a route it agrees with the
    /// real thing to well under a metre, and that is the only thing it is used for.
    /// </summary>
    [Fact]
    public void Flat_arithmetic_agrees_with_the_sphere_over_a_route_segment()
    {
        var flat = Geo.FlatMetres(49.8951, -97.1384, 49.8960, -97.1370);
        var sphere = Geo.DistanceMetres(49.8951, -97.1384, 49.8960, -97.1370);

        Assert.Equal(sphere, flat, 0.1);
        Assert.InRange(flat, 100, 200);
    }

    [Fact]
    public void Flat_arithmetic_still_agrees_over_a_kilometre() =>
        Assert.Equal(
            Geo.DistanceMetres(49.89, -97.14, 49.899, -97.14),
            Geo.FlatMetres(49.89, -97.14, 49.899, -97.14),
            1.0);

    // ---- What is left of a route ----

    /// <summary>
    /// A straight line due north from a point near Winnipeg: eleven vertices a thousandth of a
    /// degree apart, so ten segments of about 111 m and a total of about 1112 m.
    /// </summary>
    private static Route Straight(double seconds = 120)
    {
        var shape = new List<(double Longitude, double Latitude)>();

        for (var i = 0; i <= 10; i++)
        {
            shape.Add((-97.14, 49.89 + (i * 0.001)));
        }

        return new Route(Degree * 0.01, seconds, shape, []);
    }

    [Fact]
    public void At_the_start_of_the_route_all_of_it_is_left()
    {
        var progress = RouteProgress.Along(Straight(), 49.89, -97.14);

        Assert.NotNull(progress);
        Assert.Equal(Degree * 0.01, progress.Value.RemainingMetres, 1.0);
        Assert.Equal(0, progress.Value.OffRouteMetres, 1.0);
        Assert.True(progress.Value.IsOnRoute);
    }

    [Fact]
    public void At_the_end_of_the_route_none_of_it_is_left()
    {
        var progress = RouteProgress.Along(Straight(), 49.90, -97.14);

        Assert.NotNull(progress);
        Assert.Equal(0, progress.Value.RemainingMetres, 1.0);
    }

    [Fact]
    public void Halfway_along_a_segment_counts_as_half_that_segment()
    {
        // Between the fifth and sixth vertex: five whole segments plus half of one.
        var progress = RouteProgress.Along(Straight(), 49.8955, -97.14);

        Assert.NotNull(progress);
        Assert.Equal(Degree * 0.0045, progress.Value.RemainingMetres, 1.0);
    }

    /// <summary>The distance left only ever shrinks as the truck moves along the line.</summary>
    [Fact]
    public void Driving_the_route_counts_the_distance_down()
    {
        var route = Straight();
        var last = double.PositiveInfinity;

        for (var i = 0; i <= 10; i++)
        {
            var progress = RouteProgress.Along(route, 49.89 + (i * 0.001), -97.14);

            Assert.NotNull(progress);
            Assert.True(progress.Value.RemainingMetres < last, $"vertex {i} did not count down");
            last = progress.Value.RemainingMetres;
        }
    }

    /// <summary>
    /// The duration is the router's, shared out by the distance left — not a fresh estimate.
    /// </summary>
    [Fact]
    public void The_time_left_is_the_routers_own_time_pro_rated()
    {
        var progress = RouteProgress.Along(Straight(seconds: 600), 49.895, -97.14);

        Assert.NotNull(progress);
        Assert.Equal(300, progress.Value.RemainingSeconds, 1.0);
    }

    [Fact]
    public void Past_the_end_of_the_line_there_is_nothing_left()
    {
        // A kilometre beyond the last vertex, straight on.
        var progress = RouteProgress.Along(Straight(), 49.91, -97.14);

        Assert.NotNull(progress);
        Assert.Equal(0, progress.Value.RemainingMetres, 1.0);
    }

    [Fact]
    public void A_position_beside_the_line_is_still_on_the_route()
    {
        // Fifty metres east of the middle of the line: inside the limit, so believed.
        var progress = RouteProgress.Along(Straight(), 49.895, -97.14 + (50 / (Degree * Math.Cos(49.895 * Math.PI / 180))));

        Assert.NotNull(progress);
        Assert.Equal(50, progress.Value.OffRouteMetres, 1.0);
        Assert.True(progress.Value.IsOnRoute);
    }

    /// <summary>
    /// The refusal that matters. A truck that has left the route must not read a cheerfully
    /// shrinking distance to a place it is driving away from.
    /// </summary>
    [Fact]
    public void A_position_well_off_the_line_is_off_the_route()
    {
        var progress = RouteProgress.Along(Straight(), 49.895, -97.16);

        Assert.NotNull(progress);
        Assert.False(progress.Value.IsOnRoute);
        Assert.InRange(progress.Value.OffRouteMetres, 1000, 1600);
    }

    [Fact]
    public void A_route_with_no_geometry_cannot_be_measured() =>
        Assert.Null(RouteProgress.Along(new Route(1000, 60, [], []), 49.89, -97.14));

    [Fact]
    public void A_route_that_is_all_one_point_cannot_be_measured() =>
        Assert.Null(RouteProgress.Along(
            new Route(0, 0, [(-97.14, 49.89), (-97.14, 49.89)], []), 49.89, -97.14));

    [Fact]
    public void A_position_that_is_not_a_number_cannot_be_measured() =>
        Assert.Null(RouteProgress.Along(Straight(), double.NaN, -97.14));
}
