using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;

namespace DashDeck.Host.Sensors;

/// <summary>
/// The navigation readouts, presented as device sensors (ADR-0066).
/// </summary>
/// <remarks>
/// <b>The one provider that measures nothing.</b> The compass reads a magnetometer and the phone
/// reads a GPS; this reads the fix the phone already gave and the destination the person already
/// chose, and does arithmetic. That is the whole of it — which is why every value carries the
/// fix's own quality rather than one of its own, and why a simulated track produces simulated
/// navigation numbers that can never be mistaken for a real drive.
/// <para>
/// It is wired with closures rather than a reference to the map, for the ordering reason the
/// search and the directions already ran into: the sensors are built long before the map is, and
/// a closure lets this exist from the first poll and start answering the moment there is
/// something to answer with.
/// </para>
/// <para>
/// Nothing here is guidance. There is no turn to announce, no re-routing, and no opinion about
/// which way the truck is pointing — ADR-0058's bundled engine is still the thing that does all
/// of that. What this adds is the countdown, and the refusal when the truck has left the line.
/// </para>
/// </remarks>
public sealed class NavigationSensors : IDeviceSensors
{
    private readonly Func<LocationFix?> _here;
    private readonly Func<MapCamera?> _destination;
    private readonly Func<Route?> _route;

    /// <param name="here">The fix, already aged — the phone's sensors own that rule.</param>
    /// <param name="destination">The chosen place, or null when none has been chosen.</param>
    /// <param name="route">The route to it, or null when none has been worked out.</param>
    public NavigationSensors(
        Func<LocationFix?> here,
        Func<MapCamera?> destination,
        Func<Route?> route)
    {
        _here = here;
        _destination = destination;
        _route = route;
    }

    /// <inheritdoc />
    public bool Has(SensorDefinition definition) => definition.Source is SensorSource.Navigation;

    /// <inheritdoc />
    public SensorReading Read(SensorDefinition definition, MountReference reference)
    {
        if (definition.Source is not SensorSource.Navigation)
        {
            return SensorReading.None("NAV");
        }

        // How far and how long the whole route is came from the router and needs no position at
        // all. Live rather than the fix's quality, because it is the router's own answer: on a
        // synthetic track the totals are real even though the position driving along them is not.
        if (definition.Channel is SensorChannel.RouteDistance or SensorChannel.RouteDuration)
        {
            return _route() is { } whole
                ? Checked(
                    definition,
                    definition.Channel is SensorChannel.RouteDistance ? whole.Metres / 1000 : whole.Seconds / 60,
                    SignalQuality.Live,
                    "NAV · ROUTE")
                : SensorReading.None("NAV · NO ROUTE");
        }

        if (_here() is not { } fix || fix.Quality is SignalQuality.Unavailable)
        {
            return SensorReading.None("NAV · NO FIX");
        }

        switch (definition.Channel)
        {
            case SensorChannel.DestinationDistance:
            case SensorChannel.DestinationBearing:
                if (_destination() is not { } place || !place.IsUsable)
                {
                    return SensorReading.None("NAV · NO DESTINATION");
                }

                return Checked(
                    definition,
                    definition.Channel is SensorChannel.DestinationDistance
                        ? Geo.DistanceMetres(fix.Latitude, fix.Longitude, place.Latitude, place.Longitude) / 1000
                        : Geo.BearingDegrees(fix.Latitude, fix.Longitude, place.Latitude, place.Longitude),
                    fix.Quality,
                    "NAV · DESTINATION");

            case SensorChannel.RouteRemaining:
            case SensorChannel.RouteEta:
                if (_route() is not { } route)
                {
                    return SensorReading.None("NAV · NO ROUTE");
                }

                if (RouteProgress.Along(route, fix.Latitude, fix.Longitude) is not { } progress)
                {
                    return SensorReading.None("NAV · NO ROUTE");
                }

                // The refusal that matters. A truck that has left the route would otherwise read
                // a shrinking distance to a place it is driving away from.
                if (!progress.IsOnRoute)
                {
                    return SensorReading.None("NAV · OFF ROUTE");
                }

                return Checked(
                    definition,
                    definition.Channel is SensorChannel.RouteRemaining
                        ? progress.RemainingMetres / 1000
                        : progress.RemainingSeconds / 60,
                    fix.Quality,
                    "NAV · ROUTE");

            default:
                return SensorReading.None("NAV");
        }
    }

    /// <summary>Navigation cannot level a mount — that is the tablet's accelerometer's job.</summary>
    public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

    /// <summary>
    /// Nothing to release: it owns neither the GPS nor the map, only three ways of asking them.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>Held to the declared range like every other reading, and to being a number.</summary>
    private static SensorReading Checked(
        SensorDefinition definition,
        double value,
        SignalQuality quality,
        string source) =>
        double.IsFinite(value) && definition.InRange(value)
            ? new SensorReading(value, quality, source)
            : SensorReading.None(source);
}
