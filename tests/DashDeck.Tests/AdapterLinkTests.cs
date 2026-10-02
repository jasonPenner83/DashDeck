using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Link;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// The adapter link surviving a truck: rates, renumbered ports, pulled cables, reconfiguring
/// after a reconnect, testing ports, and moving from the simulator to the truck (ADR-0034).
/// </summary>
public class AdapterLinkTests
{
    // ── A bench of fake serial ports ──────────────────────────────────────────

    /// <summary>One device on one port: what rate it listens at, what it says it is.</summary>
    private sealed class Device
    {
        public int Baud { get; set; } = 115200;

        public string Identity { get; set; } = "ELM327 v1.5";

        public string Voltage { get; set; } = "12.4V";

        public bool InUse { get; set; }

        public bool Present { get; set; } = true;
    }

    private sealed class Bench
    {
        public Dictionary<string, Device> Ports { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Port, int Rate)> Opened { get; } = [];

        public List<string> Commands { get; } = [];

        public IReadOnlyList<string> List() => [.. Ports.Where(p => p.Value.Present).Select(p => p.Key)];

        public IVehicleTransport Open(string port, int rate)
        {
            Opened.Add((port, rate));
            return new FakePort(this, port, rate);
        }
    }

    private sealed class FakePort(Bench bench, string port, int rate) : IVehicleTransport
    {
        public TransportState State { get; private set; } = TransportState.Disconnected;

        public string Description => $"{port} @ {rate}";

        public event Action<TransportState>? StateChanged;

        private Device? Device => bench.Ports.TryGetValue(port, out var d) && d.Present ? d : null;

