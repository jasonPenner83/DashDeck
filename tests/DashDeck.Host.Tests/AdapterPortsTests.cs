using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;

namespace DashDeck.Host.Tests;

/// <summary>
/// Settings ▸ Vehicle's OBD-II adapter: the list of tested ports, and choosing one (ADR-0034).
/// </summary>
public sealed class AdapterPortsTests
{
    private sealed class FakeStatus : IAdapterStatus
    {
        public bool? Simulated { get; set; }

        public bool IsSimulated => Simulated ?? LiveAdapter is null;

        public AdapterLocation? LiveAdapter { get; set; }

        public string? WatchedPort { get; set; }

        public string? FallbackReason { get; set; }

        public string? LinkProblem { get; set; }

        public List<string?> Chosen { get; } = [];

        public AdapterChoice UseAdapterPort(string? port)
        {
            Chosen.Add(port);

            if (!IsSimulated)
            {
                return string.Equals(port, LiveAdapter!.Port, StringComparison.OrdinalIgnoreCase)
                    ? AdapterChoice.AlreadyConnected
                    : AdapterChoice.NextLaunch;
            }

            return string.IsNullOrWhiteSpace(port) ? AdapterChoice.Simulator : AdapterChoice.Watching;
        }
    }

    private sealed class FakeProbe(params string[] ports) : IPortProbe
    {
        public Dictionary<string, PortTestResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Tested { get; } = [];

        public IReadOnlyList<string> ListPorts() => ports;

