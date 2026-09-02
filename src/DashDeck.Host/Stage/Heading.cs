using DashDeck.Abstractions;

namespace DashDeck.Host.Stage;

/// <summary>One heading, and everything needed to decide whether to believe it.</summary>
/// <param name="Degrees">Degrees clockwise from north, 0–360. NaN when there is no reading.</param>
/// <param name="Quality">How far this can be trusted, on the same scale every signal uses.</param>
/// <param name="Source">Where it came from, shown on screen. Never inferred, never hidden.</param>
public readonly record struct HeadingReading(double Degrees, SignalQuality Quality, string Source)
{
    /// <summary>Nothing to show, from a source that at least says who it is.</summary>
    public static HeadingReading None(string source) =>
        new(double.NaN, SignalQuality.Unavailable, source);

    /// <summary>True when there is a number worth drawing.</summary>
    public bool IsUsable => Quality is SignalQuality.Live or SignalQuality.Simulated
        && !double.IsNaN(Degrees);

    /// <summary>
    /// The compass point, to eight of them.
    /// </summary>
    /// <remarks>
    /// Sixteen points would be more precise and less readable at a glance, and the number is
    /// right there beside it for anyone who wants precision.
    /// </remarks>
    public string Cardinal
    {
        get
        {
            if (!IsUsable)
            {
                return "——";
            }

            string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

            // Half a sector of bias, so each point owns the 45° centred on it rather than
            // the 45° starting at it — otherwise due north reads as north-east.
            var normalised = ((Degrees % 360) + 360) % 360;
            return points[(int)Math.Floor(((normalised + 22.5) % 360) / 45)];
        }
    }
}

/// <summary>
/// Prefers the truck, falls back to the tablet, and always says which it used.
/// </summary>
/// <remarks>
/// <b>Ask the vehicle first, and only then the device.</b> The truck's own answer is the
/// better one wherever it exists — it is measured by the vehicle, about the vehicle, and it
/// arrives as a named signal like everything else. A sensor in the tablet is a substitute for
/// a value the truck has and will not tell us yet.
/// <para>
/// The fallback is not silent. Which source won is rendered on the stage, because "the truck
/// says 341°" and "the thing stuck to your windscreen says 341°" are different claims and a
/// dash that conflates them is lying by omission.
/// </para>
/// <para>
/// Preference is re-evaluated on every read rather than fixed at construction, so a truck
/// signal that goes stale hands over to the tablet and takes back over when it recovers.
/// </para>
/// </remarks>
public sealed class PreferredHeadingSource(IHeadingSource preferred, IHeadingSource fallback)
    : IHeadingSource
{
    /// <inheritdoc />
    public string Name => Read().Source;

    /// <inheritdoc />
    public bool IsAvailable => preferred.IsAvailable || fallback.IsAvailable;

    /// <summary>True while the answer is coming from the second choice.</summary>
    public bool IsFallingBack => !preferred.Read().IsUsable;

    /// <inheritdoc />
    public HeadingReading Read()
    {
        var first = preferred.Read();

        if (first.IsUsable)
        {
            return first;
        }

        var second = fallback.Read();

        // Neither worked. Report the preferred source's failure rather than the fallback's,
        // since that is the one worth explaining.
        return second.IsUsable || !preferred.IsAvailable ? second : first;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        preferred.Dispose();
        fallback.Dispose();
    }
}

/// <summary>Somewhere a heading can come from.</summary>
public interface IHeadingSource : IDisposable
{
    /// <summary>What this source is, for the line under the rose.</summary>
    string Name { get; }

    /// <summary>True when this source can currently produce anything at all.</summary>
    bool IsAvailable { get; }

    /// <summary>The latest heading, or an unavailable one. Never throws.</summary>
    HeadingReading Read();
}

/// <summary>
/// Heading from the truck, as a named signal.
/// </summary>
/// <remarks>
/// <b>This is the one that is supposed to win.</b> The compass asks the vehicle first and
/// falls back to the tablet, because a heading the truck already knows is better than one
/// derived from a magnetometer bolted to a windscreen — and because the rule in this project
/// is that a component subscribes to a named signal and knows nothing about where it comes
/// from.
/// <para>
/// <c>vehicle.heading</c> is <b>not in the catalog yet</b> and that is deliberate rather than
/// unfinished. It is not in the legislated OBD-II set; the F-150 has a compass, but it lives
/// on a Ford module and reaching it depends on PID discovery against the real truck (R2) and
/// on what the Gateway Module passes (R4, Q5). Guessing a PID now would produce a confidently
/// wrong bearing, which is precisely the failure this project refuses.
/// </para>
/// <para>
/// So this checks whether the catalog defines the signal and stands down when it does not.
/// The day the PID is found, adding a JSON definition switches the compass over with no code
/// change at all — which is the whole point of the catalog being data (ADR-0004).
/// </para>
/// </remarks>
public sealed class TruckHeadingSource : IHeadingSource
{
    /// <summary>The signal id the catalog will define once the PID is known.</summary>
    public const string SignalId = "vehicle.heading";

    private readonly IVehicleSignals _signals;
    private readonly ISignalSubscription? _demand;

    public TruckHeadingSource(IVehicleSignals signals, double rateHz = 2)
    {
        _signals = signals;

        // Checked, not attempted. The arbiter throws on an unknown signal id by design, so
        // an unguarded Require here would take the dash down the moment the compass opened.
        if (signals.KnownSignals.Contains(SignalId))
        {
            _demand = signals.Require(SignalId, SignalPriority.Normal, rateHz);
        }
    }

    /// <inheritdoc />
    public string Name => "TRUCK";

    /// <inheritdoc />
    public bool IsAvailable => _demand is not null;

    /// <inheritdoc />
    public HeadingReading Read()
    {
        if (_demand is null)
        {
            return HeadingReading.None(Name);
        }

        var value = _signals.Current(SignalId);
        return new HeadingReading(value.Value, value.Quality, Name);
    }

    /// <inheritdoc />
    public void Dispose() => _demand?.Dispose();
}