        public Task ConnectAsync(CancellationToken ct)
        {
            if (Device is not { } device)
            {
                throw new IOException($"{port} missing", new IOException("no such port"));
            }

            if (device.InUse)
            {
                throw new IOException($"{port} busy", new UnauthorizedAccessException("access denied"));
            }

            State = TransportState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<string> ExchangeAsync(string command, CancellationToken ct)
        {
            if (Device is not { } device)
            {
                State = TransportState.Disconnected;
                StateChanged?.Invoke(State);
                throw new IOException($"{port} gone");
            }

            bench.Commands.Add(command);

            if (device.Baud != rate)
            {
                return Task.FromResult("ÿþ\u0003ç\u0081>");   // what a wrong rate sounds like
            }

            return Task.FromResult(command switch
            {
                "ATZ" => "\r\rELM327 v1.5\r\r>",
                "ATI" => $"{device.Identity}\r\r>",
                "ATRV" => $"{device.Voltage}\r\r>",
                "STP53" or "STP33" => "OK\r\r>",
                "010D" => "410D3C\r\r>",
                _ => "OK\r\r>",
            });
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Disconnected;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private static AdapterLinkTransport Link(Bench bench, string preferred = "COM3", int? baud = null, string? identity = null,
        IReadOnlyCollection<string>? reserved = null, IClock? clock = null) =>
        new(new AdapterLinkOptions
        {
            PreferredPort = preferred,
            KnownBaudRate = baud,
            KnownIdentity = identity,
            ReservedPorts = reserved ?? [],
        }, bench.Open, bench.List, clock);

    // ── Finding it ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_known_rate_on_the_chosen_port_connects_first_time()
    {
        var bench = new Bench();
        bench.Ports["COM3"] = new Device { Baud = 2000000 };

        await using var link = Link(bench, baud: 2000000);
        var found = await link.TryLocateAsync(CancellationToken.None);

        Assert.Equal(new AdapterLocation("COM3", 2000000, "ELM327 v1.5", Moved: false), found);
        Assert.Single(bench.Opened);
        Assert.Equal(TransportState.Connected, link.State);
    }

    [Fact]
    public async Task An_unknown_rate_is_found_by_asking()
    {
        var bench = new Bench();
        bench.Ports["COM3"] = new Device { Baud = 38400 };

        await using var link = Link(bench);
        var found = await link.TryLocateAsync(CancellationToken.None);

        Assert.Equal(38400, found!.BaudRate);
    }

    [Fact]
    public async Task A_renumbered_adapter_is_found_on_its_new_port()
    {
        var bench = new Bench();
        bench.Ports["COM5"] = new Device();

        await using var link = Link(bench, preferred: "COM3", identity: "ELM327 v1.5");
        var found = await link.TryLocateAsync(CancellationToken.None);

        Assert.Equal("COM5", found!.Port);
        Assert.True(found.Moved);
    }

    [Fact]
    public async Task A_different_adapter_on_another_port_is_not_adopted()
    {
        var bench = new Bench();
        bench.Ports["COM5"] = new Device { Identity = "OBDLink SX r4.2" };

        await using var link = Link(bench, preferred: "COM3", identity: "ELM327 v1.5");

        Assert.Null(await link.TryLocateAsync(CancellationToken.None));
        Assert.Contains("COM3 isn't there", link.LastProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reserved_port_is_never_opened()
    {
        var bench = new Bench();
        bench.Ports["COM7"] = new Device();   // the phone's Bluetooth GPS, say

        await using var link = Link(bench, preferred: "COM3", reserved: ["COM7"]);

        Assert.Null(await link.TryLocateAsync(CancellationToken.None));
        Assert.DoesNotContain(bench.Opened, o => o.Port == "COM7");
    }

    [Fact]
    public async Task A_port_another_program_holds_says_so()
    {
        var bench = new Bench();
        bench.Ports["COM3"] = new Device { InUse = true };

        await using var link = Link(bench);

        Assert.Null(await link.TryLocateAsync(CancellationToken.None));
        Assert.Contains("in use by another program", link.LastProblem, StringComparison.Ordinal);
        Assert.Single(bench.Opened);   // no point trying other rates on a port that won't open
    }

    // ── Keeping it ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_adapter_that_resets_to_another_rate_is_found_again()
    {
        var bench = new Bench();
        var device = bench.Ports["COM3"] = new Device { Baud = 2000000 };

        await using var link = Link(bench, baud: 2000000);
        await link.ConnectAsync(CancellationToken.None);

        device.Baud = 115200;   // power-cycled back to its factory rate

        await Assert.ThrowsAsync<IOException>(() => link.ExchangeAsync("010D", CancellationToken.None));
        Assert.Equal("410D3C\r\r>", await link.ExchangeAsync("010D", CancellationToken.None));
        Assert.Equal(115200, link.Current!.BaudRate);
    }

    [Fact]
    public async Task A_pulled_cable_is_retried_at_a_pace_not_in_a_tight_loop()
    {
        var bench = new Bench();
        var device = bench.Ports["COM3"] = new Device();
        var clock = new ManualClock();

        await using var link = Link(bench, clock: clock);
        await link.ConnectAsync(CancellationToken.None);

        device.Present = false;
        await Assert.ThrowsAsync<IOException>(() => link.ExchangeAsync("010D", CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => link.ExchangeAsync("010D", CancellationToken.None)); // looks once, fails
        var opensAfterFirstLook = bench.Opened.Count;

        await Assert.ThrowsAsync<IOException>(() => link.ExchangeAsync("010D", CancellationToken.None)); // inside the pause
        Assert.Equal(opensAfterFirstLook, bench.Opened.Count);

        device.Present = true;
        clock.UtcNow += AdapterLinkTransport.Backoff[^1];

        Assert.Equal("410D3C\r\r>", await link.ExchangeAsync("010D", CancellationToken.None));
    }

    [Fact]
    public async Task The_adapter_is_configured_again_after_a_reconnect()
    {
        var bench = new Bench();
        var device = bench.Ports["COM3"] = new Device();
        var clock = new ManualClock();

        var link = Link(bench, clock: clock);
        await using var adapter = new ElmAdapter(link);
        await adapter.InitializeAsync(CancellationToken.None);
        Assert.Equal(1, adapter.ConfigureCount);

        // A knock to the cable: the adapter power-cycles and forgets ATE0, ATS0, ATSP6 and the bus.
        device.Present = false;
        var lost = await adapter.RequestAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), CancellationToken.None);
        Assert.Equal(PidFailure.Timeout, lost.Failure);

        device.Present = true;
        clock.UtcNow += AdapterLinkTransport.Backoff[^1];
        bench.Commands.Clear();

        var back = await adapter.RequestAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), CancellationToken.None);
        var again = await adapter.RequestAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), CancellationToken.None);

