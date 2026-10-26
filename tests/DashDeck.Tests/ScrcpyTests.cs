using System.Buffers.Binary;
using DashDeck.Core.Phone;

namespace DashDeck.Tests;

/// <summary>A real phone mirrored with scrcpy (ADR-0076): the protocol, as scrcpy's own documents and tests give it.</summary>
public class ScrcpyTests
{
    private static readonly ScrcpyVersion Three = ScrcpyVersion.Parse("scrcpy 3.3.4 <https://github.com/Genymobile/scrcpy>")!;
    private static readonly ScrcpyVersion Five = ScrcpyVersion.Parse("scrcpy 5.0.1 <https://github.com/Genymobile/scrcpy>")!;

    // SPS NAL units made by x264 at the sizes shown — High, Baseline and Main, and sizes that are not
    // a multiple of sixteen, so the cropping is read too.
    [Theory]
    [InlineData("67640032acb20088025bcbc2000003000200000300791e306490", 1080, 2400)]
    [InlineData("67640032acb200b406efcb08000003000800000301e478c19240", 1440, 1752)]
    [InlineData("6742c032d9005403c79784000003000400000300f23c60c920", 1336, 1920)]
    [InlineData("674d4032d9007802a7e584000003000400000300f23c60c920", 1920, 1336)]
    [InlineData("67640032acb200c6079fc442000003000200000300791e306490", 1584, 1922)]
    public void The_picture_s_size_is_read_from_its_sps(string sps, int width, int height)
    {
        // As a phone's encoder sends it: start codes, the SPS, then a PPS.
        byte[] config = [0, 0, 0, 1, .. Convert.FromHexString(sps), 0, 0, 0, 1, 0x68, 0xee, 0x3c, 0x80];

        Assert.Equal((width, height), H264Sps.Size(config));
    }

    [Fact]
    public void A_buffer_without_an_sps_has_no_size()
    {
        Assert.Null(H264Sps.Size([0, 0, 0, 1, 0x68, 0xee, 0x3c, 0x80]));
        Assert.Null(H264Sps.Size([]));
        Assert.Null(H264Sps.Size([0, 0, 1, 0x67, 0x64]));   // cut short
    }

    [Theory]
    [InlineData("scrcpy 3.3.4 <https://github.com/Genymobile/scrcpy>", "3.3.4", 3, false)]
    [InlineData("scrcpy 4.1", "4.1", 4, true)]
    [InlineData("scrcpy-win64-v5.0.1", "5.0.1", 5, true)]
    public void The_installed_version_says_which_protocol(string text, string version, int major, bool sessions)
    {
        var parsed = ScrcpyVersion.Parse(text)!;

        Assert.Equal((version, major, sessions), (parsed.Text, parsed.Major, parsed.HasSessionPackets));
    }

    [Fact]
    public void The_server_is_started_with_the_installed_version_and_a_forward_tunnel()
    {
        var command = ScrcpyServer.Command(Three, 0x12345678, 1920, 60, 8_000_000);

        Assert.Equal(["CLASSPATH=/data/local/tmp/scrcpy-server.jar", "app_process", "/", "com.genymobile.scrcpy.Server", "3.3.4"], command.Take(5));
        Assert.Contains("scid=12345678", command);
        Assert.Contains("tunnel_forward=true", command);
        Assert.Contains("max_size=1920", command);
        Assert.Contains("audio=false", command);
        Assert.Equal("scrcpy_12345678", ScrcpyServer.SocketName(0x12345678));
    }

    [Fact]
    public async Task Before_4_0_the_size_comes_first_and_again_in_each_new_sps()
    {
        var stream = new MemoryStream();
        stream.Write(U32(0x68323634));
        stream.Write(U32(1080));
        stream.Write(U32(2400));
        Packet(stream, config: true, key: false, Sps("67640032acb200b406efcb08000003000800000301e478c19240"), headerBits: (63, 62));   // 1440×1752
        Packet(stream, config: false, key: true, [0, 0, 0, 1, 0x65, 1, 2, 3], headerBits: (63, 62));
        stream.Position = 0;

        var items = await Read(stream, Three);

        Assert.Equal(new ScrcpyItem.Size(1080, 2400), items[0]);
        Assert.Equal(new ScrcpyItem.Size(1440, 1752), items[1]);   // the new session, from its SPS
        Assert.True(((ScrcpyItem.Packet)items[2]).Config);
        Assert.True(((ScrcpyItem.Packet)items[3]).Key);
        Assert.Equal([0, 0, 0, 1, 0x65, 1, 2, 3], ((ScrcpyItem.Packet)items[3]).Data);
    }

    [Fact]
    public async Task From_4_0_each_session_announces_its_size()
    {
        var stream = new MemoryStream();
        stream.Write(U32(0x68323634));
        Session(stream, 1584, 1920);
        Packet(stream, config: true, key: false, [0, 0, 0, 1, 0x67, 0x64], headerBits: (62, 61));
        Packet(stream, config: false, key: true, [0, 0, 0, 1, 0x65, 9], headerBits: (62, 61));
        Session(stream, 1696, 1272);
        stream.Position = 0;

        var items = await Read(stream, Five);

        Assert.Equal(new ScrcpyItem.Size(1584, 1920), items[0]);
        Assert.Equal((true, false), (((ScrcpyItem.Packet)items[1]).Config, ((ScrcpyItem.Packet)items[1]).Key));
        Assert.Equal((false, true), (((ScrcpyItem.Packet)items[2]).Config, ((ScrcpyItem.Packet)items[2]).Key));
        Assert.Equal(new ScrcpyItem.Size(1696, 1272), items[3]);
    }

