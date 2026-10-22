using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DashDeck.Core.Phone;

/// <summary>
/// The owner's installed scrcpy: its version, which its server must be started with exactly
/// (ADR-0076). The protocol is internal to scrcpy and changes between versions with no
/// compatibility, so the dash speaks the one the installed server speaks.
/// </summary>
/// <param name="Text">The version as scrcpy writes it, e.g. <c>3.3.4</c>.</param>
/// <param name="Major">Its first number.</param>
public sealed partial record ScrcpyVersion(string Text, int Major)
{
    /// <summary>
    /// From 4.0 the video stream announces each capture session's size in a packet of its own; before
    /// it, the size is sent once and a change shows only in the next SPS.
    /// </summary>
    public bool HasSessionPackets => Major >= 4;

    /// <summary>From <c>scrcpy --version</c>'s first line (<c>scrcpy 3.3.4 &lt;https://…&gt;</c>), or a folder name (<c>scrcpy-win64-v3.3.4</c>).</summary>
    public static ScrcpyVersion? Parse(string? text) =>
        Number().Match(text ?? "") is { Success: true } m
            ? new ScrcpyVersion(m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    [GeneratedRegex(@"(?:scrcpy[ -]|-v)((\d+)\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Number();
}

/// <summary>Starting scrcpy's server on the phone, as scrcpy's own client does (ADR-0076).</summary>
public static class ScrcpyServer
{
    /// <summary>Where the server is pushed on the phone.</summary>
    public const string DevicePath = "/data/local/tmp/scrcpy-server.jar";

    /// <summary>The abstract socket the server listens on: <c>scrcpy_</c> and the session id in hex.</summary>
    public static string SocketName(int scid) => string.Create(CultureInfo.InvariantCulture, $"scrcpy_{scid:x8}");

    /// <summary>A session id: 31 random bits, so two clients starting together do not collide.</summary>
    public static int NewScid(Random random) => random.Next(1, int.MaxValue);

    /// <summary>
    /// The shell command that starts the server: video in H.264 at most <paramref name="maxSize"/> on
    /// its long side, no sound yet, control on, and the device listening (a forward tunnel, which
    /// needs no firewall rule on the tablet). The device's name is not sent (nothing shows it), the
    /// clipboard is left alone (its messages would come back on the control socket), the phone is
    /// kept awake while mirrored, and its settings are put back when the server ends.
    /// </summary>
    public static IReadOnlyList<string> Command(ScrcpyVersion version, int scid, int maxSize, int maxFps, int bitRate)
    {
        ArgumentNullException.ThrowIfNull(version);

        return
        [
            "CLASSPATH=" + DevicePath, "app_process", "/", "com.genymobile.scrcpy.Server", version.Text,
            string.Create(CultureInfo.InvariantCulture, $"scid={scid:x8}"),
            "log_level=info",
            "video_codec=h264",
            "audio=false",
            string.Create(CultureInfo.InvariantCulture, $"max_size={maxSize}"),
            string.Create(CultureInfo.InvariantCulture, $"max_fps={maxFps}"),
            string.Create(CultureInfo.InvariantCulture, $"video_bit_rate={bitRate}"),
            "tunnel_forward=true",
            "control=true",
            "stay_awake=true",
            "cleanup=true",
            "clipboard_autosync=false",
            "send_device_meta=false",
        ];
    }

    /// <summary>
    /// What the server said when it failed, from its output: the version mismatch most of all, which
    /// means the dash found a different scrcpy from the one whose server it pushed.
    /// </summary>
    public static string? Failure(IEnumerable<string> output) =>
        (output ?? []).Select(l => l.Trim()).FirstOrDefault(l =>
            l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("does not match", StringComparison.Ordinal)
            || l.Contains("Exception", StringComparison.Ordinal));
}

/// <summary>What the video socket carries, item by item (<see cref="ScrcpyVideo"/>).</summary>
public abstract record ScrcpyItem
{
    /// <summary>The picture is now this size — the size touch is given in.</summary>
    public sealed record Size(int Width, int Height) : ScrcpyItem;

    /// <summary>
    /// H.264 as the phone's encoder made it, Annex-B. A config packet (SPS and PPS) comes before the
    /// frames of each session and must reach the decoder first.
    /// </summary>
    public sealed record Packet(byte[] Data, bool Config, bool Key) : ScrcpyItem;
}

/// <summary>Reading the video socket of a scrcpy server, either protocol (ADR-0076).</summary>
public static class ScrcpyVideo
{
    private const uint H264 = 0x68323634;

    /// <summary>A packet larger than this is not one: the stream is out of step.</summary>
    private const int MaxPacket = 16 * 1024 * 1024;

    /// <summary>
    /// Everything the server sends on the video socket, after the dummy byte the forward tunnel
    /// opens with — which the caller has already read, to know the server was there.
    /// </summary>
    /// <remarks>
    /// <b>Before 4.0:</b> the codec, the width and the height, then packets each behind a 12-byte
    /// header — the config flag in the top bit, the key-frame flag next, the size last. A change of
    /// size is only in the next SPS, so it is read from there.
    /// <para>
    /// <b>From 4.0:</b> the codec, then for each capture session a 12-byte session packet (top bit
    /// set) carrying the size, and media packets (top bit clear) with the config and key-frame flags
    /// one bit lower.
    /// </para>
    /// </remarks>
    public static async IAsyncEnumerable<ScrcpyItem> ReadAsync(
        Stream stream, ScrcpyVersion version, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(version);

        var codec = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(stream, 4, ct).ConfigureAwait(false));
        if (codec != H264)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"the phone sent video codec 0x{codec:x8}, not H.264"));
        }

