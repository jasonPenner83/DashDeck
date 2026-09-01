namespace DashDeck.Vehicle;

/// <summary>Where a transport currently is. Disconnection is a state, not an error.</summary>
public enum TransportState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>
/// Moves bytes to and from an adapter. Knows no protocol whatsoever.
/// </summary>
/// <remarks>
/// Transport and adapter are separate layers because Bluetooth and USB to an
/// OBDLink-class adapter are the same protocol over different pipes (ADR-0003). The split
/// also makes the synthetic truck and log replay ordinary transports rather than special
/// cases, which is what lets the whole stack be built with no hardware.
/// <para>
/// Implementations must treat disconnection as routine and reconnect on their own.
/// Nothing above this layer may require an app restart to recover from an unplugged
/// cable or a tablet waking from sleep.
/// </para>
/// </remarks>
public interface IVehicleTransport : IAsyncDisposable
{
    TransportState State { get; }

    /// <summary>Raised on every state change, including self-initiated reconnects.</summary>
    event Action<TransportState>? StateChanged;

    /// <summary>Human-readable description of what is on the other end, for the status strip.</summary>
    string Description { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Write a command and read until the adapter's prompt. The returned string is raw
    /// adapter output, still in whatever text protocol the adapter speaks.
    /// </summary>
    Task<string> ExchangeAsync(string command, CancellationToken ct);
}
