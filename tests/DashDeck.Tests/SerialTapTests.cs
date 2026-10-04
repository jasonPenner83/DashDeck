using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using DashDeck.Abstractions;
using DashDeck.Vehicle.Tap;

namespace DashDeck.Tests;

/// <summary>
/// The serial tap: FORScan talks to the adapter through it over TCP, and every line is recorded.
/// </summary>
public sealed class SerialTapTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static (TapRecorder Recorder, List<string> Lines, FixedClock Clock) Recorder()
    {
        var lines = new List<string>();
        var clock = new FixedClock(Noon);
        return (new TapRecorder(clock, lines.Add), lines, clock);
    }

    private static string Body(string line) => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..];

    [Fact]
    public void A_command_is_one_line_at_its_carriage_return()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.ToAdapter, "ATZ\r"u8);

        Assert.Equal([">>  ATZ"], lines.Select(Body));
    }

    [Fact]
    public void An_answer_split_across_reads_is_put_back_together_and_ends_at_the_prompt()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.FromAdapter, "41 0C"u8);
        recorder.Add(TapDirection.FromAdapter, " 1A F8\r"u8);
        recorder.Add(TapDirection.FromAdapter, "\r>"u8);

        Assert.Equal(["<<  41 0C 1A F8", "<<  >"], lines.Select(Body));
    }

    [Fact]
    public void Several_answer_lines_are_kept_apart()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.FromAdapter, "7E8 06 41 00 BE 3F A8 13\r7E9 06 41 00 98 18 80 11\r\r>"u8);

        Assert.Equal(
            ["<<  7E8 06 41 00 BE 3F A8 13", "<<  7E9 06 41 00 98 18 80 11", "<<  >"],
            lines.Select(Body));
    }

    [Fact]
    public void Linefeeds_after_carriage_returns_do_not_make_blank_lines()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.FromAdapter, "OK\r\n\r\n>"u8);

        Assert.Equal(["<<  OK", "<<  >"], lines.Select(Body));
    }

    [Fact]
    public void A_prompt_in_a_command_is_just_a_character()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.ToAdapter, "STFAP 7E8,>\r"u8);

        Assert.Equal([">>  STFAP 7E8,>"], lines.Select(Body));
    }

    [Fact]
    public void Unprintable_bytes_are_shown_as_hex()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.FromAdapter, [0x00, (byte)'A', 0xFF, (byte)'\r']);

        Assert.Equal([@"<<  \x00A\xFF"], lines.Select(Body));
    }

    [Fact]
    public void A_line_is_stamped_with_the_time_its_first_byte_arrived()
    {
        var (recorder, lines, clock) = Recorder();

        recorder.Add(TapDirection.ToAdapter, "01"u8);
        clock.UtcNow = Noon.AddMilliseconds(250);
        recorder.Add(TapDirection.ToAdapter, "0C\r"u8);

        Assert.StartsWith(TapRecorder.Stamp(Noon), lines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_writes_a_line_that_never_ended()
    {
        var (recorder, lines, _) = Recorder();

        recorder.Add(TapDirection.ToAdapter, "ATRV"u8);
        Assert.Empty(lines);

        recorder.Flush();

        Assert.Equal([">>  ATRV"], lines.Select(Body));
    }

    [Fact]
    public void Counts_follow_what_went_through()
    {
        var (recorder, _, _) = Recorder();

        recorder.Add(TapDirection.ToAdapter, "010C\r"u8);
        recorder.Add(TapDirection.FromAdapter, "41 0C 1A F8\r\r>"u8);

        Assert.Equal(1, recorder.CommandLines);
        Assert.Equal(2, recorder.AnswerLines);
        Assert.Equal(5, recorder.BytesToAdapter);
        Assert.Equal(14, recorder.BytesFromAdapter);
    }

    // ── The bridge, end to end over a real loopback socket ────────────────────

    [Fact]
    public async Task A_program_on_tcp_talks_to_the_adapter_and_both_sides_are_recorded()
    {
        var lines = new List<string>();
        var recorder = new TapRecorder(SystemClock.Instance, l => { lock (lines) { lines.Add(l); } });
        var adapter = new FakeAdapter();
        var bridge = new TapBridge(adapter, recorder, new IPEndPoint(IPAddress.Loopback, 0));
        var started = new TaskCompletionSource<IPEndPoint>();
        bridge.Started += at => started.TrySetResult(at);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = bridge.RunAsync(cts.Token);
        var at = await started.Task.WaitAsync(cts.Token);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(at, cts.Token);
            var stream = client.GetStream();

            await stream.WriteAsync("010C\r"u8.ToArray(), cts.Token);
            var answer = await ReadUntilPromptAsync(stream, cts.Token);

            Assert.Equal("41 0C 1A F8\r\r>", answer);
        }

        Assert.Equal("010C\r", adapter.Received);

        // The disconnect note is written as the bridge notices the hang-up.
        await WaitForAsync(() => { lock (lines) { return lines.Any(l => l.Contains("disconnected", StringComparison.Ordinal)); } }, cts.Token);

        cts.Cancel();
        await run;

        string[] bodies;
        lock (lines)
        {
            bodies = [.. lines.Select(Body)];
        }

        Assert.Contains(bodies, b => b.StartsWith("--  connected", StringComparison.Ordinal));
        Assert.Contains(">>  010C", bodies);
        Assert.Contains("<<  41 0C 1A F8", bodies);
        Assert.Contains("<<  >", bodies);
    }

    [Fact]
    public async Task A_second_program_is_turned_away_while_one_is_connected()
    {
        var lines = new List<string>();
        var recorder = new TapRecorder(SystemClock.Instance, l => { lock (lines) { lines.Add(l); } });
        var bridge = new TapBridge(new FakeAdapter(), recorder, new IPEndPoint(IPAddress.Loopback, 0));
        var started = new TaskCompletionSource<IPEndPoint>();
        bridge.Started += at => started.TrySetResult(at);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = bridge.RunAsync(cts.Token);
        var at = await started.Task.WaitAsync(cts.Token);

        using var first = new TcpClient();
        await first.ConnectAsync(at, cts.Token);
        await WaitForAsync(() => { lock (lines) { return lines.Any(l => l.Contains("connected:", StringComparison.Ordinal)); } }, cts.Token);

        using var second = new TcpClient();
        await second.ConnectAsync(at, cts.Token);
        await WaitForAsync(() => { lock (lines) { return lines.Any(l => l.Contains("refused", StringComparison.Ordinal)); } }, cts.Token);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Losing_the_adapter_ends_the_tap_and_says_so()
    {
        var lines = new List<string>();
        var recorder = new TapRecorder(SystemClock.Instance, l => { lock (lines) { lines.Add(l); } });
        var bridge = new TapBridge(new UnpluggedAdapter(), recorder, new IPEndPoint(IPAddress.Loopback, 0));

        await bridge.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains(lines, l => l.Contains("adapter lost", StringComparison.Ordinal));
    }

    private sealed class UnpluggedAdapter : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The device is not connected."));
    }

    private static async Task<string> ReadUntilPromptAsync(NetworkStream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new byte[256];

        while (!text.ToString().EndsWith('>'))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(10, ct);
        }
    }

    /// <summary>An adapter that answers 010C, and reads as a serial port does: never ending.</summary>
    private sealed class FakeAdapter : Stream
    {
        private readonly Channel<byte[]> _answers = Channel.CreateUnbounded<byte[]>();
        private readonly StringBuilder _received = new();
        private byte[] _pending = [];

        public string Received
        {
            get
            {
                lock (_received)
                {
                    return _received.ToString();
                }
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.Length == 0)
            {
                _pending = await _answers.Reader.ReadAsync(cancellationToken);
            }

            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, n).CopyTo(buffer.Span);
            _pending = _pending[n..];
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_received)
            {
                _received.Append(Encoding.ASCII.GetString(buffer.Span));

                if (_received.ToString().EndsWith('\r'))
                {
                    // Split in two, as a USB read would.
                    _answers.Writer.TryWrite("41 0C 1A"u8.ToArray());
                    _answers.Writer.TryWrite(" F8\r\r>"u8.ToArray());
                }
            }

            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
