using System.Buffers.Binary;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// Message classes the dongle speaks.
/// </summary>
/// <remarks>
/// Numbers from the community reverse engineering of the Carlinkit CPC200 family — there is
/// no specification to work from and Google licenses no receiver for a PC (ADR-0019). Only
/// the ones this shell actually uses are named; the rest arrive as
/// <see cref="Unknown"/> and are counted rather than guessed at.
/// </remarks>
public enum DongleMessageType
{
    Unknown = 0,

    /// <summary>Host to dongle: the screen geometry and frame rate to project at.</summary>
    Open = 1,

    /// <summary>Dongle to host: a phone has connected. The payload says which kind (<see cref="PhoneType"/>).</summary>
    Plugged = 2,

    /// <summary>Dongle to host: where the connection has got to.</summary>
    Phase = 3,

    /// <summary>Dongle to host: the phone went away.</summary>
    Unplugged = 4,

    /// <summary>Host to dongle: a single touch point.</summary>
    Touch = 5,

    /// <summary>Dongle to host: H.264 for the projected screen.</summary>
    VideoData = 6,

    /// <summary>Dongle to host: PCM, plus the routing that says what it is.</summary>
    AudioData = 7,

    /// <summary>Either direction: a control verb.</summary>
    Command = 8,

    LogoType = 9,
    BluetoothAddress = 10,
    BluetoothPin = 12,
    BluetoothDeviceName = 13,
    WifiDeviceName = 14,

    /// <summary>Host to dongle: let the phone go.</summary>
    DisconnectPhone = 15,
    BluetoothPairedList = 18,
    ManufacturerInfo = 20,

    /// <summary>Host to dongle: shut down.</summary>
    CloseDongle = 21,
    MultiTouch = 23,
    HiCarLink = 24,

    /// <summary>Host to dongle: JSON settings — the Android Auto picture size among them.</summary>
    BoxSettings = 25,

    /// <summary>Dongle to host: what is playing, as JSON or album art.</summary>
    MediaData = 42,

    /// <summary>Host to dongle, on a timer. The link drops without it.</summary>
    Heartbeat = 170,

    SendFile = 153,
    SoftwareVersion = 204,
}

/// <summary>What a touch is doing. The dongle's own numbering, not WPF's.</summary>
public enum TouchAction
{
    Down = 14,
    Move = 15,
    Up = 16,
}

/// <summary>
/// One framed message to or from the dongle.
/// </summary>
/// <remarks>
/// <b>Sixteen bytes of header, little-endian:</b> magic, payload length, type, and the type
/// again inverted. That last field is the only integrity check the protocol has, and it is
/// worth honouring in both directions — a stream that desynchronises without it reads as a
/// corrupt video frame rather than as a framing bug, which is a miserable thing to debug
/// against hardware you cannot single-step.
/// <para>
/// The protocol is bytes on a wire and needs no USB to exercise, which is the whole reason
/// this type exists separately from any transport: framing is where the fiddly, off-by-one,
/// endianness mistakes live, and every one of them can be caught on a desk with no dongle
/// plugged into anything (ADR-0005, applied again).
/// </para>
/// </remarks>
public readonly record struct DongleMessage(DongleMessageType Type, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Marks the start of every message, in both directions.</summary>
    public const uint Magic = 0x55AA55AA;

    /// <summary>Magic, length, type, inverted type.</summary>
    public const int HeaderLength = 16;

    /// <summary>
    /// The largest payload this will accept.
    /// </summary>
    /// <remarks>
    /// A guard, not a protocol limit. The length field is attacker-controlled in the sense
    /// that a desynchronised stream produces arbitrary values, and a four-gigabyte allocation
    /// from a misframed video packet would take the dash down harder than a dropped frame.
    /// </remarks>
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    /// <summary>Frame this message for the wire.</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[HeaderLength + Payload.Length];
        WriteHeader(buffer, Type, Payload.Length);
        Payload.Span.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }

    /// <summary>Write a 16-byte header into <paramref name="destination"/>.</summary>
    public static void WriteHeader(Span<byte> destination, DongleMessageType type, int payloadLength)
    {
        var raw = (uint)type;

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), ~raw);
    }

    /// <summary>
    /// Read a header, or say why it is not one.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing. A bad header means the stream has desynchronised,
    /// which is a thing to recover from by resynchronising — not an exceptional condition, and
    /// certainly not one worth unwinding a read loop for on every corrupt packet.
    /// </remarks>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> source,
        out DongleMessageType type,
        out int payloadLength)
    {
        type = DongleMessageType.Unknown;
        payloadLength = 0;

        if (source.Length < HeaderLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != Magic)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4));
        var check = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));

        // The inverted-type field is the protocol's only checksum. A header that passes the
        // magic but fails this is a coincidence in a desynchronised stream, and treating it
        // as valid is how a framing bug turns into "the video is corrupt".
        if (check != ~raw)
        {
            return false;
        }

        if (length is < 0 or > MaxPayloadLength)
        {
            return false;
        }

        type = Enum.IsDefined(typeof(DongleMessageType), (int)raw)
            ? (DongleMessageType)raw
            : DongleMessageType.Unknown;

        payloadLength = length;
        return true;
    }

    /// <summary>
    /// The Open message: what geometry to project at.
    /// </summary>
    /// <remarks>
    /// Seven little-endian integers. The width and height are the ones that matter — the
    /// phone renders to exactly this, so getting them wrong means a correctly decoded picture
    /// of the wrong shape, which looks like a scaling bug rather than a handshake one.
    /// </remarks>
    public static DongleMessage Open(int width, int height, int frameRate) =>
        new(DongleMessageType.Open, Ints(width, height, frameRate, 5, 49152, 2, 2));

    /// <summary>The keep-alive. Empty payload; the link drops without it.</summary>
    public static DongleMessage Heartbeat() => new(DongleMessageType.Heartbeat, ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// A touch, in the dongle's coordinate space.
    /// </summary>
    /// <remarks>
    /// <b>Normalised to 0–10000, not pixels.</b> The dongle does not know how big the stage
    /// is and does not care; sending WPF coordinates would put every touch in the top-left
    /// corner of the phone's screen and look like the touch layer was ignoring input.
    /// </remarks>
    public static DongleMessage Touch(TouchAction action, double fractionX, double fractionY) =>
        new(DongleMessageType.Touch, Ints(
            (int)action,
            Normalise(fractionX),
            Normalise(fractionY),
            0));

    /// <summary>Split a video payload into its 20-byte header and the H.264 that follows.</summary>
    /// <remarks>
    /// Width, height, flags and two fields nobody has identified. The frame dimensions repeat
    /// here on every packet, which is useful: it is how a decoder notices the phone changed
    /// its mind about geometry without waiting for a decode failure.
    /// </remarks>
    public static bool TryReadVideo(
        ReadOnlySpan<byte> payload,
        out int width,
        out int height,
        out int offset)
    {
        width = 0;
        height = 0;
        offset = 20;

        if (payload.Length < offset)
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        return true;
    }

    private static int Normalise(double fraction) =>
        Math.Clamp((int)Math.Round(fraction * 10000), 0, 10000);

    private static byte[] Ints(params int[] values)
    {
        var buffer = new byte[values.Length * 4];

        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * 4, 4), values[i]);
        }

        return buffer;
    }
}
