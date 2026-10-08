using System.Buffers.Binary;
using DashDeck.Abstractions;
using DashDeck.Host.PhoneLink;

namespace DashDeck.Host.Tests;

/// <summary>
/// The dongle protocol, exercised without a dongle.
/// </summary>
/// <remarks>
/// The whole argument for the seam. A Carlinkit CPC200 is chosen and not bought (ADR-0019),
/// and framing is exactly where the off-by-one and endianness mistakes live — so every one of
/// them is caught here, on a desk, rather than against hardware that cannot be single-stepped.
/// </remarks>
public sealed class DongleProtocolTests
{
    [Fact]
    public void A_framed_message_round_trips()
    {
        var message = new DongleMessage(DongleMessageType.Touch, new byte[] { 1, 2, 3, 4 });
        var bytes = message.ToBytes();

        Assert.Equal(DongleMessage.HeaderLength + 4, bytes.Length);
        Assert.True(DongleMessage.TryReadHeader(bytes, out var type, out var length));
        Assert.Equal(DongleMessageType.Touch, type);
        Assert.Equal(4, length);
        Assert.Equal(message.Payload.ToArray(), bytes[DongleMessage.HeaderLength..]);
    }

    /// <summary>Little-endian, and the magic is the first thing on the wire.</summary>
    [Fact]
    public void The_header_is_little_endian_with_the_magic_first()
    {
        var bytes = DongleMessage.Heartbeat().ToBytes();

        Assert.Equal(DongleMessage.Magic, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(170u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
    }

    /// <summary>
    /// The inverted-type field is the protocol's only integrity check, and honouring it is
    /// what stops a desynchronised stream reading as corrupt video rather than as a framing
    /// bug.
    /// </summary>
    [Fact]
    public void A_header_whose_type_check_does_not_match_is_refused()
    {
        var bytes = DongleMessage.Heartbeat().ToBytes();
        bytes[12] ^= 0xFF;

        Assert.False(DongleMessage.TryReadHeader(bytes, out _, out _));
    }

    [Fact]
    public void A_header_without_the_magic_is_refused()
    {
        var bytes = DongleMessage.Heartbeat().ToBytes();
        bytes[0] = 0x00;

        Assert.False(DongleMessage.TryReadHeader(bytes, out _, out _));
    }

    /// <summary>
    /// A guard against a desynchronised stream, not a protocol limit: an absurd length field
    /// would otherwise become an allocation, and a four-gigabyte one takes the dash down
    /// harder than a dropped frame.
    /// </summary>
    [Fact]
    public void An_absurd_payload_length_is_refused()
    {
        Span<byte> header = stackalloc byte[DongleMessage.HeaderLength];
        DongleMessage.WriteHeader(header, DongleMessageType.VideoData, 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], int.MaxValue);

        Assert.False(DongleMessage.TryReadHeader(header, out _, out _));
    }

    /// <summary>An unrecognised type is reported as Unknown rather than guessed at or thrown on.</summary>
    [Fact]
    public void An_unrecognised_type_reads_as_unknown()
    {
        Span<byte> header = stackalloc byte[DongleMessage.HeaderLength];
        DongleMessage.WriteHeader(header, (DongleMessageType)9999, 0);

        Assert.True(DongleMessage.TryReadHeader(header, out var type, out _));
        Assert.Equal(DongleMessageType.Unknown, type);
    }

    /// <summary>
    /// Touch is normalised to the dongle's 0–10000 grid, not sent in pixels. Sending WPF
    /// coordinates would put every tap near the top-left of the phone's screen.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 5000)]
    [InlineData(1.0, 10000)]
    [InlineData(2.0, 10000)]
    [InlineData(-1.0, 0)]
    public void Touch_coordinates_are_normalised_and_clamped(double fraction, int expected)
    {
        var payload = DongleMessage.Touch(TouchAction.Down, fraction, fraction).Payload.Span;

        Assert.Equal((int)TouchAction.Down, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
    }

    [Fact]
    public void The_open_message_carries_the_geometry_to_project_at()
    {
        var payload = DongleMessage.Open(912, 513, 30).Payload.Span;

        Assert.Equal(912, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(513, BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
    }

    /// <summary>The video payload carries 20 bytes of its own before the H.264 starts.</summary>
    [Fact]
    public void Video_payloads_split_into_a_header_and_a_bytestream()
    {
        var payload = new byte[20 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 800);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 480);

        Assert.True(DongleMessage.TryReadVideo(payload, out var width, out var height, out var offset));
        Assert.Equal(800, width);
        Assert.Equal(480, height);
        Assert.Equal(20, offset);
    }

    [Fact]
    public void A_video_payload_too_short_to_hold_its_header_is_refused() =>
        Assert.False(DongleMessage.TryReadVideo(new byte[8], out _, out _, out _));

    // ---- The session ----

    /// <summary>
    /// The Open message must go before anything is expected back: a dongle that has not been
    /// told the screen size projects nothing at all.
    /// </summary>
    [Fact]
    public async Task Starting_a_session_opens_the_dongle_with_the_screen_geometry()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);

        Assert.True(await client.StartAsync(912, 513, 30, CancellationToken.None));

        var open = Assert.Single(transport.Sent, m => m.Type is DongleMessageType.Open);
        Assert.Equal(912, BinaryPrimitives.ReadInt32LittleEndian(open.Payload.Span));
    }

    [Fact]
    public async Task A_phone_plugging_in_moves_the_session_to_projecting()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);

        var projecting = new TaskCompletionSource();
        client.StateChanged += state =>
        {
            if (state is PhoneLinkState.Projecting)
            {
                projecting.TrySetResult();
            }
        };

        await client.StartAsync(912, 513, 30, CancellationToken.None);

        // The synthetic dongle announces a phone on open, as a real one does shortly after.
        await projecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PhoneLinkState.Projecting, client.State);
    }

    [Fact]
    public async Task Video_frames_are_counted_and_their_geometry_read()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);

        var arrived = new TaskCompletionSource<int>();
        client.VideoArrived += bytes => arrived.TrySetResult(bytes.Length);

        await client.StartAsync(912, 513, 30, CancellationToken.None);

        var payload = new byte[20 + 5];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 1280);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 720);
        transport.Emit(new DongleMessage(DongleMessageType.VideoData, payload));

        Assert.Equal(5, await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.VideoFrames);
        Assert.Equal((1280, 720), client.VideoSize);
    }

    /// <summary>
    /// Touches are dropped rather than queued when nothing is projecting. A tap that arrives
    /// at a phone which was not connected when it happened is worse than a tap that did
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Touches_are_not_sent_when_nothing_is_projecting()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);

        await client.TouchAsync(TouchAction.Down, 0.5, 0.5);

        Assert.DoesNotContain(transport.Sent, m => m.Type is DongleMessageType.Touch);
    }

    /// <summary>With no dongle plugged in, the USB transport refuses to open and says why.</summary>
    [Fact]
    public async Task The_usb_transport_reports_no_device_until_one_exists()
    {
        await using var transport = new UsbDongleTransport();

        Assert.False(await transport.OpenAsync(CancellationToken.None));
        Assert.False(transport.IsConnected);
        Assert.NotNull(transport.Problem);
    }
}