        (int Width, int Height)? size = null;

        if (!version.HasSessionPackets)
        {
            var meta = await ReadAsync(stream, 8, ct).ConfigureAwait(false);
            size = ((int)BinaryPrimitives.ReadUInt32BigEndian(meta), (int)BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(4)));
            yield return new ScrcpyItem.Size(size.Value.Width, size.Value.Height);
        }

        while (!ct.IsCancellationRequested)
        {
            var header = await ReadAsync(stream, 12, ct).ConfigureAwait(false);
            var flags = BinaryPrimitives.ReadUInt64BigEndian(header);

            if (version.HasSessionPackets && (flags & (1UL << 63)) != 0)
            {
                size = ((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)), (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                yield return new ScrcpyItem.Size(size.Value.Width, size.Value.Height);
                continue;
            }

            var (configBit, keyBit) = version.HasSessionPackets ? (62, 61) : (63, 62);
            var config = (flags & (1UL << configBit)) != 0;
            var key = (flags & (1UL << keyBit)) != 0;

            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
            if (length is < 0 or > MaxPacket)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"a video packet of {length} bytes"));
            }

            var data = await ReadAsync(stream, length, ct).ConfigureAwait(false);

            // Before 4.0 a new session says its size only in its SPS.
            if (config && !version.HasSessionPackets && H264Sps.Size(data) is { } fromSps && fromSps != size)
            {
                size = fromSps;
                yield return new ScrcpyItem.Size(fromSps.Width, fromSps.Height);
            }

            yield return new ScrcpyItem.Packet(data, config, key);
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>Control messages to the scrcpy server (ADR-0076): the same bytes in every version so far.</summary>
public static class ScrcpyControl
{
    private const byte InjectKeycode = 0;
    private const byte InjectTouch = 2;
    private const byte SetDisplayPower = 10;
    private const byte ResetVideo = 17;

    /// <summary>A finger, as scrcpy's client names one that is not the mouse.</summary>
    private const long GenericFinger = -2;

    /// <summary>Android's key codes for the key bar.</summary>
    public static int KeyCode(AndroidKey key) => key switch
    {
        AndroidKey.Back => 4,
        AndroidKey.Home => 3,
        AndroidKey.Recents => 187,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>
    /// A finger down (0), up (1) or moved (2) at a point of the picture, which must be given with the
    /// picture's size as the server last sent it — a touch for another size is ignored.
    /// </summary>
    public static byte[] Touch(int action, int x, int y, int width, int height)
    {
        var message = new byte[32];
        message[0] = InjectTouch;
        message[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(2), GenericFinger);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22), action == 1 ? (ushort)0 : ushort.MaxValue);

