using System.Diagnostics;
using DashDeck.Abstractions;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Fast requests (ADR-0049): the engine computer's standard values asked filtered to its answer,
/// with a count of one — and everything that must stay the slow way, staying it.
/// </summary>
public sealed class FastRequestTests
{
    /// <summary>
    /// An adapter whose answers the test writes. Unknown control commands are OK; unknown
    /// requests are NO DATA. Every command is kept, so a test can see what went to the adapter.
    /// </summary>
    private sealed class Scripted(string identity = "ELM327 v1.4b") : IVehicleTransport
    {
        public Dictionary<string, Queue<string>> Replies { get; } = new(StringComparer.Ordinal);

        public List<string> Commands { get; } = [];

        public TransportState State => TransportState.Connected;

        public string Description => "scripted";

        public event Action<TransportState>? StateChanged { add { } remove { } }

        public void Answer(string command, params string[] replies)
        {
            if (!Replies.TryGetValue(command, out var queue))
            {
                Replies[command] = queue = new Queue<string>();
            }

            foreach (var reply in replies)
            {
                queue.Enqueue(reply);
            }
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> ExchangeAsync(string command, CancellationToken ct)
        {
            Commands.Add(command);

            if (Replies.TryGetValue(command, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
            }

            if (command == "ATI")
            {
                return Task.FromResult(identity + "\r\r>");
            }

            return Task.FromResult(command.StartsWith("AT", StringComparison.Ordinal) || command.StartsWith("ST", StringComparison.Ordinal)
                ? "OK\r\r>"
                : "NO DATA\r\r>");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(ElmAdapter Adapter, Scripted Wire)> AdapterAsync(bool fast = true, string identity = "ELM327 v1.4b")
    {
        var wire = new Scripted(identity);
        var adapter = new ElmAdapter(wire) { FastRequests = fast };
        await adapter.InitializeAsync(TestCancellation.Token);
        wire.Commands.Clear();
        return (adapter, wire);
    }

    private static PidRequest Rpm => new(0x01, 0x0C, CanBus.Hs);

    // ── Which adapters ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ELM327 v1.4b", true)]
    [InlineData("ELM327 v1.3a", true)]
    [InlineData("ELM327 v2.1", true)]
    [InlineData("ELM327 v1.2", false)]
    [InlineData("ELM327 v1.0", false)]
    [InlineData("STN2230 v5.6.6 (synthetic)", true)]
    [InlineData("OBDII to RS232 Interpreter", false)]
    public void Only_adapters_that_understand_a_count_get_it(string identity, bool expected) =>
        Assert.Equal(expected, ElmAdapter.SupportsResponseCount(identity));

    [Fact]
    public async Task Off_by_default_and_the_ceiling_stays_the_measured_one()
    {
        var (adapter, _) = await AdapterAsync(fast: false);

        Assert.False(adapter.FastRequestsActive);
        Assert.Equal(ElmAdapter.AssumedRequestsPerSecond, adapter.Capabilities!.MaxRequestsPerSecond);
    }

    [Fact]
    public async Task On_with_a_capable_adapter_raises_the_ceiling_the_arbiter_may_plan_up_to()
    {
        var (adapter, _) = await AdapterAsync();

        Assert.True(adapter.FastRequestsActive);
        Assert.Equal(ElmAdapter.FastRequestsPerSecond, adapter.Capabilities!.MaxRequestsPerSecond);
    }

    [Fact]
    public async Task An_old_adapter_is_left_on_the_standard_way_even_when_asked()
    {
        var (adapter, wire) = await AdapterAsync(identity: "ELM327 v1.2");
        wire.Answer("010C", "410C1AF8\r\r>");

        var response = await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.False(adapter.FastRequestsActive);
        Assert.True(response.IsSuccess);
        Assert.DoesNotContain("010C1", wire.Commands);
        Assert.DoesNotContain("ATCRA7E8", wire.Commands);
    }

    // ── The fast way ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_standard_value_is_asked_filtered_to_the_engine_computer_with_a_count_of_one()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "410C1AF8\r\r>");

        var response = await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.True(response.IsSuccess);
        Assert.Equal(new byte[] { 0x1A, 0xF8 }, response.Data);
        Assert.Equal(["ATCRA7E8", "010C1"], wire.Commands);
        Assert.Equal(1, adapter.FastCount);
    }

    [Fact]
    public async Task The_filter_is_set_once_not_before_every_request()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "410C1AF8\r\r>");
        wire.Answer("01051", "41055A\r\r>");

        await adapter.RequestAsync(Rpm, TestCancellation.Token);
        await adapter.RequestAsync(new PidRequest(0x01, 0x05, CanBus.Hs), TestCancellation.Token);
        await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.Equal(1, wire.Commands.Count(c => c == "ATCRA7E8"));
    }

