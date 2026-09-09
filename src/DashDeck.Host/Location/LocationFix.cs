using DashDeck.Abstractions;

namespace DashDeck.Host.Location;

/// <summary>
/// One position report from the phone.
/// </summary>
/// <remarks>
/// The shape a GPS gives, once an NMEA sentence is decoded: where, how fast, which way, and
/// whether the receiver actually had a fix when it said so. Speed and course are only meaningful
/// while moving — a stationary GPS reports a random course — so a reader checks the speed before
/// trusting the heading. <see cref="Quality"/> is on this dash's own scale, so a weak fix reads
/// the same amber a stale signal does rather than a confident wrong dot.
/// </remarks>
/// <param name="Latitude">Degrees, north positive.</param>
/// <param name="Longitude">Degrees, east positive.</param>
/// <param name="GroundSpeedKmh">Speed over ground, km/h. NaN when the sentence did not carry it.</param>
/// <param name="CourseDegrees">Course over ground, degrees true 0–360. NaN when stationary or absent.</param>
/// <param name="Quality">Live for a good fix, Stale for a weak one, Unavailable for none, Simulated for the synthetic source.</param>
/// <param name="TakenUtc">When the receiver produced it — used to age a fix that stops arriving.</param>
public readonly record struct LocationFix(
    double Latitude,
    double Longitude,
    double GroundSpeedKmh,
    double CourseDegrees,
    SignalQuality Quality,
    DateTimeOffset TakenUtc)
{
    /// <summary>True when there is a position worth drawing.</summary>
    public bool HasFix => Quality is SignalQuality.Live or SignalQuality.Simulated;

    /// <summary>True when a course reading can be believed — there is a fix and the phone is moving.</summary>
    public bool HasUsableCourse(double minSpeedKmh) =>
        HasFix && !double.IsNaN(CourseDegrees) && !double.IsNaN(GroundSpeedKmh) && GroundSpeedKmh >= minSpeedKmh;
}

/// <summary>
/// Somewhere the phone's position comes from — a synthetic track for the desk, a TCP/NMEA feed in
/// the truck, a Bluetooth one later.
/// </summary>
/// <remarks>
/// The transport seam (ADR-0003/0005), the same shape <see cref="PhoneLink.IDongleTransport"/>
/// takes: a name that says "synthetic" when it is, and a latest reading polled by whoever wants
/// it. Read-only — DashDeck consumes the phone's GPS and sends nothing back (ADR-0027, and the
/// privacy line: location never leaves the tablet).
/// </remarks>
public interface ILocationSource : IDisposable
{
    /// <summary>Short description, e.g. <c>synthetic track</c> or <c>tcp 192.168.1.7:11123</c>.</summary>
    string Name { get; }

    /// <summary>The most recent fix, or null if none has arrived. Read on the UI poll.</summary>
    LocationFix? Latest { get; }

    /// <summary>Begin reading — connect, or start the replay. Idempotent.</summary>
    void Start();
}
