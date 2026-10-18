using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;

namespace DashDeck.Host.Location;

/// <summary>
/// A moving fix with no phone attached — the mock-first source (ADR-0005) for the desk and for a
/// <c>--shot</c>.
/// </summary>
/// <remarks>
/// Computes where a truck would be from the time since it started — no timer, just arithmetic on
/// the clock. Every fix is stamped <see cref="SignalQuality.Simulated"/>, so it reads on screen as
/// the blue that means "not a real truck" exactly like a synthetic OBD value, and can never be
/// mistaken for a live position.
/// <para>
/// <b>It drives a real loop, and its numbers agree with each other.</b> It used to weave inside a
/// sixty-metre box while reporting 60 km/h and a course that had nothing to do with the direction
/// it was moving — fine for showing that a marker is on a map, useless for anything that does
/// arithmetic on a position. Now it circles at a constant 60 km/h on an 800 m radius, so the speed
/// it reports is the speed it travels, the course is the actual tangent, and a distance worked out
/// from it (ADR-0066) changes the way a distance should.
/// </para>
/// </remarks>
public sealed class SyntheticLocationSource : ILocationSource
{
    private readonly IClock _clock;
    private DateTimeOffset? _started;

    public SyntheticLocationSource(IClock clock) => _clock = clock;

    /// <summary>Where the loop is centred — near Winnipeg, as it always has been.</summary>
    private const double CentreLatitude = 49.8951;

    private const double CentreLongitude = -97.1384;

    /// <summary>The loop's radius in metres. About five kilometres a lap at 60 km/h.</summary>
    private const double RadiusMetres = 800;

    /// <summary>A steady 60 km/h, which is now also the speed it actually travels.</summary>
    private const double SpeedKmh = 60;

    /// <inheritdoc />
    public string Name => "synthetic track";

    /// <inheritdoc />
    public LocationFix? Latest
    {
        get
        {
            if (_started is not { } started)
            {
                return null;
            }

            var t = (_clock.UtcNow - started).TotalSeconds;

            // How far round the loop, worked out from the speed and the radius rather than from a
            // chosen period — which is what makes the reported speed true instead of decorative.
            var radians = SpeedKmh / 3.6 / RadiusMetres * t;

            // Starts due north of the centre and travels east, so the course is the tangent.
            var latitude = CentreLatitude + (RadiusMetres * Math.Cos(radians) / Geo.MetresPerDegree);
            var longitude = CentreLongitude
                + (RadiusMetres * Math.Sin(radians)
                    / (Geo.MetresPerDegree * Math.Cos(CentreLatitude * Math.PI / 180)));

            var course = (((radians * 180 / Math.PI) + 90) % 360 + 360) % 360;

            return new LocationFix(latitude, longitude, SpeedKmh, course, SignalQuality.Simulated, _clock.UtcNow);
        }
    }

    /// <inheritdoc />
    public void Start() => _started ??= _clock.UtcNow;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release — it holds no socket and no timer.
    }
}
