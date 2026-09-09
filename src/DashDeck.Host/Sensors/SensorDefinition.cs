using System.Text.Json.Serialization;

namespace DashDeck.Host.Sensors;

/// <summary>Which piece of hardware a sensor reads.</summary>
public enum SensorSource
{
    Compass,
    Accelerometer,
    Inclinometer,

    /// <summary>The phone's GPS, over the network (ADR-0027). Not the tablet's own — it has none.</summary>
    Gps,
}

/// <summary>Which value to take from that hardware, once the mount reference is applied.</summary>
public enum SensorChannel
{
    Heading,
    Pitch,
    Roll,

    /// <summary>Across the vehicle. Positive to the right.</summary>
    Lateral,

    /// <summary>Along the vehicle. Positive forward, so braking is negative.</summary>
    Longitudinal,

    /// <summary>Latitude in decimal degrees, north positive.</summary>
    Latitude,

    /// <summary>Longitude in decimal degrees, east positive.</summary>
    Longitude,

    /// <summary>Speed over ground, from the GPS rather than the wheels.</summary>
    GroundSpeed,
}

/// <summary>
/// One value the tablet can measure about the vehicle it is riding in.
/// </summary>
/// <remarks>
/// The device half of ADR-0016, expressed as data. <see cref="Prefer"/> is the field that
/// matters: it names the vehicle signal that supersedes this sensor, so the day that id
/// appears in the signal catalog the value starts coming from the truck and the tablet stands
/// down — with no code change on either side.
/// <para>
/// Deliberately a separate catalog from the signal one rather than more rows in it. A signal
/// definition is a mode, a PID, a bus and a decode spec; a sensor has none of those and needs
/// a mount reference, which a PID never does. Folding them together would give both a shape
/// that fits neither, and would put device values into the request arbiter's plan — where
/// they would consume a budget they cost nothing against, since reading a magnetometer is not
/// traffic on the OBD-II link.
/// </para>
/// </remarks>
public sealed record SensorDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Unit symbol carried on every reading, e.g. <c>g</c> or <c>°</c>.</summary>
    public string Unit { get; init; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SensorSource Source { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SensorChannel Channel { get; init; }

    /// <summary>
    /// The vehicle signal that supersedes this sensor, if one is ever defined.
    /// </summary>
    /// <remarks>
    /// Null means the tablet is the only possible source — nothing here is in that position
    /// today, and it would be a surprising thing to add.
    /// </remarks>
    public string? Prefer { get; init; }

    /// <summary>Rate to ask the vehicle for, when the vehicle is supplying it.</summary>
    public double DefaultRateHz { get; init; } = 4;

    /// <summary>
    /// True when the raw device reading is meaningless without a levelled mount.
    /// </summary>
    /// <remarks>
    /// The reason the whole reference exists: a tablet on a kickstand reads 69° of pitch and
    /// nearly a full g on one axis while sitting perfectly still. Heading is the exception —
    /// it is measured against the earth's field rather than against the mount.
    /// </remarks>
    public bool NeedsMountReference { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>True when a value falls inside the declared range. Same guard the signals get.</summary>
    public bool InRange(double value) =>
        (Min is null || value >= Min) && (Max is null || value <= Max);
}
