using DashDeck.Abstractions;
using Windows.Devices.Sensors;

namespace DashDeck.Host.Sensors;

/// <summary>One reading of one sensor, with everything needed to decide whether to believe it.</summary>
/// <param name="Value">The value, in the definition's unit. NaN when there is nothing to show.</param>
/// <param name="Quality">How far it can be trusted, on the same scale every signal uses.</param>
/// <param name="Source">Where it came from, rendered on screen. Never inferred, never hidden.</param>
public readonly record struct SensorReading(double Value, SignalQuality Quality, string Source)
{
    /// <summary>Nothing to show, from a source that at least says who it is.</summary>
    public static SensorReading None(string source) =>
        new(double.NaN, SignalQuality.Unavailable, source);

    /// <summary>True when there is a number worth drawing.</summary>
    public bool IsUsable =>
        Quality is SignalQuality.Live or SignalQuality.Simulated && !double.IsNaN(Value);
}

/// <summary>
/// The tablet's own sensors, read on demand.
/// </summary>
/// <remarks>
/// Polled rather than subscribed. The compass reads at a handful of hertz and a poll is a
/// property read on an already-running sensor, so an event subscription would buy nothing and
/// would need unwinding every time an occupant is disposed.
/// <para>
/// Every reading is expressed through the <see cref="MountReference"/> where the definition
/// says it must be. Nothing here returns a raw device axis; that is the point of the class.
/// </para>
/// </remarks>
public sealed class DeviceSensors : IDeviceSensors
{
    private readonly Compass? _compass;
    private readonly Accelerometer? _accelerometer;
    private readonly Inclinometer? _inclinometer;

    public DeviceSensors()
    {
        // A machine with no magnetometer is a normal state, not an error. Each is taken
        // separately so a missing accelerometer does not cost us the compass.
        _compass = TryGet(Compass.GetDefault);
        _accelerometer = TryGet(Accelerometer.GetDefault);
        _inclinometer = TryGet(Inclinometer.GetDefault);
    }

    /// <summary>True when the last heading was referenced to true north rather than magnetic.</summary>
    public bool IsTrueNorth { get; private set; }

    /// <summary>True when this machine can supply anything at all for a definition.</summary>
    public bool Has(SensorDefinition definition) => definition.Source switch
    {
        SensorSource.Compass => _compass is not null,
        SensorSource.Accelerometer => _accelerometer is not null,
        SensorSource.Inclinometer => _inclinometer is not null,
        _ => false,
    };

    /// <summary>
    /// Read a sensor, relative to the mount.
    /// </summary>
    /// <remarks>
    /// A definition that needs a reference and has not got one reports Unavailable rather
    /// than a raw device axis. That refusal is deliberate: raw is not "approximately right",
    /// it is a third of a g of cornering force in a parked truck.
    /// </remarks>
    public SensorReading Read(SensorDefinition definition, MountReference reference)
    {
        if (!Has(definition))
        {
            return SensorReading.None("NO SENSOR");
        }

        if (definition.NeedsMountReference && !reference.IsSet)
        {
            return SensorReading.None("NOT LEVELLED");
        }

        try
        {
            return definition.Source switch
            {
                SensorSource.Compass => ReadCompass(),
                SensorSource.Inclinometer => ReadInclinometer(definition, reference),
                SensorSource.Accelerometer => ReadAccelerometer(definition, reference),
                _ => SensorReading.None("TABLET"),
            };
        }
        catch (Exception)
        {
            // A sensor that throws mid-drive must not take the dash with it.
            return SensorReading.None("TABLET");
        }
    }

    /// <summary>Capture the current orientation as level and forward.</summary>
    /// <returns>The new reference, or null when the hardware cannot supply one.</returns>
    public MountReference? CaptureReference(DateTimeOffset nowUtc)
    {
        if (_accelerometer?.GetCurrentReading() is not { } acceleration)
        {
            return null;
        }

        var inclination = _inclinometer?.GetCurrentReading();

        return new MountReference
        {
            Pitch = inclination?.PitchDegrees ?? 0,
            Roll = inclination?.RollDegrees ?? 0,
            Gx = acceleration.AccelerationX,
            Gy = acceleration.AccelerationY,
            Gz = acceleration.AccelerationZ,
            CapturedUtc = nowUtc,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: readings are polled, never subscribed, so no sensor is left
        // reporting into a view that has gone.
    }

    private SensorReading ReadCompass()
    {
        if (_compass!.GetCurrentReading() is not { } reading)
        {
            return SensorReading.None("TABLET");
        }

        // True north when there is a location fix behind it, magnetic otherwise. Which one it
        // is changes the label, so on the prairies — where declination is worth about ten
        // degrees — the difference is never silent.
        IsTrueNorth = reading.HeadingTrueNorth is not null;

        return new SensorReading(
            reading.HeadingTrueNorth ?? reading.HeadingMagneticNorth,
            QualityOf(reading.HeadingAccuracy),
            IsTrueNorth ? "TABLET · TRUE" : "TABLET · MAGNETIC");
    }

    private SensorReading ReadInclinometer(SensorDefinition definition, MountReference reference)
    {
        if (_inclinometer!.GetCurrentReading() is not { } reading)
        {
            return SensorReading.None("TABLET");
        }

        // Relative to the mount, not to the earth. A simple difference is right here: a truck
        // does not approach the angles where that stops being true, and one that did would
        // have larger problems than a rounding error.
        var value = definition.Channel is SensorChannel.Pitch
            ? reading.PitchDegrees - reference.Pitch
            : reading.RollDegrees - reference.Roll;

        return Guarded(definition, value, "TABLET");
    }

    private SensorReading ReadAccelerometer(SensorDefinition definition, MountReference reference)
    {
        if (_accelerometer!.GetCurrentReading() is not { } reading)
        {
            return SensorReading.None("TABLET");
        }

        var (lateral, longitudinal) = reference.Resolve(
            reading.AccelerationX,
            reading.AccelerationY,
            reading.AccelerationZ);

        var value = definition.Channel is SensorChannel.Lateral ? lateral : longitudinal;

        return Guarded(definition, value, "TABLET");
    }

    /// <summary>
    /// Apply the definition's declared range, the way the signal catalog does.
    /// </summary>
    /// <remarks>
    /// Out-of-range readings are dropped rather than displayed — three g of lateral force in
    /// a pickup means the sensor is confused, not that the truck is. Same rule as a wrong
    /// decode spec producing a wild number, and the same reason for it.
    /// </remarks>
    private static SensorReading Guarded(SensorDefinition definition, double value, string source) =>
        definition.InRange(value)
            ? new SensorReading(value, SignalQuality.Live, source)
            : SensorReading.None(source);

    /// <summary>
    /// Windows' own confidence, on this dash's scale.
    /// </summary>
    /// <remarks>
    /// <see cref="MagnetometerAccuracy.Approximate"/> becomes <see cref="SignalQuality.Stale"/>
    /// rather than Live, which is a deliberate demotion: approximate is exactly the state a
    /// magnetometer sits in after being carried past a truck door, and a bearing that is
    /// roughly right looks identical on screen to one that is right.
    /// </remarks>
    private static SignalQuality QualityOf(MagnetometerAccuracy accuracy) => accuracy switch
    {
        MagnetometerAccuracy.High => SignalQuality.Live,
        MagnetometerAccuracy.Approximate => SignalQuality.Stale,
        _ => SignalQuality.Unavailable,
    };

    private static T? TryGet<T>(Func<T?> get)
        where T : class
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