    [Fact]
    public async Task A_stream_that_is_not_h264_is_refused()
    {
        var stream = new MemoryStream(U32(0x68323635));   // H.265

        await Assert.ThrowsAsync<InvalidDataException>(() => Read(stream, Five));
    }

    [Fact]
    public void A_touch_is_the_bytes_scrcpy_s_own_client_sends()
    {
        // scrcpy's test_serialize_inject_touch_event, with the generic finger's id and no buttons.
        byte[] expected =
        [
            2, 0,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xfe,   // pointer id -2: a finger
            0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0xc8,   // 100 200
            0x04, 0x38, 0x07, 0x80,                           // 1080 1920
            0xff, 0xff,                                       // pressure 1
            0, 0, 0, 0, 0, 0, 0, 0,                           // no buttons
        ];

        Assert.Equal(expected, ScrcpyControl.Touch(0, 100, 200, 1080, 1920));
        Assert.Equal([0, 0], ScrcpyControl.Touch(1, 100, 200, 1080, 1920)[22..24]);   // a lift has no pressure
    }

    [Fact]
    public void Keys_and_the_phone_s_own_screen_are_the_bytes_scrcpy_sends()
    {
        // scrcpy's test_serialize_inject_keycode shape: type, action, key code, repeat, meta state.
        Assert.Equal([0, 1, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0], ScrcpyControl.Key(1, ScrcpyControl.KeyCode(AndroidKey.Back)));
        Assert.Equal(3, ScrcpyControl.KeyCode(AndroidKey.Home));
        Assert.Equal(187, ScrcpyControl.KeyCode(AndroidKey.Recents));

        Assert.Equal([10, 0], ScrcpyControl.DisplayPower(false));
        Assert.Equal([17], ScrcpyControl.Reset());
    }

    [Fact]
    public void The_phone_is_found_among_adb_s_devices()
    {
        const string output = """
            List of devices attached
            HT6B0123456            device usb:1-1 product:marlin model:Pixel_XL device:marlin transport_id:3
            emulator-5554          device product:sdk_gtablet_x86_64 model:Pixel_Tablet device:emu64xa transport_id:1
            """;

        var devices = AdbDevice.Parse(output);
        var phone = AdbDevice.Pick(devices)!;

        Assert.Equal(2, devices.Count);
        Assert.Equal(("HT6B0123456", "Pixel XL"), (phone.Serial, phone.Name));
        Assert.Equal("Pixel XL · connected by USB", AdbDevice.Describe(phone));

        Assert.Equal("Pixel XL · tap Allow on the phone", AdbDevice.Describe(AdbDevice.Pick(AdbDevice.Parse("HT6B0123456 unauthorized usb:1-1 model:Pixel_XL"))));
        Assert.Null(AdbDevice.Pick(AdbDevice.Parse("emulator-5554 device")));   // an emulator is not a phone
        Assert.StartsWith("No phone", AdbDevice.Describe(null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_is_told_the_region_s_shape_outright()
    {
        // Full screen beside the key bar, capped at 1920 on the long side; one dp a dash unit.
        Assert.Equal(new AndroidFit(1584, 1920, 278), AndroidFit.For(1824, 2212, 2, 1920));
        Assert.Equal(new AndroidFit(1696, 1272, 320), AndroidFit.For(1696, 1272, 2, 1920));

        // BALANCED: three quarters of SHARP's long side, and the density follows, so nothing shrinks.
        Assert.Equal(new AndroidFit(1184, 1440, 208), AndroidFit.For(1824, 2212, 2, AndroidPicture.LongSide(AndroidPicture.Parse("BALANCED"))));

        Assert.Null(AndroidFit.For(0, 2212, 2, 1920));
        Assert.Equal(
            [["-s", "HT6B", "shell", "wm", "size", "1584x1920"], ["-s", "HT6B", "shell", "wm", "density", "278"]],
            new AndroidFit(1584, 1920, 278).AdbCommands("HT6B"));
    }

    private static async Task<List<ScrcpyItem>> Read(Stream stream, ScrcpyVersion version)
    {
        var items = new List<ScrcpyItem>();
        try
        {
            await foreach (var item in ScrcpyVideo.ReadAsync(stream, version, CancellationToken.None))
            {
                items.Add(item);
            }
        }
        catch (EndOfStreamException)
        {
            // The end of the recorded stream.
        }

        return items;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Sps(string hex) => [0, 0, 0, 1, .. Convert.FromHexString(hex)];

    private static void Session(Stream stream, int width, int height)
    {
        stream.Write([0x80, 0, 0, 0]);
        stream.Write(U32((uint)width));
        stream.Write(U32((uint)height));
    }

    private static void Packet(Stream stream, bool config, bool key, byte[] data, (int Config, int Key) headerBits)
    {
        var flags = 1234UL;   // a presentation time under the flags
        flags |= config ? 1UL << headerBits.Config : 0;
        flags |= key ? 1UL << headerBits.Key : 0;

        var header = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(header, flags);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), data.Length);
        stream.Write(header);
        stream.Write(data);
    }
}
