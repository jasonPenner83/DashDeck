using DashDeck.Abstractions;

namespace DashDeck.Vehicle;

/// <summary>A single OBD-II request: a mode, a PID, and the bus to ask on.</summary>
public readonly record struct PidRequest(byte Mode, ushort Pid, CanBus Bus)
{
    /// <summary>The ELM command text, e.g. <c>010D</c> for mode 01 PID 0D.</summary>
    public string ToCommand() => Pid <= 0xFF
        ? $"{Mode:X2}{Pid:X2}"
        : $"{Mode:X2}{Pid:X4}";

    public override string ToString() => $"{ToCommand()} ({Bus})";
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
}

/// <summary>The decoded payload bytes of a response, or a reason there are none.</summary>
public sealed record PidResponse(
    PidRequest Request,
    byte[] Data,
    PidFailure Failure,
    DateTimeOffset TimestampUtc)
{
    public bool IsSuccess => Failure == PidFailure.None;

    public static PidResponse Ok(PidRequest request, byte[] data, DateTimeOffset at) =>
        new(request, data, PidFailure.None, at);

    public static PidResponse Failed(PidRequest request, PidFailure failure, DateTimeOffset at) =>
        new(request, [], failure, at);
}
