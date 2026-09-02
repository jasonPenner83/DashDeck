using DashDeck.Abstractions;
using Windows.Devices.Sensors;

namespace DashDeck.Host.Stage;

/// <summary>
/// Heading from the tablet's own magnetometer.
/// </summary>
/// <remarks>
/// The fallback, and it is worth being blunt about why it is second choice. This is a
/// magnetometer in a steel cab, near speakers, a metal mount and the vehicle's own wiring —
/// everything a compass is supposed to be kept away from. It also reads the <em>tablet's</em>
/// orientation, not the truck's, so it is only meaningful while the Surface is in its mount
/// and pointing where the truck points.
/// <para>
/// Two things keep that honest rather than hidden. Windows reports its own confidence, which
/// maps onto the same <see cref="SignalQuality"/> scale every value on this dash carries —
/// an unreliable reading renders as unreliable rather than as a number. And the compass says
/// <c>TABLET</c> under the rose, so a bearing from a windscreen magnetometer is never
/// mistaken for one the truck supplied.
/// </para>
/// <para>
/// True north where Windows can work it out — it needs a location fix, which the Surface Pro
/// 7 has no GPS for and gets from Wi-Fi. Magnetic north otherwise, said plainly, because on
/// the prairies the declination is worth about ten degrees and a compass quietly wrong by ten
/// degrees is worse than one that admits which north it means.
/// </para>
/// </remarks>
public sealed class DeviceHeadingSource : IHeadingSource
{
    private readonly Compass? _compass;

    public DeviceHeadingSource()
    {
        try
        {
            _compass = Compass.GetDefault();
        }
        catch (Exception)
        {
            // A machine with no magnetometer is a normal state, not an error. The compass
            // renders "no heading source" and the dash carries on.
            _compass = null;
        }
    }

    /// <summary>True when the last reading was referenced to true north rather than magnetic.</summary>
    public bool IsTrueNorth { get; private set; }

    /// <inheritdoc />
    public string Name => IsTrueNorth ? "TABLET · TRUE" : "TABLET · MAGNETIC";

    /// <inheritdoc />
    public bool IsAvailable => _compass is not null;

    /// <inheritdoc />
    public HeadingReading Read()
    {
        if (_compass is null)
        {
            return HeadingReading.None("NO SENSOR");
        }

        try
        {
            var reading = _compass.GetCurrentReading();

            if (reading is null)
            {
                return HeadingReading.None(Name);
            }

            // True north when there is a location fix behind it, magnetic otherwise. Which
            // one it is changes the label, so the difference is never silent.
            IsTrueNorth = reading.HeadingTrueNorth is not null;
            var degrees = reading.HeadingTrueNorth ?? reading.HeadingMagneticNorth;

            return new HeadingReading(degrees, QualityOf(reading.HeadingAccuracy), Name);
        }
        catch (Exception)
        {
            return HeadingReading.None(Name);
        }
    }

    /// <summary>
    /// Windows' own confidence, on this dash's scale.
    /// </summary>
    /// <remarks>
    /// <see cref="MagnetometerAccuracy.Approximate"/> becomes <see cref="SignalQuality.Stale"/>
    /// rather than Live, which is a deliberate demotion: approximate is exactly the state a
    /// magnetometer sits in after being carried past a truck door, and a bearing that is
    /// roughly right looks identical on screen to one that is right. Unknown and unreliable
    /// are not shown as numbers at all.
    /// </remarks>
    private static SignalQuality QualityOf(MagnetometerAccuracy accuracy) => accuracy switch
    {
        MagnetometerAccuracy.High => SignalQuality.Live,
        MagnetometerAccuracy.Approximate => SignalQuality.Stale,
        _ => SignalQuality.Unavailable,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: readings are polled rather than subscribed, so the sensor is
        // never left reporting into a view that has gone.
    }
}
