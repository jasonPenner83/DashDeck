using DashDeck.Abstractions;

namespace DashDeck.Host.Sensors;

/// <summary>The tablet's sensors, behind a seam so the preference rule can be tested without hardware.</summary>
public interface IDeviceSensors : IDisposable
{
    /// <summary>True when this machine can supply anything at all for a definition.</summary>
    bool Has(SensorDefinition definition);

    /// <summary>Read a sensor, relative to the mount.</summary>
    SensorReading Read(SensorDefinition definition, MountReference reference);

    /// <summary>Capture the current orientation as level and forward, or null if it cannot.</summary>
    MountReference? CaptureReference(DateTimeOffset nowUtc);
}

/// <summary>
/// Every sensor in the catalog, resolved truck-first.
/// </summary>
/// <remarks>
/// <b>ADR-0016's rule, generalised from one value to all of them.</b> The compass shipped with
/// a hand-written preference between two heading sources; that ADR predicted the fork would
/// recur for location, altitude and G-force, and said each should get "a named signal, a
/// declared fallback and a visible source rather than a fresh judgement call". This is that,
/// driven from the sensor catalog's <c>prefer</c> field rather than from code.
/// <para>
/// Preference is re-evaluated on every read, not fixed at construction, so a truck signal
/// that goes stale hands over to the tablet and takes back over when it recovers.
/// </para>
/// <para>
/// A truck-supplied value is declared through the arbiter like any other signal and costs
/// budget accordingly. A tablet-supplied one costs nothing — reading a magnetometer is not
/// traffic on the OBD-II link — which is precisely why device sensors are a separate catalog
/// rather than more rows in the signal one.
/// </para>
/// </remarks>
public sealed class SensorService : IDisposable
{
    private readonly SensorCatalog _catalog;
    private readonly IVehicleSignals _signals;
    private readonly IClock _clock;
    private readonly IDeviceSensors _device;
    private readonly Dictionary<string, ISignalSubscription> _demands = new(StringComparer.Ordinal);

    public SensorService(
        SensorCatalog catalog,
        IVehicleSignals signals,
        IClock clock,
        IDeviceSensors? device = null,
        MountReference? reference = null)
    {
        _catalog = catalog;
        _signals = signals;
        _clock = clock;
        _device = device ?? new DeviceSensors();
        Reference = reference ?? MountReferenceStore.Load();

        foreach (var definition in catalog.Definitions)
        {
            // Checked against the catalog, not attempted. The arbiter throws on an unknown
            // signal id by design, so an unguarded Require would take the dash down the
            // moment a sensor was read — and none of these signals exist yet.
            if (definition.Prefer is { } prefer && signals.KnownSignals.Contains(prefer))
            {
                _demands[definition.Id] =
                    signals.Require(prefer, SignalPriority.Normal, definition.DefaultRateHz);
            }
        }
    }

    /// <summary>What "level, pointing forward" currently means for this mount.</summary>
    public MountReference Reference { get; private set; }

    /// <summary>True once the mount has been levelled. Until then, anything relative to it is refused.</summary>
    public bool IsLevelled => Reference.IsSet;

    /// <summary>True when any sensor in the catalog is being supplied by the vehicle.</summary>
    public bool AnyFromVehicle => _demands.Count > 0;

    /// <summary>
    /// Read a sensor by catalog id.
    /// </summary>
    /// <remarks>
    /// The truck wins whenever it has a usable answer. Its value is measured by the vehicle,
    /// about the vehicle, and unaffected by whether the tablet is in its cradle or on the
    /// passenger seat — which is the entire argument, and the reason the fallback is labelled
    /// rather than silent.
    /// </remarks>
    public SensorReading Read(string sensorId)
    {
        if (!_catalog.TryGet(sensorId, out var definition))
        {
            return SensorReading.None("NO SUCH SENSOR");
        }

        if (_demands.ContainsKey(sensorId) && definition.Prefer is { } prefer)
        {
            var value = _signals.Current(prefer);

            if (value.Quality is SignalQuality.Live or SignalQuality.Simulated
                && !double.IsNaN(value.Value))
            {
                return new SensorReading(value.Value, value.Quality, "TRUCK");
            }
        }

        return _device.Read(definition, Reference);
    }

    /// <summary>
    /// Capture the tablet's current orientation as level and forward.
    /// </summary>
    /// <remarks>
    /// Meant to be done once, parked, on flat ground, with the tablet in its mount. Written
    /// out immediately for the same reason every other choice is: a dash is closed by having
    /// its power pulled, and a levelling that survives only a graceful shutdown would have to
    /// be redone every drive.
    /// </remarks>
    public bool Level()
    {
        if (_device.CaptureReference(_clock.UtcNow) is not { } captured)
        {
            return false;
        }

        Reference = captured;
        MountReferenceStore.Save(captured);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var demand in _demands.Values)
        {
            demand.Dispose();
        }

        _demands.Clear();
        _device.Dispose();
    }
}
