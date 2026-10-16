using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.Sensors;

namespace DashDeck.Host.Tests;

/// <summary>
/// The navigation readouts as device sensors (ADR-0066): a straight line, a bearing, and what is
/// left of a route, offered to a card like any other value.
/// </summary>
/// <remarks>
/// The arithmetic itself is tested in Core, where it can run on any machine. These are about the
/// part that only makes sense here: which channel answers from what, what a reading says when
/// there is no destination or no route or no fix, and that a simulated position produces
/// navigation numbers that say they are simulated.
/// </remarks>
public sealed class NavigationSensorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>Near Winnipeg, where the synthetic track runs.</summary>
    private const double Latitude = 49.89;
    private const double Longitude = -97.14;

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeSource(LocationFix? fix) : ILocationSource
    {
        public string Name => "fake";

        public LocationFix? Latest => fix;

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private static SensorDefinition Def(
        SensorChannel channel,
        string unit = "km",
        double min = 0,
        double max = 20000) =>
        new()
        {
            Id = "nav." + channel,
            Name = channel.ToString(),
            Unit = unit,
            Source = SensorSource.Navigation,
            Channel = channel,
            Min = min,
            Max = max,
        };

    private static LocationFix Fix(
        double latitude = Latitude,
        double longitude = Longitude,
        SignalQuality quality = SignalQuality.Live) =>
        new(latitude, longitude, 60, 0, quality, Now);

    /// <summary>A kilometre of straight road, due north, in eleven vertices.</summary>
    private static Route Straight(double seconds = 600)
    {
        var shape = new List<(double Longitude, double Latitude)>();

        for (var i = 0; i <= 10; i++)
        {
            shape.Add((Longitude, Latitude + (i * 0.0009)));
        }

        return new Route(1000, seconds, shape, [new RouteStep("Head north", 1000)]);
    }

    private static NavigationSensors Sensors(
        LocationFix? fix = null,
        MapCamera? destination = null,
        Route? route = null) =>
        new(() => fix, () => destination, () => route);

    private static SensorReading Read(NavigationSensors sensors, SensorChannel channel, SensorDefinition? definition = null) =>
        sensors.Read(definition ?? Def(channel), new MountReference());

    // ---- The destination ----

    [Fact]
    public void With_no_destination_there_is_nothing_to_show()
    {
        var reading = Read(Sensors(Fix()), SensorChannel.DestinationDistance);

        Assert.False(reading.IsUsable);
        Assert.Equal("NAV · NO DESTINATION", reading.Source);
    }

    [Fact]
    public void The_straight_line_to_the_destination_is_in_kilometres()
    {
        // A tenth of a degree of latitude north: about eleven kilometres.
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);

        var reading = Read(Sensors(Fix(), destination), SensorChannel.DestinationDistance);

        Assert.True(reading.IsUsable);
        Assert.Equal(11.12, reading.Value, 0.02);
        Assert.Equal("NAV · DESTINATION", reading.Source);
    }

    [Theory]
    [InlineData(0.1, 0, 0)]      // due north
    [InlineData(0, 0.1, 90)]     // due east
    [InlineData(-0.1, 0, 180)]   // due south
    [InlineData(0, -0.1, 270)]   // due west
    public void The_bearing_to_the_destination_is_degrees_true(double dLat, double dLon, double expected)
    {
        var destination = new MapCamera(Longitude + dLon, Latitude + dLat, MapCamera.DrivingZoom, 0);

        var reading = Read(Sensors(Fix(), destination), SensorChannel.DestinationBearing, Def(SensorChannel.DestinationBearing, "°", 0, 360));

        Assert.True(reading.IsUsable);
        Assert.Equal(expected, reading.Value, 0.2);
    }

    /// <summary>
    /// The declared range is a guard here exactly as it is on a measured sensor: a distance past
    /// the top of it is refused rather than drawn, which is what stops a card scaled for a town
    /// errand from rendering a drive to Regina as a pegged bar.
    /// </summary>
    [Fact]
    public void A_distance_past_the_declared_range_is_refused()
    {
        var destination = new MapCamera(-104.6189, 50.4452, MapCamera.DrivingZoom, 0);
        var narrow = Def(SensorChannel.DestinationDistance, max: 50);

        var reading = Read(Sensors(Fix(), destination), SensorChannel.DestinationDistance, narrow);

        Assert.False(reading.IsUsable);
    }

    // ---- No fix ----

    [Fact]
    public void With_no_fix_there_is_nothing_to_show()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);

        var reading = Read(Sensors(null, destination), SensorChannel.DestinationDistance);

        Assert.False(reading.IsUsable);
        Assert.Equal("NAV · NO FIX", reading.Source);
    }

    [Fact]
    public void A_fix_the_gps_has_given_up_on_is_no_fix()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);

        var reading = Read(
            Sensors(Fix(quality: SignalQuality.Unavailable), destination),
            SensorChannel.DestinationDistance);

        Assert.Equal("NAV · NO FIX", reading.Source);
    }

    /// <summary>
    /// The whole reason these carry the fix's quality rather than one of their own: a number
    /// worked out from a synthetic track has to read as synthetic, or the desk looks like a drive.
    /// </summary>
    [Fact]
    public void A_simulated_position_makes_simulated_navigation()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);

        var reading = Read(
            Sensors(Fix(quality: SignalQuality.Simulated), destination),
            SensorChannel.DestinationDistance);

        Assert.Equal(SignalQuality.Simulated, reading.Quality);
        Assert.True(reading.IsUsable);
    }

    [Fact]
    public void A_stale_position_makes_a_stale_reading()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);

        var reading = Read(
            Sensors(Fix(quality: SignalQuality.Stale), destination),
            SensorChannel.DestinationDistance);

        Assert.Equal(SignalQuality.Stale, reading.Quality);
        Assert.False(reading.IsUsable);
    }

    // ---- The route ----

    [Fact]
    public void With_no_route_the_route_rows_say_so()
    {
        var sensors = Sensors(Fix());

        Assert.Equal("NAV · NO ROUTE", Read(sensors, SensorChannel.RouteDistance).Source);
        Assert.Equal("NAV · NO ROUTE", Read(sensors, SensorChannel.RouteRemaining).Source);
    }

    /// <summary>
    /// The totals are the router's answer about the road, not a measurement of the truck, so they
    /// need no position and do not borrow a position's quality.
    /// </summary>
    [Fact]
    public void The_route_totals_need_no_fix_at_all()
    {
        var sensors = Sensors(null, route: Straight(seconds: 600));

        var distance = Read(sensors, SensorChannel.RouteDistance);
        var duration = Read(sensors, SensorChannel.RouteDuration, Def(SensorChannel.RouteDuration, "min"));

        Assert.Equal(1.0, distance.Value, 0.001);
        Assert.Equal(10, duration.Value, 0.001);
        Assert.Equal(SignalQuality.Live, distance.Quality);
        Assert.Equal("NAV · ROUTE", distance.Source);
    }

    [Fact]
    public void What_is_left_of_the_route_counts_down_as_the_truck_moves()
    {
        var route = Straight();
        var start = Read(Sensors(Fix(), route: route), SensorChannel.RouteRemaining);
        var later = Read(Sensors(Fix(latitude: Latitude + 0.0045), route: route), SensorChannel.RouteRemaining);

        Assert.True(start.IsUsable);
        Assert.True(later.IsUsable);
        Assert.True(later.Value < start.Value, "the distance left did not shrink");
        Assert.Equal("NAV · ROUTE", later.Source);
    }

    [Fact]
    public void The_time_left_is_minutes()
    {
        // Halfway along a ten-minute route.
        var reading = Read(
            Sensors(Fix(latitude: Latitude + 0.0045), route: Straight(seconds: 600)),
            SensorChannel.RouteEta,
            Def(SensorChannel.RouteEta, "min"));

        Assert.Equal(5, reading.Value, 0.1);
    }

    /// <summary>
    /// The refusal the whole design turns on. A truck that has left the route must not read a
    /// shrinking distance to a place it is driving away from.
    /// </summary>
    [Fact]
    public void Off_the_route_the_countdown_refuses_to_answer()
    {
        var sensors = Sensors(Fix(longitude: Longitude - 0.02), route: Straight());

        var remaining = Read(sensors, SensorChannel.RouteRemaining);

        Assert.False(remaining.IsUsable);
        Assert.Equal("NAV · OFF ROUTE", remaining.Source);
    }

    /// <summary>Off the route is not off the air: the straight line still answers.</summary>
    [Fact]
    public void Off_the_route_the_straight_line_still_answers()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);
        var sensors = Sensors(Fix(longitude: Longitude - 0.02), destination, Straight());

        Assert.True(Read(sensors, SensorChannel.DestinationDistance).IsUsable);
    }

    [Fact]
    public void A_route_with_no_geometry_cannot_be_counted_down()
    {
        var sensors = Sensors(Fix(), route: new Route(1000, 600, [], []));

        Assert.Equal("NAV · NO ROUTE", Read(sensors, SensorChannel.RouteRemaining).Source);
    }

    // ---- Housekeeping ----

    [Fact]
    public void A_sensor_from_another_source_is_declined()
    {
        var gps = new SensorDefinition
        {
            Id = "location.latitude",
            Name = "Latitude",
            Source = SensorSource.Gps,
            Channel = SensorChannel.Latitude,
        };

        Assert.Equal("NAV", Sensors(Fix()).Read(gps, new MountReference()).Source);
    }

    [Fact]
    public void Navigation_levels_nothing() =>
        Assert.Null(Sensors(Fix()).CaptureReference(Now));

    /// <summary>It owns neither the GPS nor the map, so disposing it must not reach either.</summary>
    [Fact]
    public void Disposing_navigation_does_not_close_the_gps()
    {
        var source = new FakeSource(Fix());
        var phone = new PhoneLocationSensors(source, new Clock(Now));
        var navigation = new NavigationSensors(() => phone.Fix, () => null, () => null);

        navigation.Dispose();

        Assert.NotNull(phone.Fix);
    }

    // ---- Through the composite ----

    [Fact]
    public void The_composite_sends_navigation_channels_to_navigation()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);
        var phone = new PhoneLocationSensors(new FakeSource(Fix()), new Clock(Now));
        using var device = new CompositeDeviceSensors(
            new NoSensors(),
            phone,
            new NavigationSensors(() => phone.Fix, () => destination, () => null));

        var definition = Def(SensorChannel.DestinationDistance);

        Assert.True(device.Has(definition));
        Assert.Equal("NAV · DESTINATION", device.Read(definition, new MountReference()).Source);
    }

    /// <summary>A dash built without a map says so rather than pretending the row is broken.</summary>
    [Fact]
    public void A_composite_built_without_navigation_has_no_navigation()
    {
        var phone = new PhoneLocationSensors(new FakeSource(Fix()), new Clock(Now));
        using var device = new CompositeDeviceSensors(new NoSensors(), phone);

        var definition = Def(SensorChannel.DestinationDistance);

        Assert.False(device.Has(definition));
        Assert.Equal("NO NAVIGATION", device.Read(definition, new MountReference()).Source);
    }

    /// <summary>
    /// Navigation reads the phone's aged fix, not the raw source — one staleness rule for the
    /// whole dash, so the location rows and the navigation rows cannot disagree about when the
    /// GPS went quiet.
    /// </summary>
    [Fact]
    public void A_gps_that_goes_quiet_takes_the_navigation_numbers_with_it()
    {
        var destination = new MapCamera(Longitude, Latitude + 0.1, MapCamera.DrivingZoom, 0);
        var phone = new PhoneLocationSensors(
            new FakeSource(Fix()),
            new Clock(Now + TimeSpan.FromMinutes(1)));

        var navigation = new NavigationSensors(() => phone.Fix, () => destination, () => null);

        var reading = Read(navigation, SensorChannel.DestinationDistance);

        Assert.Equal(SignalQuality.Stale, reading.Quality);
        Assert.False(reading.IsUsable);
    }

    /// <summary>A tablet with no sensors of its own, so the composite's other arms are the test.</summary>
    private sealed class NoSensors : IDeviceSensors
    {
        public bool Has(SensorDefinition definition) => false;

        public SensorReading Read(SensorDefinition definition, MountReference reference) =>
            SensorReading.None("NO SENSOR");

        public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

        public void Dispose()
        {
        }
    }
}
