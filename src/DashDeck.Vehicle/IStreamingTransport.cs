namespace DashDeck.Vehicle;

/// <summary>
/// A transport that can also carry a command whose answer does not end — the adapter's monitor
/// mode, which prints every frame it hears until it is told to stop (ADR-0044).
/// </summary>
/// <remarks>
/// <see cref="IVehicleTransport.ExchangeAsync"/> frames an answer by the adapter's <c>&gt;</c>
/// prompt, and a monitor prints no prompt until it is stopped, so it cannot be carried there.
/// </remarks>
public interface IStreamingTransport : IVehicleTransport
{
    /// <summary>
    /// Send <paramref name="command"/> and yield each line the adapter prints, until it prints its
    /// prompt by itself (it gave up — a full buffer, say) or <paramref name="ct"/> is cancelled.
    /// Cancelling stops the adapter (any character does) and waits for its prompt, so the next
    /// exchange starts clean; the enumeration then simply ends.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(string command, CancellationToken ct);
}
