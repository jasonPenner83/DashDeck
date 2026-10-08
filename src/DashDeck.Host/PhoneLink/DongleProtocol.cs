using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// The control verbs a <see cref="DongleMessageType.Command"/> carries, as one little-endian integer.
/// </summary>
/// <remarks>
/// From the community reverse engineering (the MIT-licensed <c>node-carplay</c> driver), like every
/// other number in this folder (ADR-0019, ADR-0057). Only the ones DashDeck sends or reads are named.
/// </remarks>
public enum DongleCommand
{
    /// <summary>Use the truck's microphone — here, the tablet's.</summary>
    Mic = 7,

    /// <summary>Use the dongle's own microphone.</summary>
    BoxMic = 15,

    EnableNightMode = 16,
    DisableNightMode = 17,

    /// <summary>Send phone audio to the host as PCM — what DashDeck plays.</summary>
    AudioTransferOff = 23,

    /// <summary>Let the phone send audio straight to the car's Bluetooth instead.</summary>
    AudioTransferOn = 22,

    Wifi24G = 24,
    Wifi5G = 25,
    RequestVideoFocus = 500,
    ReleaseVideoFocus = 501,
    WifiEnable = 1000,
    AutoConnectEnable = 1001,

    /// <summary>Connect to the last phone it knew.</summary>
    WifiConnect = 1002,
}

/// <summary>What kind of phone session a <see cref="DongleMessageType.Plugged"/> announces.</summary>
public enum PhoneType
{
    Unknown = 0,
    AndroidMirror = 1,
    CarPlay = 3,
    IPhoneMirror = 4,
    AndroidAuto = 5,
    HiCar = 6,
}

/// <summary>A PCM format the dongle can send: sample rate and channels, 16-bit signed little-endian.</summary>
public readonly record struct PcmFormat(int SampleRate, int Channels)
{
    public int BytesPerSecond => SampleRate * Channels * 2;

    /// <summary>The format a <c>decodeType</c> names, or null for one this build does not know.</summary>
    public static PcmFormat? ForDecodeType(int decodeType) => decodeType switch
    {
        1 or 2 => new PcmFormat(44100, 2),
        3 => new PcmFormat(8000, 1),
        4 => new PcmFormat(48000, 2),
        5 => new PcmFormat(16000, 1),
        6 => new PcmFormat(24000, 1),
        7 => new PcmFormat(16000, 2),
        _ => null,
    };
}

/// <summary>One <see cref="DongleMessageType.AudioData"/> message, read.</summary>
/// <param name="DecodeType">Which PCM format the samples are in.</param>
/// <param name="Volume">A volume the dongle suggests, 0–1.</param>
/// <param name="AudioType">Which stream: media, navigation, a call…</param>
/// <param name="Samples">The PCM, empty for a control message.</param>
/// <param name="Command">A one-byte audio control (a stream starting or stopping), or null.</param>
public readonly record struct DongleAudio(int DecodeType, float Volume, int AudioType, ReadOnlyMemory<byte> Samples, byte? Command)
{
    public const int HeaderLength = 12;

    public PcmFormat? Format => PcmFormat.ForDecodeType(DecodeType);

    /// <summary>
    /// Read an audio payload: decode type, volume and audio type, then either one command byte, four
    /// bytes of volume-ramp duration, or PCM. Null when it is too short to be any of them.
    /// </summary>
    public static DongleAudio? TryRead(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < HeaderLength)
        {
            return null;
        }

        var decodeType = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
        var volume = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, 4));
        var audioType = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
        var rest = payload[HeaderLength..];

        return rest.Length switch
        {
            1 => new DongleAudio(decodeType, volume, audioType, ReadOnlyMemory<byte>.Empty, rest.Span[0]),
            4 => new DongleAudio(decodeType, volume, audioType, ReadOnlyMemory<byte>.Empty, null),
            _ => new DongleAudio(decodeType, volume, audioType, rest, null),
        };
    }
}

