using DashDeck.Abstractions;

namespace DashDeck.Host.PhoneLink;

/// <summary>Where a phone-link session has got to. Rendered, never inferred.</summary>
public enum PhoneLinkState
{
    /// <summary>Nothing plugged in, or nothing that answered.</summary>
    NoDongle,

    /// <summary>The dongle is open and waiting for a phone.</summary>
    WaitingForPhone,

    /// <summary>A phone is connected and projecting.</summary>
    Projecting,

    /// <summary>The link was open and dropped.</summary>
    Lost,
}

/// <summary>
/// One projection session: open the dongle, keep it alive, route what comes back.
/// </summary>
/// <remarks>
/// The dongle does the hard part. It performs the Android Auto — and CarPlay — handshake with
/// the phone in its own firmware and hands back H.264 and PCM, which is exactly why this route
/// was chosen over building a head unit (ADR-0019): the alternative was implementing Google's
/// projection protocol, and this is a framed byte stream with four message types that matter.
/// <para>
/// Nothing here touches USB. It drives an <see cref="IDongleTransport"/>, so the whole session
/// runs against a synthetic dongle on a desk.
/// </para>
/// </remarks>
public sealed class DongleClient : IAsyncDisposable
{
    /// <summary>
    /// How often the host must say it is still there.
    /// </summary>
    /// <remarks>
    /// The dongle drops the link without this. Two seconds is the community-observed interval;
    /// it is not a documented value, because there is no documentation.
    /// </remarks>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly IDongleTransport _transport;
    private readonly IClock _clock;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _pump;
    private Task? _heartbeat;
    private bool _disposed;

    public DongleClient(IDongleTransport transport, IClock clock)
    {
        _transport = transport;
        _clock = clock;
    }

    /// <summary>Where the session has got to.</summary>
    public PhoneLinkState State { get; private set; } = PhoneLinkState.NoDongle;

    /// <summary>What the link is, for the screen. Names the synthetic one as synthetic.</summary>
    public string TransportName => _transport.Name;

    /// <summary>Frames received. The honest answer to "is anything actually arriving".</summary>
    public long VideoFrames { get; private set; }

    /// <summary>Messages whose type this build does not recognise. Counted, not guessed at.</summary>
    public long UnknownMessages { get; private set; }

    /// <summary>The projected picture's size, once a frame has said what it is.</summary>
    public (int Width, int Height)? VideoSize { get; private set; }

    /// <summary>Raised when the state changes, so a view can follow without polling.</summary>
    public event Action<PhoneLinkState>? StateChanged;

    /// <summary>Raised for each H.264 payload. The decoder's input, when there is one.</summary>
    public event Action<ReadOnlyMemory<byte>>? VideoArrived;

    /// <summary>
    /// Open the dongle and start pumping.
    /// </summary>
    /// <remarks>
    /// The Open message carries the geometry the phone will render to, so it is sent before
    /// anything is expected back — a dongle that has not been told the screen size projects
    /// nothing at all rather than projecting badly.
    /// </remarks>
    public async Task<bool> StartAsync(int width, int height, int frameRate, CancellationToken ct)
    {
        if (!await _transport.OpenAsync(ct).ConfigureAwait(false))
        {
            Move(PhoneLinkState.NoDongle);
            return false;
        }

        await _transport.SendAsync(DongleMessage.Open(width, height, frameRate), ct).ConfigureAwait(false);
        Move(PhoneLinkState.WaitingForPhone);

        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        _heartbeat = Task.Run(() => BeatAsync(_stopping.Token), CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Send a touch, in fractions of the projected surface.
    /// </summary>
    /// <remarks>
    /// Fractions rather than pixels, because the caller knows how big its own surface is and
    /// the dongle's scale is fixed at 0–10000. Converting at the edge keeps the view free of
    /// protocol constants.
    /// </remarks>
    public Task TouchAsync(TouchAction action, double fractionX, double fractionY) =>
        State is PhoneLinkState.Projecting
            ? _transport.SendAsync(DongleMessage.Touch(action, fractionX, fractionY), _stopping.Token)
            : Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();

        // Awaited, not abandoned: an orphaned pump holding a USB handle is how the next
        // launch finds the device busy and reports no dongle.
        foreach (var task in new[] { _pump, _heartbeat })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: that is how they stop.
                }
            }
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await _transport.ReadAsync(ct).ConfigureAwait(false);

            if (message is not { } received)
            {
                Move(State is PhoneLinkState.NoDongle ? PhoneLinkState.NoDongle : PhoneLinkState.Lost);
                return;
            }

            Handle(received);
        }
    }

    private void Handle(DongleMessage message)
    {
        switch (message.Type)
        {
            case DongleMessageType.Plugged:
                Move(PhoneLinkState.Projecting);
                break;

            case DongleMessageType.Unplugged:
                Move(PhoneLinkState.WaitingForPhone);
                break;

            case DongleMessageType.VideoData:
                OnVideo(message.Payload);
                break;

            case DongleMessageType.Unknown:
                UnknownMessages++;
                break;

            default:
                // Audio, Bluetooth, manufacturer info and the rest are recognised and not yet
                // acted on. Falling through deliberately beats a default that counts them as
                // unknown, which would make that counter useless for finding real gaps.
                break;
        }
    }

    private void OnVideo(ReadOnlyMemory<byte> payload)
    {
        if (!DongleMessage.TryReadVideo(payload.Span, out var width, out var height, out var offset))
        {
            return;
        }

        VideoFrames++;
        VideoSize = (width, height);

        // A phone can change its mind about geometry mid-session; the size travels on every
        // frame precisely so that is noticed here rather than by the decoder failing.
        VideoArrived?.Invoke(payload[offset..]);
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _transport.SendAsync(DongleMessage.Heartbeat(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Move(PhoneLinkState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(next);
    }
}
