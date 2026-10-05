using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.Tests;

/// <summary>The ID hunter's engine: listening, and ranking what followed (ADR-0044).</summary>
public class HuntTests
{
    // ── Monitor lines ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3B3 01 02 03 04 05 06 07 08", 0x3B3u, "0102030405060708")]
    [InlineData("3B30102", 0x3B3u, "0102")]
    [InlineData("18DAF110 03 22 F1 13", 0x18DAF110u, "0322F113")]
    [InlineData("201", 0x201u, "")]
    public void A_monitor_line_reads_as_an_identifier_and_its_bytes(string line, uint id, string hex)
    {
        Assert.True(MonitorLine.TryParse(line, out var readId, out var data));
        Assert.Equal(id, readId);
        Assert.Equal(hex, Convert.ToHexString(data));
    }

    [Theory]
    [InlineData("BUFFER FULL")]
    [InlineData("STOPPED")]
    [InlineData("?")]
    [InlineData("3B3 0102 03")]
    [InlineData("3B3 01 02 03 04 05 06 07 08 09")]
    public void Anything_else_is_not_a_frame(string line) =>
        Assert.False(MonitorLine.TryParse(line, out _, out _));

    [Fact]
    public void A_full_buffer_is_an_overflow() => Assert.True(MonitorLine.IsOverflow("BUFFER FULL"));

    // ── Listening to the synthetic truck ─────────────────────────────────────

    private static SyntheticTransport Truck() =>
        new(new SimulatedF150(Drives.Idle), faults: SyntheticFaults.Perfect);

    private static async Task<List<CanFrame>> Listen(SyntheticTransport truck, CanBus bus, uint? only, int ms)
    {
        await truck.ConnectAsync(CancellationToken.None);
        var monitor = new CanMonitor(truck);
        using var cts = new CancellationTokenSource(ms);
        var frames = new List<CanFrame>();
        await foreach (var frame in monitor.ListenAsync(bus, only, cts.Token))
        {
            frames.Add(frame);
        }

        Assert.Null(monitor.Problem);
        return frames;
    }

    [Fact]
    public async Task Listening_hears_the_body_frames_on_ms_can_and_the_engine_on_hs()
    {
        var truck = Truck();

        var ms = await Listen(truck, CanBus.Ms, null, 400);
        Assert.Contains(ms, f => f.Id == 0x3B3);
        Assert.DoesNotContain(ms, f => f.Id == 0x201);

        var hs = await Listen(truck, CanBus.Hs, null, 300);
        Assert.Contains(hs, f => f.Id == 0x201);
    }