        Assert.True(back.IsSuccess || again.IsSuccess);
        Assert.Equal(2, adapter.ConfigureCount);
        Assert.Contains("ATSP6", bench.Commands);
        Assert.Contains("ATS0", bench.Commands);
    }

    // ── Testing ports ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Testing_a_port_says_what_is_on_it()
    {
        var bench = new Bench();
        bench.Ports["COM3"] = new Device { Baud = 2000000, Identity = "OBDLink EX r2.7.1", Voltage = "12.3V" };
        bench.Ports["COM4"] = new Device { Identity = "u-blox NMEA" };
        bench.Ports["COM6"] = new Device { InUse = true };

        var adapter = await PortTester.TestAsync("COM3", bench.Open);
        var other = await PortTester.TestAsync("COM4", bench.Open);
        var busy = await PortTester.TestAsync("COM6", bench.Open);
        var gone = await PortTester.TestAsync("COM9", bench.Open);

        Assert.Equal(PortTestOutcome.Adapter, adapter.Outcome);
        Assert.Equal(2000000, adapter.BaudRate);
        Assert.Equal("12.3V", adapter.Voltage);
        Assert.Contains("ignition off", adapter.Detail, StringComparison.Ordinal);
        Assert.Equal(PortTestOutcome.NoAdapter, other.Outcome);
        Assert.Equal(PortTestOutcome.InUse, busy.Outcome);
        Assert.Equal(PortTestOutcome.Unavailable, gone.Outcome);
    }

    [Fact]
    public async Task Testing_sends_nothing_to_the_vehicle()
    {
        var bench = new Bench();
        bench.Ports["COM3"] = new Device();

        await PortTester.TestAsync("COM3", bench.Open);

        Assert.All(bench.Commands, c => Assert.StartsWith("AT", c, StringComparison.Ordinal));
    }

    // ── Simulator to truck ────────────────────────────────────────────────────

    [Fact]
    public async Task The_dash_moves_from_the_simulator_to_the_truck_when_the_adapter_answers()
    {
        var simulator = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        var truck = new SyntheticTransport(new SimulatedF150(Drives.HighwayCruise), faults: SyntheticFaults.Perfect);
        var switchable = new SwitchableTransport(simulator);
        var adapter = new ElmAdapter(switchable);

        await using var service = new VehicleService(adapter, TestCatalog.Load()) { Quality = SignalQuality.Simulated };
        await service.StartAsync(TestCancellation.Token);
        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 10);

        await WaitUntilAsync(() => service.Bus.Current("vehicle.speed").Quality == SignalQuality.Simulated);

        var attempts = 0;
        await using var failover = new AdapterFailover(service, switchable, async _ =>
        {
            attempts++;

            if (attempts < 3)
            {
                return null;   // still in the house
            }

            await truck.ConnectAsync(CancellationToken.None);
            return truck;
        }, TimeSpan.FromMilliseconds(10));

        var wentLive = new TaskCompletionSource();
        failover.WentLive += () => wentLive.TrySetResult();
        failover.Start();

        await wentLive.Task.WaitAsync(TestCancellation.Token);
        await WaitUntilAsync(() => service.Bus.Current("vehicle.speed").Quality == SignalQuality.Live);

        Assert.True(failover.IsLive);
        Assert.Same(truck, switchable.Current);
        Assert.True(adapter.ConfigureCount >= 2, "the new adapter was never configured");
        Assert.Equal(SignalQuality.Live, service.Bus.Current("vehicle.speed").Quality);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20, TestCancellation.Token);
        }

        Assert.True(condition(), "timed out waiting");
    }
}
