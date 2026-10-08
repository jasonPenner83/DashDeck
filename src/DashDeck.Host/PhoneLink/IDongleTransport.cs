namespace DashDeck.Host.PhoneLink;

/// <summary>How the dongle is reached. USB today, in principle anything.</summary>
/// <remarks>
/// The same seam the vehicle stack has between <c>SyntheticTransport</c> and a real adapter
/// (ADR-0003), and for the same reason: everything above this line is written, run and tested
/// against a synthetic dongle on a desk, and <see cref="UsbDongleTransport"/> is the only part
/// that needs the real one (ADR-0005, ADR-0019, ADR-0057).
/// </remarks>
public interface IDongleTransport : IAsyncDisposable
{
    /// <summary>What this transport is, for the screen. Says "synthetic" when it is.</summary>
    string Name { get; }

    /// <summary>True once the device is open and readable.</summary>
    bool IsConnected { get; }

    /// <summary>Why the last open found nothing usable, in words for the screen; null when it opened.</summary>
    string? Problem => null;

    /// <summary>Open the device. Returns false when there is nothing to open.</summary>
    Task<bool> OpenAsync(CancellationToken ct);

    /// <summary>
    /// Read the next framed message, or null when the link has closed.
    /// </summary>
    /// <remarks>
    /// Framing is the transport's job because it is the transport that knows where a read
    /// ends — a USB bulk transfer does not respect message boundaries, so a caller reading
    /// "one message" from raw reads would have to reassemble anyway.
    /// </remarks>
    Task<DongleMessage?> ReadAsync(CancellationToken ct);

    /// <summary>Send a framed message.</summary>
    Task SendAsync(DongleMessage message, CancellationToken ct);
}
