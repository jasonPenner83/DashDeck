using DashDeck.Abstractions;

namespace DashDeck.Vehicle;

/// <summary>A single OBD-II request: a mode, a PID, the bus to ask on, and which module to ask.</summary>
/// <param name="Header">
/// The 11-bit CAN id of one module to address — <c>0x726</c> for a Ford body control module —
/// or null for the functional broadcast (<c>7DF</c>) that every emissions ECU listens to. The
/// module answers on <c>Header + 8</c>, and only that answer is listened for (ADR-0035).
/// </param>
public readonly record struct PidRequest(byte Mode, ushort Pid, CanBus Bus, ushort? Header = null)
{
    /// <summary>The functional broadcast id: every emissions ECU listens to it.</summary>
    public const ushort Broadcast = 0x7DF;

    /// <summary>The lowest physical module request id on an 11-bit diagnostic bus.</summary>
    public const ushort FirstModule = 0x700;

    /// <summary>The highest: <c>7F7</c> answers on <c>7FF</c>, the top of the 11-bit range.</summary>
    public const ushort LastModule = 0x7F7;

    /// <summary>
    /// True for an id a module can be addressed on: <c>700</c>–<c>7F7</c>, with bit 3 clear,
    /// because ids with it set are where modules <em>answer</em> (request + 8), and
    /// <c>7DF</c> is the broadcast.
    /// </summary>
    public static bool IsModuleAddress(int id) => id is >= FirstModule and <= LastModule && (id & 0x8) == 0;

    /// <summary>Where the addressed module answers: its request id plus eight.</summary>
    public ushort? ResponseHeader => Header is { } header ? (ushort)(header + 8) : null;

    /// <summary>The ELM command text, e.g. <c>010D</c> for mode 01 PID 0D.</summary>
    public string ToCommand() => Pid <= 0xFF
        ? $"{Mode:X2}{Pid:X2}"
        : $"{Mode:X2}{Pid:X4}";

    public override string ToString() => Header is { } header
        ? $"{ToCommand()} ({Bus} → {header:X3})"
        : $"{ToCommand()} ({Bus})";
}

/// <summary>Why a request produced no data.</summary>
public enum PidFailure
{
    None,

    /// <summary>Adapter replied NO DATA — the vehicle does not support this PID, or did not answer.</summary>
    NoData,

    /// <summary>Adapter reported a bus or protocol problem.</summary>
    BusError,

    /// <summary>Nothing came back in time.</summary>
    Timeout,

    /// <summary>Response arrived but could not be parsed.</summary>
    Malformed,

    /// <summary>
    /// The module answered with a negative response (<c>7F</c>, mode, code): it is there and
    /// heard the request, and declined it. The code is in <see cref="PidResponse.NegativeCode"/>.
    /// </summary>
    Rejected,
}

/// <summary>The decoded payload bytes of a response, or a reason there are none.</summary>
public sealed record PidResponse(
    PidRequest Request,
    byte[] Data,
    PidFailure Failure,
    DateTimeOffset TimestampUtc)
{
    public bool IsSuccess => Failure == PidFailure.None;

    /// <summary>
    /// The negative response code when <see cref="Failure"/> is <see cref="PidFailure.Rejected"/>:
    /// <c>0x31</c> request out of range, <c>0x11</c> service not supported, <c>0x22</c>
    /// conditions not correct, <c>0x33</c> security access denied.
    /// </summary>
    public byte? NegativeCode { get; init; }

    /// <summary>True when something on the bus answered at all — data or a refusal.</summary>
    public bool ModuleAnswered => Failure is PidFailure.None or PidFailure.Rejected;

    public static PidResponse Ok(PidRequest request, byte[] data, DateTimeOffset at) =>
        new(request, data, PidFailure.None, at);

    public static PidResponse Failed(PidRequest request, PidFailure failure, DateTimeOffset at) =>
        new(request, [], failure, at);

    public static PidResponse Refused(PidRequest request, byte code, DateTimeOffset at) =>
        new(request, [], PidFailure.Rejected, at) { NegativeCode = code };
}
