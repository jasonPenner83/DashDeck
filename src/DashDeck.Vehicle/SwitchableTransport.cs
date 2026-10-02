namespace DashDeck.Vehicle;

/// <summary>
/// A transport whose far end can be swapped while everything above keeps running (ADR-0034).
/// </summary>
/// <remarks>
/// How a dash started on the simulator becomes a dash on the truck without a restart. The
/// adapter, the arbiter, the state bus, every card and component subscribe once, at launch, to
/// one pipeline; this is the bottom of it, and <see cref="SwitchToAsync"/> changes what is at
/// the bottom. A switch raises <see cref="StateChanged"/> with Connected, which is what tells
/// <c>ElmAdapter</c> to configure the new adapter before its next request.
/// <para>
/// Exchanges and the switch share one gate, so a switch never lands in the middle of an
/// exchange and a reply from the old end is never read as an answer from the new one.
/// </para>
/// </remarks>
public sealed class SwitchableTransport : IVehicleTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SwitchableTransport(IVehicleTransport initial)
    {
        Current = initial;
        Current.StateChanged += Forward;
    }

    /// <summary>What is at the far end now.</summary>
    public IVehicleTransport Current { get; private set; }

    public TransportState State => Current.State;

    public string Description => Current.Description;

    public event Action<TransportState>? StateChanged;

    public Task ConnectAsync(CancellationToken ct) => Current.ConnectAsync(ct);

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await Current.ExchangeAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Put a different transport at the far end, and dispose the old one.</summary>
    public async Task SwitchToAsync(IVehicleTransport next, CancellationToken ct = default)
    {
        IVehicleTransport previous;

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            previous = Current;
            previous.StateChanged -= Forward;
            Current = next;
            next.StateChanged += Forward;
        }
        finally
        {
            _gate.Release();
        }

        await previous.DisposeAsync().ConfigureAwait(false);
        StateChanged?.Invoke(next.State);
    }

    private void Forward(TransportState state) => StateChanged?.Invoke(state);

    public async ValueTask DisposeAsync()
    {
        Current.StateChanged -= Forward;
        await Current.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
