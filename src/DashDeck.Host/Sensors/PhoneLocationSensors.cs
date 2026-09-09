using DashDeck.Abstractions;
using DashDeck.Host.Location;

namespace DashDeck.Host.Sensors;

/// <summary>
/// The phone's GPS, presented as a device sensor (ADR-0027).
/// </summary>
/// <remarks>
/// Reads the newest <see cref="LocationFix"/> from whatever <see cref="ILocationSource"/> it was
/// given and answers the location channels — latitude, longitude, ground speed — with the source
/// string <c>PHONE · GPS</c>, so its provenance is on screen like every other reading. It also
/// offers a <em>heading</em> from the GPS course, but only while moving: a stationary GPS reports
/// a random course, so below a small speed it declines and the tablet magnetometer answers
/// instead. A fix that stops arriving ages to Stale, the same demotion a weak magnetometer gets.
/// </remarks>
public sealed class PhoneLocationSensors : IDeviceSensors
{
    private readonly ILocationSource _source;
    private readonly IClock _clock;
    private readonly double _headingThresholdKmh;
    private readonly TimeSpan _staleAfter;

    public PhoneLocationSensors(
        ILocationSource source,
        IClock clock,
        double headingThresholdKmh = 5,
        TimeSpan? staleAfter = null)
    {
        _source = source;
        _clock = clock;
        _headingThresholdKmh = headingThresholdKmh;
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public bool Has(SensorDefinition definition) => definition.Source is SensorSource.Gps;

    /// <inheritdoc />
    public SensorReading Read(SensorDefinition definition, MountReference reference)
    {
        if (definition.Source is not SensorSource.Gps)
        {
            return SensorReading.None("PHONE");
        }

        if (Current() is not { } fix)
        {
            return SensorReading.None("PHONE · NO FIX");
        }

        var value = definition.Channel switch
        {
            SensorChannel.Latitude => fix.Latitude,
            SensorChannel.Longitude => fix.Longitude,
            SensorChannel.GroundSpeed => fix.GroundSpeedKmh,
            _ => double.NaN,
        };

        if (double.IsNaN(value) || !definition.InRange(value))
        {
            return SensorReading.None("PHONE · NO FIX");
        }

        return new SensorReading(value, fix.Quality, "PHONE · GPS");
    }

    /// <summary>
    /// A heading from the GPS course, if the phone has a fix and is moving fast enough for the
    /// course to mean anything. False otherwise, so the caller can fall back to the magnetometer.
    /// </summary>
    public bool TryReadHeading(out SensorReading reading)
    {
        if (Current() is { } fix && fix.HasUsableCourse(_headingThresholdKmh))
        {
            reading = new SensorReading(fix.CourseDegrees, fix.Quality, "PHONE · GPS");
            return true;
        }

        reading = SensorReading.None("PHONE · NO FIX");
        return false;
    }

    /// <summary>The phone cannot level a mount — that is the tablet's accelerometer's job.</summary>
    public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();

    /// <summary>The latest fix, with a live one aged to Stale if it has stopped arriving.</summary>
    private LocationFix? Current()
    {
        if (_source.Latest is not { } fix)
        {
            return null;
        }

        if (fix.Quality is SignalQuality.Live or SignalQuality.Simulated
            && _clock.UtcNow - fix.TakenUtc > _staleAfter)
        {
            fix = fix with { Quality = SignalQuality.Stale };
        }

        return fix;
    }
}