/// <summary>
/// How DashDeck asks the dongle to behave — the picture size, Android Auto, and the rest — and the
/// messages that say so, in the order the community driver sends them (ADR-0057).
/// </summary>
/// <param name="Width">The projected picture's width, px — the stage's.</param>
/// <param name="Height">Its height.</param>
/// <param name="FrameRate">Frames a second asked of the phone.</param>
/// <param name="Dpi">Pixel density the phone lays its interface out for. Higher reads larger.</param>
/// <param name="NightMode">Start in the phone's dark theme.</param>
/// <param name="AndroidAuto">
/// Enable Android Auto — written to the dongle's <c>/etc/android_work_mode</c>. Without it a
/// CarPlay-first dongle never offers Android Auto to the phone.
/// </param>
/// <param name="Wifi5G">Use 5 GHz for the phone's link; 2.4 GHz otherwise.</param>
/// <param name="BoxName">What the phone's Bluetooth list calls the dongle.</param>
public sealed record DongleSetup(
    int Width,
    int Height,
    int FrameRate = 30,
    int Dpi = 160,
    bool NightMode = true,
    bool AndroidAuto = true,
    bool Wifi5G = true,
    string BoxName = "DashDeck")
{
    /// <summary>
    /// Everything sent at open, in order: density, the Open with the geometry, night mode, which side
    /// the driver sits, charging, the name, the settings with Android Auto's picture size, Wi-Fi,
    /// microphone, audio routing — and Android Auto switched on.
    /// </summary>
    /// <remarks>
    /// Audio is asked for as PCM to the host (<see cref="DongleCommand.AudioTransferOff"/>): DashDeck
    /// plays it through Windows' own output, never the truck's (C3). The microphone is the dongle's
    /// own, so nothing on the tablet listens.
    /// </remarks>
    public IReadOnlyList<DongleMessage> Messages(DateTimeOffset now) =>
    [
        DongleFiles.Number("/tmp/screen_dpi", Dpi),
        DongleMessage.Open(Width, Height, FrameRate),
        DongleFiles.Boolean("/tmp/night_mode", NightMode),
        DongleFiles.Number("/tmp/hand_drive_mode", 0),
        DongleFiles.Boolean("/tmp/charge_mode", true),
        DongleFiles.Text("/etc/box_name", BoxName),
        BoxSettings(now),
        DongleFiles.Command(DongleCommand.WifiEnable),
        DongleFiles.Command(Wifi5G ? DongleCommand.Wifi5G : DongleCommand.Wifi24G),
        DongleFiles.Command(DongleCommand.BoxMic),
        DongleFiles.Command(DongleCommand.AudioTransferOff),
        DongleFiles.Boolean("/etc/android_work_mode", AndroidAuto),
    ];

    /// <summary>The settings JSON: media delay, the clock, and the picture size Android Auto renders to.</summary>
    public DongleMessage BoxSettings(DateTimeOffset now) =>
        new(DongleMessageType.BoxSettings, Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"mediaDelay":300,"syncTime":{{now.ToUnixTimeSeconds()}},"androidAutoSizeW":{{Width}},"androidAutoSizeH":{{Height}}}""")));
}

/// <summary>
/// The dongle's settings are files it is sent (<see cref="DongleMessageType.SendFile"/>): a path, and
/// the bytes to put there.
/// </summary>
public static class DongleFiles
{
    /// <summary>A file: its name's length with the terminating zero, the name and zero, the content's length, the content.</summary>
    public static DongleMessage File(string path, ReadOnlySpan<byte> content)
    {
        var name = Encoding.ASCII.GetBytes(path + "\0");
        var buffer = new byte[4 + name.Length + 4 + content.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, name.Length);
        name.CopyTo(buffer, 4);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4 + name.Length), content.Length);
        content.CopyTo(buffer.AsSpan(8 + name.Length));
        return new DongleMessage(DongleMessageType.SendFile, buffer);
    }

    /// <summary>A number, as four little-endian bytes.</summary>
    public static DongleMessage Number(string path, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return File(path, bytes);
    }

    /// <summary>A switch, as a number: 1 or 0.</summary>
    public static DongleMessage Boolean(string path, bool value) => Number(path, value ? 1 : 0);

    /// <summary>Text, as ASCII.</summary>
    public static DongleMessage Text(string path, string value) => File(path, Encoding.ASCII.GetBytes(value));

    /// <summary>A control verb.</summary>
    public static DongleMessage Command(DongleCommand command)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)command);
        return new DongleMessage(DongleMessageType.Command, bytes);
    }

    /// <summary>The command a <see cref="DongleMessageType.Command"/> payload carries, or null.</summary>
    public static int? ReadCommand(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : null;

    /// <summary>The phone type a <see cref="DongleMessageType.Plugged"/> payload names.</summary>
    public static PhoneType ReadPhoneType(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 && Enum.IsDefined(typeof(PhoneType), BinaryPrimitives.ReadInt32LittleEndian(payload))
            ? (PhoneType)BinaryPrimitives.ReadInt32LittleEndian(payload)
            : PhoneType.Unknown;
}
