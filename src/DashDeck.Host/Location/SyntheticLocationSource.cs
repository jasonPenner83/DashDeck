using DashDeck.Abstractions;

namespace DashDeck.Host.Location;

/// <summary>
/// A moving fix with no phone attached — the mock-first source (ADR-0005) for the desk and for a
/// <c>--shot</c>.
/// </summary>
/// <remarks>
/// Computes where a truck would be, weaving gently along a road near Winnipeg at about 60 km/h,
/// from the time since it started — no timer, just arithmetic on the clock. Every fix is stamped
/// <see cref="SignalQuality.Simulated"/>, so it reads on screen as the blue that means "not a
/// real truck" exactly like a synthetic OBD value, and can never be mistaken for a live position.
/// </remarks>
public sealed class SyntheticLocationSource : ILocationSource
{
    private readonly IClock _clock;
    private DateTimeOffset? _started;

    public SyntheticLocationSource(IClock clock) => _clock = clock;

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

            var course = ((30 + (15 * Math.Sin(t / 15))) % 360 + 360) % 360;
            var speed = 60 + (5 * Math.Sin(t / 10));
            var latitude = 49.8951 + (0.0006 * Math.Sin(t / 30));
            var longitude = -97.1384 + (0.0006 * Math.Cos(t / 30));

            return new LocationFix(latitude, longitude, speed, course, SignalQuality.Simulated, _clock.UtcNow);
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