        public async Task<PortTestResult> TestAsync(string port, int? knownBaudRate, CancellationToken ct)
        {
            lock (Tested)
            {
                Tested.Add(port);
            }

            await Task.Yield();
            return Results.TryGetValue(port, out var result)
                ? result
                : new PortTestResult(port, PortTestOutcome.NoAdapter, null, null, null, "Opened, but nothing answered.");
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private static PortTestResult Adapter(string port) =>
        new(port, PortTestOutcome.Adapter, "OBDLink EX r2.7.1", 115200, "12.4V", "OBDLink EX r2.7.1 · 115200 baud · 12.4V at the OBD port (ignition off)");

    private sealed class Rig
    {
        public FakeStatus Status { get; } = new();

        public FakeProbe Probe { get; init; } = new();

        public ManualClock Clock { get; } = new();

        public string Chosen { get; set; } = "";

        public string[] Reserved { get; set; } = [];

        public AdapterPortsViewModel Make() =>
            new(Status, Probe, Clock, () => Chosen, port => Chosen = port, () => Reserved, () => null);
    }

    private static PortRowViewModel Row(AdapterPortsViewModel vm, string port) => vm.Ports.Single(r => r.Port == port);

    [Fact]
    public async Task Every_port_is_tested_and_labelled()
    {
        var rig = new Rig { Probe = new FakeProbe("COM10", "COM3", "COM4", "COM7") };
        rig.Probe.Results["COM3"] = Adapter("COM3");
        rig.Probe.Results["COM4"] = new PortTestResult("COM4", PortTestOutcome.InUse, null, null, null, "In use by another program.");
        rig.Reserved = ["COM7"];

        var vm = rig.Make();
        await vm.TestPortsCommand.ExecuteAsync(null);

        Assert.Equal(["COM3", "COM4", "COM7", "COM10"], vm.Ports.Select(r => r.Port));   // COM3 before COM10
        Assert.Equal("ADAPTER", Row(vm, "COM3").Label);
        Assert.True(Row(vm, "COM3").IsAdapter);
        Assert.Equal("IN USE", Row(vm, "COM4").Label);
        Assert.Equal("NO ADAPTER", Row(vm, "COM10").Label);
        Assert.Equal("PHONE GPS", Row(vm, "COM7").Label);
        Assert.DoesNotContain("COM7", rig.Probe.Tested);   // the phone's GPS port is never opened
        Assert.Contains("1 adapter", vm.TestSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_port_being_read_is_shown_not_opened()
    {
        var rig = new Rig { Probe = new FakeProbe("COM3") };
        rig.Status.LiveAdapter = new AdapterLocation("COM3", 2000000, "OBDLink EX r2.7.1", Moved: false);

        var vm = rig.Make();
        await vm.TestPortsCommand.ExecuteAsync(null);

        Assert.Equal("CONNECTED", Row(vm, "COM3").Label);
        Assert.Empty(rig.Probe.Tested);
        Assert.StartsWith("LIVE — OBDLink EX r2.7.1 on COM3 @ 2000000 baud", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chosen_port_that_is_missing_is_listed_as_not_here()
    {
        var rig = new Rig { Probe = new FakeProbe("COM4"), Chosen = "COM3" };

        var vm = rig.Make();
        await vm.TestPortsCommand.ExecuteAsync(null);

        Assert.Equal("NOT HERE", Row(vm, "COM3").Label);
        Assert.True(Row(vm, "COM3").IsSelected);
        Assert.DoesNotContain("COM3", rig.Probe.Tested);
    }

    [Fact]
    public async Task Choosing_an_adapter_while_simulated_applies_without_a_restart()
    {
        var rig = new Rig { Probe = new FakeProbe("COM3") };
        rig.Probe.Results["COM3"] = Adapter("COM3");

        var vm = rig.Make();
        await vm.TestPortsCommand.ExecuteAsync(null);
        vm.ChooseCommand.Execute(Row(vm, "COM3"));

        Assert.Equal("COM3", rig.Chosen);
        Assert.Equal(["COM3"], rig.Status.Chosen);
        Assert.True(Row(vm, "COM3").IsSelected);
        Assert.False(vm.RestartNeeded);
        Assert.Contains("no restart", vm.ChoiceMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_another_port_while_live_waits_for_the_next_launch()
    {
        var rig = new Rig { Probe = new FakeProbe("COM3", "COM5") };
        rig.Status.LiveAdapter = new AdapterLocation("COM3", 115200, "ELM327 v1.5", Moved: false);

        var vm = rig.Make();
        await vm.TestPortsCommand.ExecuteAsync(null);
        vm.ChooseCommand.Execute(Row(vm, "COM5"));

        Assert.True(vm.RestartNeeded);
        Assert.Contains("next launch", vm.ChoiceMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_simulator_can_be_chosen()
    {
        var rig = new Rig { Chosen = "COM3" };
        var vm = rig.Make();

        vm.UseSimulatorCommand.Execute(null);

        Assert.Equal("", rig.Chosen);
        Assert.Equal([null], rig.Status.Chosen);
    }

    [Fact]
    public void Says_when_it_is_watching_for_the_adapter()
    {
        var rig = new Rig();
        rig.Status.WatchedPort = "COM3";
        rig.Status.FallbackReason = "COM3 isn't there — is the adapter plugged in? — switches to live when it answers";

        var vm = rig.Make();

        Assert.StartsWith("SIMULATED — COM3 isn't there", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(SignalQuality.Stale, vm.StatusQuality);
    }

    [Fact]
    public async Task Opening_the_section_again_soon_does_not_test_again()
    {
        var rig = new Rig { Probe = new FakeProbe("COM3") };
        var vm = rig.Make();

        await vm.TestIfStaleAsync();
        await vm.TestIfStaleAsync();
        Assert.Single(rig.Probe.Tested);

        rig.Clock.UtcNow += AdapterPortsViewModel.Freshness;
        await vm.TestIfStaleAsync();
        Assert.Equal(2, rig.Probe.Tested.Count);
    }

    [Fact]
    public void A_dropped_live_adapter_reads_as_reconnecting_not_as_unchosen()
    {
        var rig = new Rig();
        rig.Status.Simulated = false;   // live, but the cable is out

        var vm = rig.Make();

        Assert.Contains("reconnecting", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(SignalQuality.Stale, vm.StatusQuality);
    }
}