        // Bytes 24–31: the action button and buttons — none, for a finger.
        return message;
    }

    /// <summary>A key pressed (0) or released (1).</summary>
    public static byte[] Key(int action, int keyCode)
    {
        var message = new byte[14];
        message[0] = InjectKeycode;
        message[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(2), keyCode);

        // Bytes 6–13: repeat and meta state, none.
        return message;
    }

    /// <summary>The phone's own screen on or off — mirrored, it is off, which saves its battery and heat.</summary>
    public static byte[] DisplayPower(bool on) => [SetDisplayPower, on ? (byte)1 : (byte)0];

    /// <summary>Ask for a fresh key frame, so a decoder joining late has something whole to start from.</summary>
    public static byte[] Reset() => [ResetVideo];
}

/// <summary>
/// An H.264 sequence parameter set, read for one thing: the picture's size, after cropping.
/// </summary>
public static class H264Sps
{
    private static readonly int[] HighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    /// <summary>
    /// The size in the first SPS of an Annex-B buffer — the config packet a phone's encoder sends —
    /// or null when there is none, or it cannot be read.
    /// </summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> annexB)
    {
        ReadOnlySpan<byte> startCode = [0, 0, 1];

        for (var at = annexB.IndexOf(startCode); at >= 0 && at + 3 < annexB.Length;)
        {
            var nal = annexB[(at + 3)..];
            var next = nal.IndexOf(startCode);
            if ((nal[0] & 0x1F) == 7)
            {
                // A four-byte start code leaves a zero on the end, past anything the size is read from.
                return Read(next < 0 ? nal : nal[..next]);
            }

            at = next < 0 ? -1 : at + 3 + next;
        }

        return null;
    }

    /// <summary>The size in one SPS NAL unit, its header byte first.</summary>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> nal)
    {
        try
        {
            var bits = new BitReader(Unescape(nal[1..]));

            var profile = bits.Bits(8);
            bits.Bits(16);   // constraint flags, level
            bits.Ue();       // seq_parameter_set_id

            var chroma = 1;
            if (HighProfiles.Contains(profile))
            {
                chroma = bits.Ue();
                if (chroma == 3)
                {
                    bits.Bits(1);   // separate_colour_plane_flag
                }

                bits.Ue();   // bit_depth_luma_minus8
                bits.Ue();   // bit_depth_chroma_minus8
                bits.Bits(1);   // qpprime_y_zero_transform_bypass_flag
                if (bits.Bits(1) == 1)   // seq_scaling_matrix_present_flag
                {
                    for (var list = 0; list < (chroma == 3 ? 12 : 8); list++)
                    {
                        if (bits.Bits(1) == 1)
                        {
                            SkipScalingList(ref bits, list < 6 ? 16 : 64);
                        }
                    }
                }
            }

            bits.Ue();   // log2_max_frame_num_minus4
            var pocType = bits.Ue();
            if (pocType == 0)
            {
                bits.Ue();
            }
            else if (pocType == 1)
            {
                bits.Bits(1);
                bits.Se();
                bits.Se();
                var cycle = bits.Ue();
                for (var n = 0; n < cycle; n++)
                {
                    bits.Se();
                }
            }

            bits.Ue();      // max_num_ref_frames
            bits.Bits(1);   // gaps_in_frame_num_value_allowed_flag
            var widthInMbs = bits.Ue() + 1;
            var heightInMapUnits = bits.Ue() + 1;
            var frameMbsOnly = bits.Bits(1);
            if (frameMbsOnly == 0)
            {
                bits.Bits(1);   // mb_adaptive_frame_field_flag
            }

            bits.Bits(1);   // direct_8x8_inference_flag

            int left = 0, right = 0, top = 0, bottom = 0;
            if (bits.Bits(1) == 1)
            {
                left = bits.Ue();
                right = bits.Ue();
                top = bits.Ue();
                bottom = bits.Ue();
            }

            var (subWidth, subHeight) = chroma switch { 1 => (2, 2), 2 => (2, 1), _ => (1, 1) };
            var cropX = chroma == 0 ? 1 : subWidth;
            var cropY = (chroma == 0 ? 1 : subHeight) * (2 - frameMbsOnly);

            var width = (widthInMbs * 16) - (cropX * (left + right));
            var height = ((2 - frameMbsOnly) * heightInMapUnits * 16) - (cropY * (top + bottom));
            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static void SkipScalingList(ref BitReader bits, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
            {
                next = (last + bits.Se() + 256) % 256;
            }

            last = next == 0 ? last : next;
        }
    }

    /// <summary>The NAL's payload with the encoder's emulation-prevention bytes (00 00 03) taken out.</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> payload)
    {
        var bytes = new List<byte>(payload.Length);
        var zeros = 0;
        foreach (var b in payload)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            bytes.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. bytes];
    }

