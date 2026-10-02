using System.Text.Json.Serialization;
using DashDeck.Abstractions;

namespace DashDeck.Core.Catalog;

/// <summary>How to turn payload bytes into a number.</summary>
/// <param name="ByteOffset">Index of the first payload byte, after mode and PID.</param>
/// <param name="ByteLength">1, 2 or 4 bytes, big-endian as OBD-II always is.</param>
/// <param name="Signed">True to interpret the raw value as two's complement.</param>
/// <param name="Scale">Multiplier applied to the raw value.</param>
/// <param name="Offset">Added after scaling. Temperatures use -40 here.</param>
/// <param name="Unit">Unit symbol carried on every reading.</param>
public sealed record DecodeSpec(
    int ByteOffset,
    int ByteLength,
    bool Signed,
    double Scale,
    double Offset,
    string Unit)
{
    /// <summary>Decode payload bytes, or null when the response is too short to trust.</summary>
    public double? Decode(ReadOnlySpan<byte> payload)
    {
        if (ByteOffset + ByteLength > payload.Length)
        {
            return null;
        }

        long raw = 0;
        for (var i = 0; i < ByteLength; i++)
        {
            raw = (raw << 8) | payload[ByteOffset + i];
        }

        if (Signed)
        {
            var signBit = 1L << ((ByteLength * 8) - 1);
            if ((raw & signBit) != 0)
            {
                raw -= 1L << (ByteLength * 8);
            }
        }

        return (raw * Scale) + Offset;
    }
}

/// <summary>Where a signal comes from.</summary>
public enum SignalSourceKind
{
    /// <summary>A standard or manufacturer OBD-II mode/PID request.</summary>
    ObdPid,
}

/// <summary>
/// One named signal: where to get it, how to decode it, and how fast it is worth asking.
/// </summary>
/// <remarks>
/// These are loaded from JSON, never compiled in. Ford's interesting values — transmission
/// temperature, per-wheel TPMS, real coolant, odometer — are undocumented and will be
/// found by trial and error against the truck. Discovering one must be a config edit.
/// </remarks>
public sealed record SignalDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// A function group for the picker to sort under — "Engine", "Fuel", "Temperature".
    /// </summary>
    /// <remarks>
    /// Presentation metadata, not decode data: it exists only so a person choosing among many
    /// signals can find one by what it is about rather than scrolling an alphabet. Defaults to
    /// "Other" so an untagged signal still lands somewhere sensible.
    /// </remarks>
    public string Category { get; init; } = "Other";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CanBus Bus { get; init; } = CanBus.Hs;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SignalSourceKind Kind { get; init; } = SignalSourceKind.ObdPid;

    public byte Mode { get; init; } = 0x01;

    public required ushort Pid { get; init; }

    /// <summary>
    /// The module to ask, as its 11-bit request id in hex — <c>"726"</c> — or null to ask the
    /// broadcast, as every standard mode 01 PID does (ADR-0035).
    /// </summary>
    /// <remarks>
    /// Mode 01 is legislated: the engine computer answers the broadcast. Everything else on a
    /// Ford — the body module's door states, the ABS module's wheel speeds, the instrument
    /// cluster's odometer — is a mode 22 identifier on a particular module, which answers only
    /// when asked by its own address. Text, not a number, because a file that says
    /// <c>"module": "726"</c> can be checked against FORScan by eye and one that says
    /// <c>1830</c> cannot.
    /// </remarks>
    public string? Module { get; init; }

    /// <summary>The <see cref="Module"/> as a request id, or null for the broadcast or an unreadable one.</summary>
    [JsonIgnore]
    public ushort? ModuleAddress => ParseModule(Module);

    /// <summary>Read a module address typed as hex: "726", "0x726", "7e0". Null for anything else.</summary>
    public static ushort? ParseModule(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length is > 0 and <= 3
            && ushort.TryParse(trimmed, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var id)
            && id is >= 0x700 and <= 0x7F7 && (id & 0x8) == 0
                ? id
                : null;
    }

    public required DecodeSpec Decode { get; init; }

    /// <summary>Rate used when a component does not ask for a specific one.</summary>
    public double DefaultRateHz { get; init; } = 1.0;

    /// <summary>
    /// How long a reading stays <see cref="SignalQuality.Live"/> before going stale.
    /// Defaults to five poll intervals, so a signal polled slowly is not constantly stale.
    /// </summary>
    public double? StalenessSeconds { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    [JsonIgnore]
    public TimeSpan StalenessBudget => TimeSpan.FromSeconds(
        StalenessSeconds ?? Math.Max(2.0, 5.0 / Math.Max(DefaultRateHz, 0.05)));

    public PidRequestSpec ToRequest() => new(Mode, Pid, Bus, ModuleAddress);

    /// <summary>True when a decoded value falls inside the declared physical range.</summary>
    public bool InRange(double value) =>
        (Min is null || value >= Min) && (Max is null || value <= Max);
}

/// <summary>A mode, PID, bus and module, kept free of the Vehicle layer so the catalog stays pure data.</summary>
/// <param name="Module">The module's request id, or null for the broadcast.</param>
public readonly record struct PidRequestSpec(byte Mode, ushort Pid, CanBus Bus, ushort? Module = null);