    // ── What stays the slow way ───────────────────────────────────────────────

    [Theory]
    [InlineData(0x01, 0x00, CanBus.Hs, null)]     // supported-PID bitmap: several modules answer
    [InlineData(0x01, 0x20, CanBus.Hs, null)]
    [InlineData(0x09, 0x02, CanBus.Hs, null)]     // the VIN: several frames
    [InlineData(0x22, 0x1E1C, CanBus.Hs, null)]   // Ford identifiers: lengths not known yet
    [InlineData(0x01, 0x0C, CanBus.Ms, null)]     // pins 3/11
    [InlineData(0x01, 0x0C, CanBus.Hs, 0x7E0)]    // a named module
    public void Only_single_frame_standard_values_to_the_broadcast_are_eligible(int mode, int pid, CanBus bus, int? header) =>
        Assert.False(ElmAdapter.IsFastEligible(new PidRequest((byte)mode, (ushort)pid, bus, (ushort?)header)));

    [Fact]
    public void Rpm_is_eligible() => Assert.True(ElmAdapter.IsFastEligible(Rpm));

    [Fact]
    public async Task A_bitmap_after_fast_requests_clears_the_filter_so_every_module_is_heard()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "410C1AF8\r\r>");
        wire.Answer("0100", "4100BE3FA813\r4100981880 11\r\r>");

        await adapter.RequestAsync(Rpm, TestCancellation.Token);
        var bitmap = await adapter.RequestAsync(new PidRequest(0x01, 0x00, CanBus.Hs), TestCancellation.Token);

