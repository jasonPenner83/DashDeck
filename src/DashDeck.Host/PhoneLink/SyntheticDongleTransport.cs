using System.Threading.Channels;
using DashDeck.Abstractions;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// A dongle that is not there, behaving like one that is.
/// </summary>
/// <remarks>
/// The counterpart of the synthetic truck (ADR-0005), and it earns its place the same way:
/// everything above the transport — framing, the session, the heartbeat, the touch mapping,
/// the stage occupant — can be written and exercised before any hardware is ordered, and the
/// day a real CPC200 arrives the only untested code is the USB reads.
/// <para>
/// <b>It does not fabricate video.</b> It answers the handshake, plugs a phone in, and keeps
/// the link alive, but it emits no H.264 — because a synthetic picture would be the one thing
/// on this dash that looks like it is working when nothing is connected. The stage says
/// "synthetic dongle, no picture" and means it.
/// </para>
/// </remarks>
public sealed class SyntheticDongleTransport : IDongleTransport
{
    private readonly IClock _clock;
    private readonly Channel<DongleMessage> _inbound = Channel.CreateUnbounded<DongleMessage>();
    private readonly CancellationTokenSource _stopping = new();

    private bool _disposed;

    public SyntheticDongleTransport(IClock clock) => _clock = clock;

    /// <summary>Says what it is. Nothing on this dash pretends to be hardware it is not.</summary>
    public string Name => "SYNTHETIC DONGLE";

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    private readonly List<DongleMessage> _sent = [];

    /// <summary>Every message the host has sent, as a copy. What the tests assert against.</summary>
    public IReadOnlyList<DongleMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Raised for each message the host sends — the heartbeat's thread included.</summary>
    public event Action<DongleMessage>? MessageSent;

    /// <inheritdoc />
    public Task<bool> OpenAsync(CancellationToken ct)
    {
        IsConnected = true;

        // A real dongle announces the phone shortly after the host opens it. Queued rather
        // than delayed, so a test does not have to wait for wall-clock time to pass.
        // It says Android Auto, over Wi-Fi: the phone type, then 1 for wireless.
        var plugged = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(plugged, (int)PhoneType.AndroidAuto);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(plugged.AsSpan(4), 1);
        _inbound.Writer.TryWrite(new DongleMessage(DongleMessageType.Plugged, plugged));

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<DongleMessage?> ReadAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            return await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task SendAsync(DongleMessage message, CancellationToken ct)
    {
        lock (_sent)
        {
            _sent.Add(message);
        }

        MessageSent?.Invoke(message);
        return Task.CompletedTask;
    }

    /// <summary>Push a message at the host, as if the dongle had sent it.</summary>
    public void Emit(DongleMessage message) => _inbound.Writer.TryWrite(message);

    /// <summary>Pretend the phone was unplugged. The session has to notice.</summary>
    public void UnplugPhone() =>
        Emit(new DongleMessage(DongleMessageType.Unplugged, ReadOnlyMemory<byte>.Empty));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        IsConnected = false;

        _inbound.Writer.TryComplete();
        _stopping.Cancel();
        _stopping.Dispose();

        return ValueTask.CompletedTask;
    }
}
