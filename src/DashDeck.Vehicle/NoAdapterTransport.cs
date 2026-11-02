namespace DashDeck.Vehicle;

/// <summary>
/// The bottom of the pipeline when no adapter has answered and simulated data is off (ADR-0082).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing to read, said honestly.</b> The adapter's own setup commands are accepted, so the
/// pipeline starts the way it does on a real adapter. Every question to the vehicle fails as a
/// dropped cable does, which the adapter turns into a timeout. A timeout never writes a signal off
/// as unsupported, and no reading is ever published, so every value on screen is Unavailable.
/// </para>
/// <para>
/// It is replaced the moment the real adapter answers (<c>AdapterFailover</c>), as the synthetic
/// truck is, and what polling learned here is forgotten then.
/// </para>
/// </remarks>
public sealed class NoAdapterTransport : IVehicleTransport
{
    /// <summary>What it says when asked to identify itself.</summary>
    public const string Identity = "NO ADAPTER";

    /// <summary>
    /// Connected, so the pipeline does not try to reconnect a link that has nothing on the other
    /// end. The status strip says there is no adapter in its own words.
    /// </summary>
    public TransportState State => TransportState.Connected;

    public event Action<TransportState>? StateChanged
    {
        add { }
        remove { }
    }

    public string Description => "no adapter — simulated data is off";

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();

        var text = command.Trim().ToUpperInvariant();

        if (text.StartsWith("ATI", StringComparison.Ordinal))
        {
            return Task.FromResult($"{Identity}\r\r>");
        }

        // The adapter's own settings (AT for the ELM327, ST for the OBDLink): accepted, so start-up
        // goes as it does on real hardware.
        if (text.StartsWith("AT", StringComparison.Ordinal) || text.StartsWith("ST", StringComparison.Ordinal))
        {
            return Task.FromResult("OK\r\r>");
        }

        throw new IOException("No adapter is connected, and simulated data is off.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
