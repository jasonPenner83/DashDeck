namespace DashDeck.Vehicle;

/// <summary>
/// Speaks an adapter's command protocol. Knows nothing about which pipe carries it.
/// </summary>
/// <remarks>
/// A future raw-CAN interface implements this same contract and reports much higher
/// <see cref="AdapterCapabilities.MaxRequestsPerSecond"/>. Nothing above this layer
/// changes when that happens — that is the escape hatch for the throughput risk.
/// </remarks>
public interface IVehicleAdapter : IAsyncDisposable
{
    /// <summary>Null until <see cref="InitializeAsync"/> completes.</summary>
    AdapterCapabilities? Capabilities { get; }

    /// <summary>Bring the adapter up and discover what it can do.</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Ask for one PID. Never throws for vehicle-side problems — an unsupported PID or a
    /// silent module is an ordinary <see cref="PidResponse"/> with a
    /// <see cref="PidFailure"/>, because on a vehicle those are normal, not exceptional.
    /// </summary>
    Task<PidResponse> RequestAsync(PidRequest request, CancellationToken ct);
}
