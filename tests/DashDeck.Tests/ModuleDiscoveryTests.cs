using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Asking modules by address: finding them, sweeping their identifiers, and signals that name
/// one (ADR-0035).
/// </summary>
public class ModuleDiscoveryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Remembers every command, so a test can see what went to the adapter.</summary>
    private sealed class Logging(IVehicleTransport inner) : IVehicleTransport
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

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<(ElmAdapter Adapter, Logging Wire)> AdapterAsync(SyntheticFaults? faults = null)
    {
        var wire = new Logging(new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: faults ?? SyntheticFaults.Perfect));
        var adapter = new ElmAdapter(wire);
        await adapter.InitializeAsync(TestCancellation.Token);
        wire.Commands.Clear();
        return (adapter, wire);
    }

    // ── Addresses ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x7E0, true)]
    [InlineData(0x726, true)]
    [InlineData(0x700, true)]
    [InlineData(0x7F7, true)]
    [InlineData(0x7E8, false)] // where 7E0 answers
    [InlineData(0x7DF, false)] // the broadcast
    [InlineData(0x6FF, false)]
    [InlineData(0x7F8, false)]
    public void Knows_a_module_address_from_a_reply_address(int id, bool expected) =>
        Assert.Equal(expected, PidRequest.IsModuleAddress(id));

    [Fact]
    public void The_sweep_covers_128_addresses_per_bus() =>
        Assert.Equal(128, ModuleScanner.Addresses().Count());

    // ── The parser ────────────────────────────────────────────────────────────

    [Fact]
    public void A_negative_response_is_a_refusal_with_its_code()
    {
        var request = new PidRequest(0x22, 0xF113, CanBus.Hs, 0x716);
        var result = ElmResponseParser.Parse(request, "7F2231\r\r>", At);

        Assert.Equal(PidFailure.Rejected, result.Failure);
        Assert.Equal((byte)0x31, result.NegativeCode);
        Assert.True(result.ModuleAnswered);
    }

    [Fact]
    public void Response_pending_is_skipped_for_the_answer_that_follows()
    {
        var request = new PidRequest(0x22, 0x4002, CanBus.Ms, 0x726);
        var result = ElmResponseParser.Parse(request, "7F2278\r6240025A\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x5A], result.Data);
    }

    [Fact]
    public void Two_modules_answering_the_broadcast_read_as_the_first_not_as_garbage()
    {
        // The PCM and the TCM both answer mode 01 PID 00 on many trucks. Glued together this
        // was one long malformed reply.
        var request = new PidRequest(0x01, 0x00, CanBus.Hs);
        var result = ElmResponseParser.Parse(request, "4100BE3FA813\r410098180001\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0xBE, 0x3F, 0xA8, 0x13], result.Data);
    }

    [Fact]
    public void A_multi_frame_reply_is_still_joined()
    {
        var request = new PidRequest(0x22, 0xF113, CanBus.Hs, 0x7E0);
        var result = ElmResponseParser.Parse(request, "00B\r0:62F113414243\r1:4445464748\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal("ABCDEFGH", System.Text.Encoding.ASCII.GetString(result.Data));
    }

    // ── The adapter ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Addressing_a_module_sets_the_header_and_filter_and_the_broadcast_restores_them()
    {
        var (adapter, wire) = await AdapterAsync();
        await using var _ = adapter;

        await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Hs, 0x760), TestCancellation.Token);
        Assert.Contains("ATSH760", wire.Commands);
        Assert.Contains("ATCRA768", wire.Commands);
        Assert.Contains("ATFCSH760", wire.Commands);

        wire.Commands.Clear();
        await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Hs, 0x760), TestCancellation.Token);
        Assert.DoesNotContain(wire.Commands, c => c.StartsWith("ATSH", StringComparison.Ordinal));

        wire.Commands.Clear();
        var speed = await adapter.RequestAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), TestCancellation.Token);
        Assert.Contains("ATSH7DF", wire.Commands);
        Assert.Contains("ATAR", wire.Commands);
        Assert.True(speed.IsSuccess);
    }

    [Fact]
    public async Task A_reply_address_is_refused_before_anything_is_sent()
    {
        var (adapter, wire) = await AdapterAsync();
        await using var _ = adapter;

        var response = await adapter.RequestAsync(new PidRequest(0x22, 0xF113, CanBus.Hs, 0x7E8), TestCancellation.Token);

        Assert.Equal(PidFailure.Malformed, response.Failure);
        Assert.Empty(wire.Commands);
    }

    // ── The module sweep ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_finds_the_synthetic_modules_on_both_buses()
    {
        var (adapter, _) = await AdapterAsync();
        await using var _adapter = adapter;

        var result = await ModuleScanner.ScanAsync(adapter.RequestAsync, [CanBus.Hs, CanBus.Ms], SyntheticIdentity, null, TestCancellation.Token);

        Assert.True(result.Completed);
        Assert.Empty(result.Problems);

        var hs = result.Modules.Where(m => m.Bus == CanBus.Hs).Select(m => (int)m.Address).Order();
        var ms = result.Modules.Where(m => m.Bus == CanBus.Ms).Select(m => (int)m.Address).Order();
        Assert.Equal([0x716, 0x730, 0x760, 0x7E0, 0x7E1], hs);
        Assert.Equal([0x720, 0x726, 0x733], ms);

        var pcm = result.Modules.Single(m => m.Address == 0x7E0);
        Assert.Equal("SYNTH-PCM-14C204-AA", pcm.PartNumber);

        // The gateway is there and declined to give a part number. Still found.
        var gateway = result.Modules.Single(m => m.Address == 0x716);
        Assert.Null(gateway.PartNumber);
        Assert.Equal((byte)0x31, gateway.RefusalCode);
    }

    [Fact]
    public async Task An_hs_only_adapter_says_why_ms_found_nothing()
    {
        var (adapter, _) = await AdapterAsync(SyntheticFaults.HsCanOnly with { LatencyMs = 0, DropProbability = 0 });
        await using var _adapter = adapter;

        var result = await ModuleScanner.ScanAsync(adapter.RequestAsync, [CanBus.Hs, CanBus.Ms], SyntheticIdentity, null, TestCancellation.Token);

        Assert.DoesNotContain(result.Modules, m => m.Bus == CanBus.Ms);
        Assert.True(result.Problems.ContainsKey(CanBus.Ms));
        Assert.False(result.Problems.ContainsKey(CanBus.Hs));
    }

    [Fact]
    public async Task The_sweep_reports_progress_to_the_end()
    {
        var (adapter, _) = await AdapterAsync();
        await using var _adapter = adapter;

        var reports = new List<SweepProgress>();
        await ModuleScanner.ScanAsync(adapter.RequestAsync, [CanBus.Hs], SyntheticIdentity, new SyncProgress<SweepProgress>(reports.Add), TestCancellation.Token);

        Assert.Equal(128, reports[^1].Done);
        Assert.Equal(128, reports[^1].Total);
        Assert.Equal(5, reports[^1].Found);
    }

    [Fact]
    public void A_part_number_is_text_and_a_value_is_not()
    {
        Assert.Equal("JL3A-14C204", ModuleScanner.Text("JL3A-14C204\0\0 "u8));
        Assert.Null(ModuleScanner.Text([0x01, 0x8C]));
        Assert.Null(ModuleScanner.Text([0x00, 0x00]));
    }

    // ── The identifier sweep ──────────────────────────────────────────────────

    [Fact]
    public async Task The_identifier_sweep_finds_what_a_module_answers_and_what_it_locks()
    {
        var (adapter, _) = await AdapterAsync();
        await using var _adapter = adapter;

        var result = await DidScanner.ScanAsync(adapter.RequestAsync, CanBus.Ms, 0x726, 0x4000, 0x40FF, null, TestCancellation.Token);

        Assert.Null(result.Problem);
        Assert.Equal(256, result.Asked);
        Assert.Equal([0x4001, 0x4002, 0x4003], result.Found.Select(f => (int)f.Did));
        Assert.Equal(2, result.Found.Single(f => f.Did == 0x4001).Data.Length);
        Assert.Equal((byte)0x33, result.Found.Single(f => f.Did == 0x4003).RefusalCode);
    }

    [Fact]
    public async Task The_identifier_sweep_stops_when_the_module_is_silent()
    {
        var (adapter, _) = await AdapterAsync();
        await using var _adapter = adapter;

        // Nothing lives at 750.
        var result = await DidScanner.ScanAsync(adapter.RequestAsync, CanBus.Hs, 0x750, 0x0000, 0x0FFF, null, TestCancellation.Token);

        Assert.Equal(DidScanner.SilenceLimit, result.Asked);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task The_identifier_sweep_refuses_a_range_too_wide_to_finish()
    {
        var (adapter, _) = await AdapterAsync();
        await using var _adapter = adapter;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DidScanner.ScanAsync(adapter.RequestAsync, CanBus.Hs, 0x7E0, 0x0000, 0x1000, null, TestCancellation.Token));
    }

    // ── Signals that name a module ────────────────────────────────────────────

    private static SignalDefinition BodyVoltage(string? module = "726") => new()
    {
        Id = "body.voltage",
        Name = "Body module voltage",
        Bus = CanBus.Ms,
        Mode = 0x22,
        Pid = 0x4001,
        Module = module,
        Decode = new DecodeSpec(0, 2, false, 0.1, 0, "V"),
        DefaultRateHz = 2,
        Min = 0,
        Max = 20,
    };

    [Theory]
    [InlineData("726", 0x726)]
    [InlineData("0x7e0", 0x7E0)]
    [InlineData(" 760 ", 0x760)]
    [InlineData("7E8", null)]
    [InlineData("BCM", null)]
    [InlineData("", null)]
    public void A_module_is_read_as_a_hex_address(string text, int? expected) =>
        Assert.Equal(expected, SignalDefinition.ParseModule(text));

    [Fact]
    public void A_definition_naming_a_reply_address_is_refused()
    {
        Assert.Empty(SignalCatalog.Check(BodyVoltage()));
        Assert.Contains(SignalCatalog.Check(BodyVoltage("72E")), p => p.Contains("module", StringComparison.Ordinal));
    }

    [Fact]
    public void The_module_survives_the_round_trip_through_the_user_file()
    {
        var json = SignalCatalog.ToJson([BodyVoltage()]);
        Assert.Contains("\"module\": \"726\"", json, StringComparison.Ordinal);
        Assert.Equal(BodyVoltage(), SignalCatalog.ParseList(json).Single());

        // And a broadcast signal writes no module at all.
        Assert.DoesNotContain("\"module\"", SignalCatalog.ToJson([BodyVoltage(null)]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signal_naming_a_module_is_polled_from_it()
    {
        var catalog = SignalCatalog.Overlay(TestCatalog.Load(), [BodyVoltage()]);
        var transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        await using var service = new VehicleService(new ElmAdapter(transport), catalog) { Quality = SignalQuality.Simulated };
        await service.StartAsync(TestCancellation.Token);

        using var demand = service.Bus.Require("body.voltage", SignalPriority.Normal, 2);
        using var rpm = service.Bus.Require("engine.rpm", SignalPriority.High, 4);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline &&
               !(service.Bus.Current("body.voltage").IsUsable && service.Bus.Current("engine.rpm").IsUsable))
        {
            await Task.Delay(20, TestCancellation.Token);
        }

        // Both: the module-addressed one, and the broadcast one interleaved with it.
        Assert.InRange(service.Bus.Current("body.voltage").Value, 13.5, 14.7);
        Assert.True(service.Bus.Current("engine.rpm").IsUsable);
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Names_come_from_the_users_file_then_the_standard()
    {
        var pack = new VehiclePack
        {
            Name = "test",
            Match = new VehiclePackMatch { Make = "Ford" },
            Modules = new Dictionary<string, string> { ["726"] = "BCM — body control" },
        };

        var reference = TestCatalog.Reference().With([pack]);

        Assert.Equal("BCM — body control", reference.Modules[0x726]);
        Assert.StartsWith("Engine", reference.Modules[0x7E0]);
        Assert.False(reference.Modules.ContainsKey(0x750));
    }

    [Fact]
    public void The_standard_asks_the_iso_identity_and_a_users_file_can_ask_its_makers()
    {
        var standard = TestCatalog.Reference();
        Assert.Equal(0xF187, standard.IdentityDid);
        Assert.Equal([(0xF100, 0xF1FF)], standard.IdentifierRanges.Select(r => ((int)r.First, (int)r.Last)));

        var pack = VehiclePacks.Parse("""
            { "name": "P", "match": { "make": "Ford" }, "identityDid": "F113",
              "identifierRanges": [ { "name": "DD00–DDFF", "from": "DD00", "to": "DDFF" },
                                    { "from": "F100", "to": "F1FF" } ] }
            """, "p.json");
        var yours = standard.With([pack]);

        Assert.Equal(0xF113, yours.IdentityDid);
        Assert.Equal(["F100–F1FF  IDENTITY", "DD00–DDFF"], yours.IdentifierRanges.Select(r => r.Name));
    }

    /// <summary>The identity the synthetic truck's modules answer with their part numbers.</summary>
    private const ushort SyntheticIdentity = 0xF113;

    /// <summary><see cref="Progress{T}"/> posts to a context; this reports in line, so a test sees every one.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
