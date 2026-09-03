namespace DashDeck.Host.PhoneLink;

/// <summary>How the dongle is reached. USB today, in principle anything.</summary>
/// <remarks>
/// The same seam the vehicle stack has between <c>SyntheticTransport</c> and a real adapter
/// (ADR-0003), and it exists here for the same reason: <b>the hardware has not been bought</b>.
/// A Carlinkit CPC200 is chosen and not ordered, so everything above this line is written,
/// run and tested against a synthetic one — and when the real device arrives, one
/// implementation of this interface is the whole of the work (ADR-0005, ADR-0019).
/// </remarks>
public interface IDongleTransport : IAsyncDisposable
{
    /// <summary>What this transport is, for the screen. Says "synthetic" when it is.</summary>
    string Name { get; }

    /// <summary>True once the device is open and readable.</summary>
    bool IsConnected { get; }

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

/// <summary>
/// The USB transport, for when the dongle exists.
/// </summary>
/// <remarks>
/// <b>Deliberately not implemented.</b> It needs a Carlinkit CPC200 in hand and a WinUSB
/// binding for its VID/PID — the second driver exception, which ADR-0019 amends ADR-0007 to
/// admit. Writing it against a device nobody has would be guessing at bulk endpoint numbers
/// and transfer sizes, and a guess that compiles is worse than a gap that does not: the gap
/// is visible, and this one is one class long.
/// <para>
/// Everything it will plug into — the framing, the session, the stage occupant — is finished
/// and exercised against <see cref="SyntheticDongleTransport"/>.
/// </para>
/// </remarks>
public sealed class UsbDongleTransport : IDongleTransport
{
    /// <summary>The CPC200 family. Confirmed against the device before this is trusted.</summary>
    public const int VendorId = 0x1314;

    /// <inheritdoc />
    public string Name => "CPC200 (USB)";

    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<DongleMessage?> ReadAsync(CancellationToken ct) =>
        Task.FromResult<DongleMessage?>(null);

    /// <inheritdoc />
    public Task SendAsync(DongleMessage message, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