    private ref struct BitReader(byte[] data)
    {
        private int _at;

        public int Bits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, _at++)
            {
                value = (value << 1) | ((data[_at >> 3] >> (7 - (_at & 7))) & 1);
            }

            return value;
        }

        public int Ue()
        {
            var zeros = 0;
            while (Bits(1) == 0)
            {
                if (++zeros > 31)
                {
                    throw new IndexOutOfRangeException("not Exp-Golomb");
                }
            }

            return zeros == 0 ? 0 : (1 << zeros) - 1 + Bits(zeros);
        }

        public int Se()
        {
            var k = Ue();
            return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
        }
    }
}

/// <summary>A device as <c>adb devices -l</c> lists it.</summary>
/// <param name="Serial">ADB's name for it.</param>
/// <param name="State"><c>device</c> when it can be used; <c>unauthorized</c> until the phone allows the tablet.</param>
/// <param name="Model">The model, e.g. <c>Pixel_XL</c>, or null.</param>
public sealed record AdbDevice(string Serial, string State, string? Model)
{
    /// <summary>True for an Android emulator, which is not a phone to mirror.</summary>
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.Ordinal);

    /// <summary>
    /// True when ADB reaches it over the network: <c>192.168.1.23:5555</c> after <c>adb tcpip</c>, or a
    /// wireless-debugging pairing's own name.
    /// </summary>
    public bool IsWireless => Serial.Contains("._adb-tls-connect", StringComparison.Ordinal)
        || (Serial.LastIndexOf(':') is > 0 and var colon && System.Net.IPAddress.TryParse(Serial[..colon], out _));

    /// <summary>Its model as people write it: <c>Pixel XL</c>.</summary>
    public string Name => Model?.Replace('_', ' ') ?? Serial;

    /// <summary>Every device in <c>adb devices -l</c>'s answer.</summary>
    public static IReadOnlyList<AdbDevice> Parse(string? output) =>
    [
        .. (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("List of devices", StringComparison.Ordinal) && !l.StartsWith('*'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => new AdbDevice(
                p[0],
                p[1],
                p.Skip(2).FirstOrDefault(f => f.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..])),
    ];

    /// <summary>
    /// The phone to mirror: one that has allowed the tablet, by cable before Wi-Fi — the same phone
    /// can be both at once — else the first one at all.
    /// </summary>
    public static AdbDevice? Pick(IReadOnlyList<AdbDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var phones = devices.Where(d => !d.IsEmulator).ToList();
        return phones.Where(d => d.State == "device").OrderBy(d => d.IsWireless).FirstOrDefault() ?? phones.FirstOrDefault();
    }

    /// <summary>One line for the hold menu: the phone and whether it can be used, or how to make it so.</summary>
    public static string Describe(AdbDevice? phone) => phone switch
    {
        null => "No phone — plug one in by USB with USB debugging on",
        { State: "device", IsWireless: true } => $"{phone.Name} · connected over Wi-Fi",
        { State: "device" } => $"{phone.Name} · connected by USB",
        { State: "unauthorized" } => $"{phone.Name} · tap Allow on the phone",
        _ => $"{phone.Name} · {phone.State}",
    };
}