    [Fact]
    public async Task A_filter_passes_one_identifier_alone()
    {
        var frames = await Listen(Truck(), CanBus.Ms, 0x3C1, 600);

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(0x3C1u, f.Id));
    }

    [Fact]
    public async Task Listening_is_silent_and_the_adapter_answers_requests_after()
    {
        var truck = Truck();
        var log = new Recording(truck);
        await log.ConnectAsync(CancellationToken.None);
        var adapter = new ElmAdapter(log);
        await adapter.InitializeAsync(CancellationToken.None);

        using (var cts = new CancellationTokenSource(200))
        {
            await foreach (var _ in new CanMonitor(log).ListenAsync(CanBus.Ms, null, cts.Token))
            {
            }
        }

        Assert.Contains("ATCSM1", log.Commands);
        Assert.Contains("STMA", log.Commands);
        Assert.DoesNotContain(log.Commands, c => c.StartsWith("ATSH", StringComparison.Ordinal) && c != "ATSH7DF");

        await adapter.InitializeAsync(CancellationToken.None);
        var rpm = await adapter.RequestAsync(new PidRequest(0x01, 0x0C, CanBus.Hs), CancellationToken.None);
        Assert.True(rpm.IsSuccess);
    }

    /// <summary>Remembers every command sent, exchanged or streamed.</summary>
    private sealed class Recording(SyntheticTransport inner) : IStreamingTransport
    {
        public List<string> Commands { get; } = [];

        public TransportState State => inner.State;

        public string Description => inner.Description;

        public event Action<TransportState>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public Task ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);

        public Task<string> ExchangeAsync(string command, CancellationToken ct)
        {
            Commands.Add(command);
            return inner.ExchangeAsync(command, ct);
        }

        public IAsyncEnumerable<string> StreamAsync(string command, CancellationToken ct)
        {
            Commands.Add(command);
            return inner.StreamAsync(command, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task A_bus_busier_than_the_link_is_heard_in_bursts_not_once()
    {
        var truck = Truck();
        truck.MonitorBufferFrames = 10;
        await truck.ConnectAsync(CancellationToken.None);
        var monitor = new CanMonitor(truck);

        using var cts = new CancellationTokenSource(1200);
        var frames = 0;
        await foreach (var _ in monitor.ListenAsync(CanBus.Hs, null, cts.Token))
        {
            frames++;
        }

        Assert.True(monitor.Restarts >= 2, $"restarts {monitor.Restarts}");
        Assert.True(frames > 20, $"frames {frames}");
        Assert.Null(monitor.Problem);
    }

    [Fact]
    public async Task At_the_wrong_rate_pins_3_and_11_are_silent_and_requests_there_fail()
    {
        var truck = Truck();
        truck.Pins311BitRate = 500000;
        await truck.ConnectAsync(CancellationToken.None);
        var monitor = new CanMonitor(truck);

        async Task<int> Heard(int rate)
        {
            using var cts = new CancellationTokenSource(300);
            var n = 0;
            await foreach (var _ in monitor.ListenAsync(CanBus.Ms, null, cts.Token, rate))
            {
                n++;
            }

            return n;
        }

        Assert.Equal(0, await Heard(125000));
        Assert.True(await Heard(500000) > 0);

        var adapter = new ElmAdapter(truck);
        await adapter.InitializeAsync(CancellationToken.None);
        var request = new PidRequest(0x22, 0xF113, CanBus.Ms, 0x726);
        Assert.Equal(PidFailure.BusError, (await adapter.RequestAsync(request, CancellationToken.None)).Failure);

        adapter.Pins311BitRate = 500000;
        Assert.True((await adapter.RequestAsync(request, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task With_no_measured_rate_nothing_is_sent_on_pins_3_and_11()
    {
        // ADR-0052: a real vehicle with no vehicle file giving the rate is never sent anything there.
        var truck = Truck();
        var log = new Recording(truck);
        var adapter = new ElmAdapter(log) { Pins311BitRate = null };
        await adapter.InitializeAsync(CancellationToken.None);
        var before = log.Commands.Count;

        var refused = await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Ms, 0x726), CancellationToken.None);

        Assert.Equal(PidFailure.NoData, refused.Failure);
        Assert.Equal(before, log.Commands.Count);
        Assert.True((await adapter.RequestAsync(new PidRequest(0x01, 0x0C, CanBus.Hs), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Speed_and_rpm_are_read_through_the_catalog_by_name()
    {
        var truck = new SyntheticTransport(new SimulatedF150(Drives.HighwayCruise), faults: SyntheticFaults.Perfect);
        truck.Truck.Advance(TimeSpan.FromSeconds(60));
        var adapter = new ElmAdapter(truck);
        await adapter.InitializeAsync(CancellationToken.None);
        var catalog = TestCatalog.Load();

        var speed = await SignalProbe.ReadAsync(catalog, SignalProbe.Speed, adapter.RequestAsync, CancellationToken.None);
        var rpm = await SignalProbe.ReadAsync(catalog, SignalProbe.Rpm, adapter.RequestAsync, CancellationToken.None);

        Assert.True(speed > 50, $"speed {speed}");
        Assert.True(rpm > 500, $"rpm {rpm}");

        // A catalog without the signal answers nothing, rather than guessing a PID.
        var empty = SignalCatalog.FromDefinitions([]);
        Assert.Null(await SignalProbe.ReadAsync(empty, SignalProbe.Speed, adapter.RequestAsync, CancellationToken.None));
    }

    [Fact]
    public async Task The_adapter_sets_the_pins_3_and_11_rate_each_time_it_selects_that_bus()
    {
        var truck = Truck();
        truck.Pins311BitRate = 500000;
        var log = new Recording(truck);
        var adapter = new ElmAdapter(log) { Pins311BitRate = 500000 };
        await adapter.InitializeAsync(CancellationToken.None);

        Assert.True((await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Ms, 0x726), CancellationToken.None)).IsSuccess);
        var stp = log.Commands.LastIndexOf("STP53");
        Assert.Equal("STPBR500000", log.Commands[stp + 1]);
    }

    // ── Ranking a listen ──────────────────────────────────────────────────────

    private static CanFrame F(double seconds, uint id, params byte[] data) =>
        new(TimeSpan.FromSeconds(seconds), CanBus.Ms, id, data);

    /// <summary>A recording of phases, a frame every 100 ms per identifier, made from a function of the phase's state.</summary>
    private static (List<CanFrame> Frames, List<HuntPhase> Phases) Record(double[] states, Func<double, int, byte[]>[] makers, uint[] ids)
    {
        var frames = new List<CanFrame>();
        var phases = new List<HuntPhase>();
        var t = 0.0;
        var tick = 0;

        for (var p = 0; p < states.Length; p++)
        {
            var start = t;
            for (var i = 0; i < 40; i++, t += 0.1, tick++)
            {
                for (var k = 0; k < ids.Length; k++)
                {
                    frames.Add(F(t, ids[k], makers[k](states[p], tick)));
                }
            }

            phases.Add(new HuntPhase($"S{states[p]}", states[p], TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(t)));
            t += 1.0; // the person acting between holds
        }

        return (frames, phases);
    }

    [Fact]
    public void A_door_bit_is_found_and_a_counter_beside_it_is_not()
    {
        var (frames, phases) = Record(
            [0, 1, 0, 1, 0],
            [
                (s, n) => [(byte)(s > 0 ? 0x01 : 0x00), 0x40, (byte)(n & 0x0F)],
                (s, n) => [(byte)n, (byte)(n * 7)],
            ],
            [0x3B3, 0x4A0]);

        var ranked = BroadcastRanker.Rank(frames, phases);

        var top = Assert.Single(ranked);
        Assert.Equal(0x3B3u, top.Id);
        Assert.Equal(FieldKind.Bit, top.Kind);
        Assert.Equal(0, top.Byte);
        Assert.Equal(0, top.Bit);
        Assert.Equal([0, 1, 0, 1, 0], top.Values);
    }

    [Fact]
    public void Levels_are_found_as_the_narrowest_field_that_holds_them()
    {
        var (frames, phases) = Record(
            [0, 1, 2, 3, 0],
            [(s, _) => [0x00, (byte)(0x50 | (int)s)]],
            [0x3B3]);

        var top = BroadcastRanker.Rank(frames, phases)[0];

        Assert.Equal(FieldKind.LowNibble, top.Kind);
        Assert.Equal(1, top.Byte);
        Assert.Equal([0, 1, 2, 3, 0], top.Values);
        Assert.True(top.Score > 1, "a field that rises with the level ranks above one that merely differs");
    }

    [Fact]
    public void A_field_that_differs_for_the_same_state_is_not_a_candidate()
    {
        // OFF then ON then OFF, but the "OFF" reads differently the second time: something else.
        var (frames, phases) = Record(
            [0, 1, 0],
            [(s, n) => [(byte)(n < 40 ? 0 : n < 80 ? 1 : 3)]],
            [0x123]);

        Assert.Empty(BroadcastRanker.Rank(frames, phases));
    }

    [Fact]
    public void A_field_that_tells_only_some_levels_apart_still_ranks_below_one_that_tells_all()
    {
        // Three cooling levels: one frame reports the level, another only on or off.
        var (frames, phases) = Record(
            [0, -1, -2, -3, 0],
            [
                (s, _) => [0x00, (byte)(-(int)s << 4)],
                (s, _) => [(byte)(s != 0 ? 0x04 : 0x00)],
            ],
            [0x3B3, 0x2A0]);

        var ranked = BroadcastRanker.Rank(frames, phases);

        Assert.Equal(0x3B3u, ranked[0].Id);
        Assert.True(ranked[0].SeparatesAll);
        var onOff = Assert.Single(ranked, c => c.Id == 0x2A0);
        Assert.False(onOff.SeparatesAll);
        Assert.Equal(3, onOff.Separated);   // off against each of the three levels
        Assert.Equal(6, onOff.Pairs);
        Assert.Equal(2, onOff.Bit);
    }

    [Fact]
    public void A_frame_sent_only_on_change_is_carried_forward()
    {
        var phases = new List<HuntPhase>
        {
            new("CLOSED", 0, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4)),
            new("OPEN", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9)),
            new("CLOSED", 0, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14)),
        };

        List<CanFrame> frames = [F(0.1, 0x3D5, 0x00), F(4.5, 0x3D5, 0x10), F(9.5, 0x3D5, 0x00)];

        var top = Assert.Single(BroadcastRanker.Rank(frames, phases));
        Assert.True(top.CarriedForward);
        Assert.Equal(4, top.Bit);
    }

    // ── Following a reference ────────────────────────────────────────────────

    [Fact]
    public void A_temperature_that_warms_with_coolant_ranks_first_with_its_scaling()
    {
        var passes = new List<FollowPass>();
        for (var i = 0; i < 20; i++)
        {
            var coolant = 20.0 + (i * 3);
            var oil = coolant - 3;
            var trans = 15.0 + (i * 2);
            passes.Add(new FollowPass(TimeSpan.FromSeconds(i * 10), coolant, new Dictionary<ushort, byte[]>
            {
                [0x1310] = [(byte)(oil + 40)],
                [0x1E1C] = [(byte)((int)((trans + 40) * 16) >> 8), (byte)((int)((trans + 40) * 16) & 0xFF)],
                [0x2000] = [(byte)(i % 2 == 0 ? 10 : 200)],
                [0x2001] = [0x55],
            }));
        }

        var ranked = FollowRanker.Rank(passes);

        Assert.Equal(0x1310, ranked[0].Did);
        Assert.True(ranked[0].Correlation > 0.99);
        Assert.Equal("value − 40", ranked[0].Hint);
        Assert.Contains(ranked, c => c.Did == 0x1E1C && c.Reading.Length == 2 && c.Correlation > 0.99);
        Assert.DoesNotContain(ranked, c => c.Did == 0x2001);
        Assert.True(Math.Abs(ranked.Single(c => c.Did == 0x2000).Correlation) < 0.5);
    }

    [Fact]
    public void Too_few_passes_rank_nothing() =>
        Assert.Empty(FollowRanker.Rank([new FollowPass(TimeSpan.Zero, 1, new Dictionary<ushort, byte[]> { [1] = [1] })]));

    // ── Matching the cluster ──────────────────────────────────────────────────

    [Fact]
    public void Distance_to_empty_is_matched_in_kilometres_or_miles()
    {
        var answers = new Dictionary<ushort, byte[]> { [0x4201] = [0x01, 0x9C] }; // 412

        Assert.Contains(MatchRanker.Find(answers, 412, 0), h => h.Did == 0x4201 && h.Transform.ToString() == "as is");
        Assert.Contains(MatchRanker.Find(answers, 256, 0), h => h.Transform.Unit == "km→mi");
    }

    [Fact]
    public void Four_tyres_bring_their_shared_scaling_to_the_top()
    {
        var answers = new Dictionary<ushort, byte[]>
        {
            [0x4301] = [142], [0x4302] = [140], [0x4303] = [108], [0x4304] = [138],
            [0x5000] = [35], // a stray 35 that matches the front left as is
        };

        var tally = new MatchTally();
        tally.Add("FL", MatchRanker.Find(answers, 35.5, 1));
        tally.Add("FR", MatchRanker.Find(answers, 35.0, 1));
        tally.Add("RL", MatchRanker.Find(answers, 27.0, 1));
        tally.Add("RR", MatchRanker.Find(answers, 34.5, 1));

        var best = tally.Ranked("FL")[0];
        Assert.Equal(0x4301, best.Hit.Did);
        Assert.Equal("÷ 4", best.Hit.Transform.ToString());
        Assert.Equal(0x4303, tally.Ranked("RL")[0].Hit.Did);
    }

    [Theory]
    [InlineData("35", 0)]
    [InlineData("13.4", 1)]
    [InlineData("0.25", 2)]
    public void Decimals_set_the_tolerance(string typed, int decimals) =>
        Assert.Equal(decimals, MatchRanker.Decimals(typed));

    [Fact]
    public void Readings_cover_whole_bytes_and_pairs()
    {
        var all = Reading.All(4).Select(r => r.ToString()).ToList();

        Assert.Contains("u32@0", all);
        Assert.Contains("s16@2", all);
        Assert.Contains("u8@3", all);
        Assert.Equal(-2, new Reading(0, 2, true).Read([0xFF, 0xFE]));
    }
}
