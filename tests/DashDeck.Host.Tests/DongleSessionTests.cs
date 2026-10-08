using System.Buffers.Binary;
using System.Text;
using DashDeck.Abstractions;
using DashDeck.Host.PhoneLink;

namespace DashDeck.Host.Tests;

/// <summary>
/// Android Auto through the dongle (ADR-0057): the setup it is sent, the bytes it answers with, and
/// how those bytes are cut back into messages — all on a desk, before the dongle is plugged in.
/// </summary>
public sealed class DongleSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // ---- The setup ----

    /// <summary>
    /// Density before the Open, Android Auto last: the order the community driver uses, and the one
    /// the dongle has been seen to accept.
    /// </summary>
    [Fact]
    public void The_setup_opens_with_the_density_and_ends_by_switching_android_auto_on()
    {
        var messages = new DongleSetup(912, 636).Messages(Now);

        Assert.Equal("/tmp/screen_dpi", FileName(messages[0]));
        Assert.Equal(DongleMessageType.Open, messages[1].Type);
        Assert.Equal("/etc/android_work_mode", FileName(messages[^1]));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(FileContent(messages[^1])));
    }

    [Fact]
    public void Android_auto_can_be_left_off()
    {
        var last = new DongleSetup(912, 636, AndroidAuto: false).Messages(Now)[^1];

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(FileContent(last)));
    }

    /// <summary>Audio comes to the host as PCM — DashDeck plays it on Windows' output, never the truck's (C3).</summary>
    [Fact]
    public void The_setup_asks_for_audio_as_pcm_and_the_dongles_own_microphone()
    {
        var commands = new DongleSetup(912, 636).Messages(Now)
            .Where(m => m.Type is DongleMessageType.Command)
            .Select(m => (DongleCommand)DongleFiles.ReadCommand(m.Payload.Span)!.Value)
            .ToList();

        Assert.Contains(DongleCommand.AudioTransferOff, commands);
        Assert.Contains(DongleCommand.BoxMic, commands);
        Assert.Contains(DongleCommand.WifiEnable, commands);
        Assert.DoesNotContain(DongleCommand.Mic, commands);
    }

    [Theory]
    [InlineData(true, DongleCommand.Wifi5G)]
    [InlineData(false, DongleCommand.Wifi24G)]
    public void The_wifi_band_is_chosen(bool fiveGhz, DongleCommand expected)
    {
        var commands = new DongleSetup(912, 636, Wifi5G: fiveGhz).Messages(Now)
            .Where(m => m.Type is DongleMessageType.Command)
            .Select(m => (DongleCommand)DongleFiles.ReadCommand(m.Payload.Span)!.Value);

        Assert.Contains(expected, commands);
    }

    /// <summary>Android Auto renders to the size in the settings, not the Open's.</summary>
    [Fact]
    public void The_settings_carry_android_autos_picture_size_and_the_clock()
    {
        var json = Encoding.ASCII.GetString(new DongleSetup(912, 636).BoxSettings(Now).Payload.Span);

        Assert.Contains("\"androidAutoSizeW\":912", json, StringComparison.Ordinal);
        Assert.Contains("\"androidAutoSizeH\":636", json, StringComparison.Ordinal);
        Assert.Contains($"\"syncTime\":{Now.ToUnixTimeSeconds()}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_box_name_is_sent_as_text()
    {
        var name = new DongleSetup(912, 636, BoxName: "F150").Messages(Now)
            .Single(m => FileName(m) == "/etc/box_name");

        Assert.Equal("F150", Encoding.ASCII.GetString(FileContent(name)));
    }

    /// <summary>A file: the name's length counts its terminating zero; then the content's length and the content.</summary>
    [Fact]
    public void A_file_is_its_name_with_a_terminator_then_its_content()
    {
        var bytes = DongleFiles.Number("/tmp/x", 7).Payload.ToArray();

        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal("/tmp/x\0", Encoding.ASCII.GetString(bytes, 4, 7));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(15)));
        Assert.Equal(19, bytes.Length);
    }

    [Fact]
    public void A_command_is_one_little_endian_number()
    {
        var message = DongleFiles.Command(DongleCommand.WifiConnect);

        Assert.Equal(DongleMessageType.Command, message.Type);
        Assert.Equal(1002, DongleFiles.ReadCommand(message.Payload.Span));
        Assert.Null(DongleFiles.ReadCommand(new byte[2]));
    }

    // ---- What comes back ----

    [Theory]
    [InlineData(3, PhoneType.CarPlay)]
    [InlineData(5, PhoneType.AndroidAuto)]
    [InlineData(99, PhoneType.Unknown)]
    public void Plugged_names_the_kind_of_phone(int raw, PhoneType expected)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, raw);

        Assert.Equal(expected, DongleFiles.ReadPhoneType(payload));
    }

    [Fact]
    public void An_empty_plugged_names_no_kind() =>
        Assert.Equal(PhoneType.Unknown, DongleFiles.ReadPhoneType([]));

    [Theory]
    [InlineData(1, 44100, 2)]
    [InlineData(2, 44100, 2)]
    [InlineData(3, 8000, 1)]
    [InlineData(4, 48000, 2)]
    [InlineData(5, 16000, 1)]
    [InlineData(6, 24000, 1)]
    [InlineData(7, 16000, 2)]
    public void Each_decode_type_names_a_pcm_format(int decodeType, int rate, int channels) =>
        Assert.Equal(new PcmFormat(rate, channels), PcmFormat.ForDecodeType(decodeType));

    [Fact]
    public void An_unknown_decode_type_names_no_format() => Assert.Null(PcmFormat.ForDecodeType(42));

    [Fact]
    public void Audio_carrying_pcm_reads_its_samples()
    {
        var audio = DongleAudio.TryRead(AudioPayload(4, 0.5f, 1, new byte[] { 1, 2, 3, 4, 5, 6 }));

        Assert.NotNull(audio);
        Assert.Equal(new PcmFormat(48000, 2), audio.Value.Format);
        Assert.Equal(0.5f, audio.Value.Volume);
        Assert.Equal(1, audio.Value.AudioType);
        Assert.Equal(6, audio.Value.Samples.Length);
        Assert.Null(audio.Value.Command);
    }

    [Fact]
    public void Audio_carrying_one_byte_is_a_command_not_a_sample()
    {
        var audio = DongleAudio.TryRead(AudioPayload(1, 0, 1, new byte[] { 2 }));

        Assert.Equal((byte)2, audio!.Value.Command);
        Assert.True(audio.Value.Samples.IsEmpty);
    }

    /// <summary>Four bytes is a volume ramp's duration. Played as PCM it would be a click.</summary>
    [Fact]
    public void Audio_carrying_four_bytes_is_neither_a_command_nor_a_sample()
    {
        var audio = DongleAudio.TryRead(AudioPayload(1, 0, 1, new byte[4]));

        Assert.Null(audio!.Value.Command);
        Assert.True(audio.Value.Samples.IsEmpty);
    }

    [Fact]
    public void Audio_too_short_for_its_header_is_refused() => Assert.Null(DongleAudio.TryRead(new byte[11]));

    // ---- Cutting the stream back into messages ----

    /// <summary>A USB read ends where the transfer does — here, after every single byte.</summary>
    [Fact]
    public void A_message_arriving_a_byte_at_a_time_is_reassembled()
    {
        var reader = new DongleFrameReader();
        var bytes = new DongleMessage(DongleMessageType.VideoData, new byte[] { 9, 8, 7 }).ToBytes();
        DongleMessage? found = null;

        foreach (var b in bytes)
        {
            Assert.Null(found);
            reader.Append([b]);
            found = reader.Next();
        }

        Assert.Equal(DongleMessageType.VideoData, found!.Value.Type);
        Assert.Equal(new byte[] { 9, 8, 7 }, found.Value.Payload.ToArray());
    }

    [Fact]
    public void Two_messages_in_one_read_come_out_as_two()
    {
        var reader = new DongleFrameReader();
        reader.Append([.. DongleMessage.Heartbeat().ToBytes(), .. DongleFiles.Command(DongleCommand.Mic).ToBytes()]);

        Assert.Equal(DongleMessageType.Heartbeat, reader.Next()!.Value.Type);
        Assert.Equal(DongleMessageType.Command, reader.Next()!.Value.Type);
        Assert.Null(reader.Next());
    }

    /// <summary>Out of step, it drops what is not a header — and counts it — until it finds one.</summary>
    [Fact]
    public void Garbage_before_a_header_is_skipped_and_counted()
    {
        var reader = new DongleFrameReader();
        reader.Append([1, 2, 3, 0xAA, 0x55, .. DongleMessage.Heartbeat().ToBytes()]);

        Assert.Equal(DongleMessageType.Heartbeat, reader.Next()!.Value.Type);
        Assert.Equal(5, reader.SkippedBytes);
    }

    /// <summary>The transport reads exactly what is needed: the rest of a header, then the payload it announced.</summary>
    [Fact]
    public void Needed_asks_for_the_header_then_the_payload()
    {
        var reader = new DongleFrameReader();
        var bytes = new DongleMessage(DongleMessageType.AudioData, new byte[100]).ToBytes();

        Assert.Equal(16, reader.Needed);
        reader.Append(bytes.AsSpan(0, 10));
        Assert.Equal(6, reader.Needed);
        reader.Append(bytes.AsSpan(10, 6));
        Assert.Equal(100, reader.Needed);
        reader.Append(bytes.AsSpan(16, 40));
        Assert.Equal(60, reader.Needed);
        reader.Append(bytes.AsSpan(56));
        Assert.NotNull(reader.Next());
        Assert.Equal(16, reader.Needed);
    }

    [Fact]
    public void Needed_asks_for_one_byte_when_out_of_step()
    {
        var reader = new DongleFrameReader();
        reader.Append(new byte[16]);

        Assert.Equal(1, reader.Needed);
    }

    // ---- The session ----

    [Fact]
    public async Task The_session_knows_an_android_auto_phone_from_plugged()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);
        var projecting = new TaskCompletionSource();
        client.StateChanged += s =>
        {
            if (s is PhoneLinkState.Projecting)
            {
                projecting.TrySetResult();
            }
        };

        await client.StartAsync(new DongleSetup(912, 636), CancellationToken.None);
        await projecting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PhoneType.AndroidAuto, client.PhoneType);
    }

    [Fact]
    public async Task Unplugging_forgets_the_kind_of_phone()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);
        var waiting = new TaskCompletionSource();
        var projected = false;
        client.StateChanged += s =>
        {
            if (s is PhoneLinkState.Projecting)
            {
                projected = true;
                transport.UnplugPhone();
            }
            else if (s is PhoneLinkState.WaitingForPhone && projected)
            {
                waiting.TrySetResult();
            }
        };

        await client.StartAsync(new DongleSetup(912, 636), CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PhoneType.Unknown, client.PhoneType);
    }

    [Fact]
    public async Task Audio_is_raised_and_counted()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        await using var client = new DongleClient(transport, SystemClock.Instance);
        var arrived = new TaskCompletionSource<DongleAudio>();
        client.AudioArrived += a => arrived.TrySetResult(a);

        await client.StartAsync(new DongleSetup(912, 636), CancellationToken.None);
        transport.Emit(new DongleMessage(DongleMessageType.AudioData, AudioPayload(5, 1, 3, new byte[320])));

        var audio = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new PcmFormat(16000, 1), audio.Format);
        Assert.Equal(1, client.AudioPackets);
    }

    /// <summary>Once its settings have taken, the dongle is asked for the last phone it knew.</summary>
    [Fact]
    public async Task The_session_asks_for_the_last_phone_after_the_setup()
    {
        var transport = new SyntheticDongleTransport(SystemClock.Instance);
        var reconnect = new TaskCompletionSource();
        transport.MessageSent += m =>
        {
            if (m.Type is DongleMessageType.Command
                && DongleFiles.ReadCommand(m.Payload.Span) == (int)DongleCommand.WifiConnect)
            {
                reconnect.TrySetResult();
            }
        };

        await using var client = new DongleClient(transport, SystemClock.Instance);
        await client.StartAsync(new DongleSetup(912, 636), CancellationToken.None);

        await reconnect.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sent = transport.Sent;
        Assert.True(
            sent.ToList().FindIndex(m => FileName(m) == "/etc/android_work_mode")
            < sent.ToList().FindLastIndex(m => m.Type is DongleMessageType.Command));
    }

    // ---- Feeding the decoder ----

    [Fact]
    public void The_pipe_hands_out_what_was_pushed_in_order()
    {
        using var pipe = new H264Pipe();
        pipe.Push([1, 2, 3]);
        pipe.Push([4, 5]);

        var buffer = new byte[4];
        Assert.Equal(3, pipe.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..3]);
        Assert.Equal(2, pipe.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 4, 5 }, buffer[..2]);
    }

    /// <summary>The decoder's read waits for the dongle, and ends when the pipe does.</summary>
    [Fact]
    public async Task A_read_waits_for_data_and_ends_when_completed()
    {
        using var pipe = new H264Pipe();
        var read = Task.Run(() => pipe.Read(new byte[8], 0, 8));

        await Task.Delay(50);
        Assert.False(read.IsCompleted);

        pipe.Push([7]);
        Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(5)));

        pipe.Complete();
        Assert.Equal(0, await Task.Run(() => pipe.Read(new byte[8], 0, 8)).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A decoder that falls behind loses the backlog rather than showing the past.</summary>
    [Fact]
    public void A_backlog_past_the_limit_is_thrown_away()
    {
        using var pipe = new H264Pipe();
        var chunk = new byte[H264Pipe.MaxQueuedBytes / 2];
        pipe.Push(chunk);
        pipe.Push(chunk);
        pipe.Push([9]);

        Assert.Equal(H264Pipe.MaxQueuedBytes, pipe.DroppedBytes);
        var buffer = new byte[4];
        Assert.Equal(1, pipe.Read(buffer, 0, 4));
        Assert.Equal(9, buffer[0]);
    }

    // ---- Finding the dongle on USB ----

    [Theory]
    [InlineData(@"USB\VID_1314&PID_1520\0123", true)]
    [InlineData(@"\\?\usb#vid_1314&pid_1521#abc#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", true)]
    [InlineData(@"USB\VID_1314&PID_9999\0123", false)]
    [InlineData(@"USB\VID_0403&PID_6015\FTDI", false)]
    public void The_dongle_is_known_by_its_vendor_and_product(string path, bool expected) =>
        Assert.Equal(expected, UsbDongleTransport.MatchesDongle(path));

    /// <summary>Zadig writes the interface GUID as a multi-string; each one is a path to try.</summary>
    [Fact]
    public void Interface_guids_are_read_from_a_multi_string()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var value = Encoding.Unicode.GetBytes($"{{{first}}}\0{{{second}}}\0\0");

        Assert.Equal(new[] { first, second }, UsbDongleTransport.ParseGuids(value));
        Assert.Empty(UsbDongleTransport.ParseGuids(Encoding.Unicode.GetBytes("not a guid\0")));
    }

    private static byte[] AudioPayload(int decodeType, float volume, int audioType, byte[] rest)
    {
        var payload = new byte[DongleAudio.HeaderLength + rest.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, decodeType);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), volume);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), audioType);
        rest.CopyTo(payload, DongleAudio.HeaderLength);
        return payload;
    }

    private static string? FileName(DongleMessage message)
    {
        if (message.Type is not DongleMessageType.SendFile)
        {
            return null;
        }

        var span = message.Payload.Span;
        var length = BinaryPrimitives.ReadInt32LittleEndian(span);
        return Encoding.ASCII.GetString(span.Slice(4, length - 1));
    }

    private static byte[] FileContent(DongleMessage message)
    {
        var span = message.Payload.Span;
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(span);
        var length = BinaryPrimitives.ReadInt32LittleEndian(span[(4 + nameLength)..]);
        return span.Slice(8 + nameLength, length).ToArray();
    }
}