        Assert.True(bitmap.IsSuccess);
        Assert.Equal(["ATCRA7E8", "010C1", "ATAR", "0100"], wire.Commands);
    }

    [Fact]
    public async Task A_module_request_after_fast_requests_sets_its_own_filter_and_the_broadcast_one_comes_back_after()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "410C1AF8\r\r>");
        wire.Answer("22F113", "62F1134A4C33\r\r>");

        await adapter.RequestAsync(Rpm, TestCancellation.Token);
        await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Hs, 0x726), TestCancellation.Token);
        await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.Contains("ATCRA72E", wire.Commands);
        Assert.Equal("22F113", wire.Commands[wire.Commands.IndexOf("ATCRA72E") + 4]);

        // Back to the broadcast: the module's filter cleared, then the engine computer's set again.
        var back = wire.Commands.Skip(wire.Commands.IndexOf("22F113") + 1).ToList();
        Assert.Equal(["ATSH7DF", "ATAR", "ATFCSM0", "ATCRA7E8", "010C1"], back);
    }

    // ── Falling back ──────────────────────────────────────────────────────────

    [Fact]
    public async Task No_data_the_fast_way_is_asked_again_the_slow_way()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "NO DATA\r\r>");
        wire.Answer("010C", "NO DATA\r\r>");

        var response = await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.Equal(PidFailure.NoData, response.Failure);
        Assert.Equal(["ATCRA7E8", "010C1", "ATAR", "010C"], wire.Commands);
        Assert.Equal(1, adapter.FallbackCount);
    }

    [Fact]
    public async Task A_value_another_module_answers_goes_the_slow_way_for_good_after_three_in_a_row()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("01A61", "NO DATA\r\r>");
        wire.Answer("01A6", "41A600012345\r\r>");
        var odometer = new PidRequest(0x01, 0xA6, CanBus.Hs);

        for (var i = 0; i < ElmAdapter.HiddenAnswersBeforeSlow; i++)
        {
            Assert.True((await adapter.RequestAsync(odometer, TestCancellation.Token)).IsSuccess);
            Assert.Contains("01A61", wire.Commands);
            wire.Commands.Clear();
        }

        var after = await adapter.RequestAsync(odometer, TestCancellation.Token);

        Assert.True(after.IsSuccess);
        Assert.Equal(["01A6"], wire.Commands);
    }

    [Fact]
    public async Task One_dropped_answer_does_not_send_a_value_the_slow_way_for_good()
    {
        // What the truck does now and then: one answer lost, the next fine.
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "NO DATA\r\r>", "410C1AF8\r\r>");
        wire.Answer("010C", "410C1AF8\r\r>");

        await adapter.RequestAsync(Rpm, TestCancellation.Token);   // dropped fast, answered slow
        await adapter.RequestAsync(Rpm, TestCancellation.Token);   // fast again, and fine
        wire.Commands.Clear();
        await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.Equal(["010C1"], wire.Commands);
    }

    [Fact]
    public async Task A_busy_reply_alone_is_asked_again_the_slow_way_which_waits_for_the_real_answer()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "7F0178\r\r>");
        wire.Answer("010C", "7F0178\r410C1AF8\r\r>");

        var response = await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.True(response.IsSuccess);
        Assert.Equal(new byte[] { 0x1A, 0xF8 }, response.Data);
        Assert.Equal(["ATCRA7E8", "010C1", "ATAR", "010C"], wire.Commands);
    }

    [Fact]
    public async Task A_late_answer_landing_on_the_next_request_is_never_read_as_that_requests_value()
    {
        // The busy module's real rpm answer arrives while coolant is being asked for.
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("01051", "410C1AF8\r\r>");
        wire.Answer("0105", "41055A\r\r>");

        var coolant = await adapter.RequestAsync(new PidRequest(0x01, 0x05, CanBus.Hs), TestCancellation.Token);

        // Either a refusal to decode or the right value, never rpm's bytes as a temperature.
        Assert.False(coolant.IsSuccess && coolant.Data.SequenceEqual(new byte[] { 0x1A, 0xF8 }));
    }

    [Fact]
    public async Task An_adapter_that_refuses_the_count_turns_the_fast_way_off()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "?\r\r>");
        wire.Answer("010C", "410C1AF8\r\r>");

        var first = await adapter.RequestAsync(Rpm, TestCancellation.Token);
        wire.Commands.Clear();
        await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.True(first.IsSuccess);
        Assert.False(adapter.FastRequestsActive);
        Assert.Equal(["010C"], wire.Commands);
    }

    [Fact]
    public async Task A_reconfiguration_starts_from_the_adapter_reset_not_a_filter_it_no_longer_has()
    {
        var (adapter, wire) = await AdapterAsync();
        wire.Answer("010C1", "410C1AF8\r\r>");

        await adapter.RequestAsync(Rpm, TestCancellation.Token);
        adapter.FastRequests = false;
        adapter.FastRequests = true;   // a changed setting configures again: ATZ clears the filter
        wire.Commands.Clear();

        await adapter.RequestAsync(Rpm, TestCancellation.Token);

        Assert.Contains("ATZ", wire.Commands);
        Assert.Equal("ATCRA7E8", wire.Commands[wire.Commands.Count - 2]);
    }

    // ── On the synthetic truck ────────────────────────────────────────────────

    [Fact]
    public async Task On_the_synthetic_truck_fast_answers_decode_like_slow_ones()
    {
        var slowWire = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        var fastWire = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        var slow = new ElmAdapter(slowWire);
        var fast = new ElmAdapter(fastWire) { FastRequests = true };
        await slow.InitializeAsync(TestCancellation.Token);
        await fast.InitializeAsync(TestCancellation.Token);

        var request = new PidRequest(0x01, 0x05, CanBus.Hs);
        var a = await slow.RequestAsync(request, TestCancellation.Token);
        var b = await fast.RequestAsync(request, TestCancellation.Token);

        Assert.True(fast.FastRequestsActive);
        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
        Assert.Equal(a.Data.Length, b.Data.Length);
        Assert.Equal(1, fast.FastCount);
    }

    [Fact]
    public async Task On_the_synthetic_truck_fast_requests_take_its_measured_twenty_milliseconds_not_fifty_two()
    {
        var wire = new SyntheticTransport(
            new SimulatedF150(Drives.ColdStartCity),
            faults: SyntheticFaults.Realistic with { DropProbability = 0 });
        var adapter = new ElmAdapter(wire) { FastRequests = true };
        await adapter.InitializeAsync(TestCancellation.Token);
        var rpm = Rpm;

        await adapter.RequestAsync(rpm, TestCancellation.Token);   // sets the filter
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            await adapter.RequestAsync(rpm, TestCancellation.Token);
        }

        watch.Stop();

        // Ten at 20 ms is 200; ten at 52 would be 520. Generous either side for a busy CI machine.
        Assert.InRange(watch.ElapsedMilliseconds, 150, 450);
    }
}
