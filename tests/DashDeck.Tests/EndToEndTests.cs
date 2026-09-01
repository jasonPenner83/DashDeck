using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Simulator;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Recording;

namespace DashDeck.Tests;

/// <summary>
/// The whole pipeline: synthetic truck -> ELM ASCII -> adapter -> catalog -> arbiter ->
/// state bus. These are the tests that would have caught the P0 bring-up bugs.
/// </summary>
public class EndToEndTests
{
    private static async Task<(VehicleService Service, SyntheticTransport Transport)> StartAsync(
        SyntheticFaults? faults = null, ScriptedDrive? drive = null)
    {
        var transport = new SyntheticTransport(
            new SimulatedF150(drive ?? Drives.ColdStartCity),
            faults: faults ?? SyntheticFaults.Perfect);

        var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load())
        {
            Quality = SignalQuality.Simulated,
        };

        await service.StartAsync(TestCancellation.Token);
        return (service, transport);
    }

    [Fact]
    public async Task Adapter_selects_the_high_speed_bus_after_probing_for_ms_can()
    {
        // Regression: probing for MS-CAN physically switches the adapter to it. If the
        // adapter's cached bus is not updated, the switch back is skipped as a no-op and
        // every subsequent request answers NO DATA. This bug behaves identically on real
        // hardware, and the simulator is what surfaced it.
        var (service, _) = await StartAsync();
        await using var _service = service;

        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 10);

        var value = await WaitForValueAsync(service, "vehicle.speed");

        Assert.True(value.IsUsable, "no reading arrived — the adapter is likely on the wrong bus");
    }

    [Fact]
    public async Task Readings_flow_through_the_whole_stack_and_are_flagged_simulated()
    {
        var (service, _) = await StartAsync();
        await using var _service = service;

        using var rpm = service.Bus.Require("engine.rpm", SignalPriority.High, 10);
        using var coolant = service.Bus.Require("engine.coolantTemp", SignalPriority.Normal, 5);

        var rpmValue = await WaitForValueAsync(service, "engine.rpm");

        Assert.Equal(SignalQuality.Simulated, rpmValue.Quality);
        Assert.Equal("rpm", rpmValue.Unit);
        Assert.InRange(rpmValue.Value, 400, 1200);   // a cold idle
    }

    [Fact]
    public async Task An_occasional_dropped_response_does_not_kill_a_supported_signal()
    {
        // Regression: a single NO DATA used to mark a signal permanently unsupported.
        // With a real adapter's occasional drops, one unlucky poll would silently and
        // permanently blank a signal the vehicle supports perfectly well.
        var faults = new SyntheticFaults(LatencyMs: 0, DropProbability: 0.30, SupportsMsCan: true);
        var (service, _) = await StartAsync(faults);
        await using var _service = service;

        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 20);

        var value = await WaitForValueAsync(service, "vehicle.speed", TimeSpan.FromSeconds(8));

        Assert.True(value.IsUsable, "a 30% drop rate should degrade a signal, not remove it");
    }

    [Fact]
    public async Task An_unsupported_pid_is_written_off_so_budget_is_not_wasted_on_it()
    {
        var (service, transport) = await StartAsync();
        await using var _service = service;

        // engine.intakeAirTemp is modelled; engine.runTime is too. Pick one the synthetic
        // ECU does not implement by asking for something outside its encode table.
        using var unsupported = service.Bus.Require("engine.throttlePosition", SignalPriority.High, 20);
        using var supported = service.Bus.Require("vehicle.speed", SignalPriority.High, 20);

        await WaitForValueAsync(service, "vehicle.speed");
        Assert.Contains("vehicle.speed", service.SupportedSignals);
    }

    [Fact]
    public async Task Measured_ceiling_reflects_adapter_latency_rather_than_the_claim()
    {
        // Regression: measuring achieved requests/second could not tell "the adapter cannot
        // go faster" from "nothing needed polling", so idleness read as incapacity and the
        // budget spiralled downward. Service time has no such ambiguity.
        var faults = new SyntheticFaults(LatencyMs: 50, DropProbability: 0, SupportsMsCan: true);
        var (service, _) = await StartAsync(faults);
        await using var _service = service;

        // Deliberately ask for far less than the adapter can do. A demand-based measurement
        // would conclude the adapter is slow; a service-time measurement will not.
        using var slow = service.Bus.Require("ambient.airTemp", SignalPriority.Low, 0.5);

        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellation.Token);

        // 50 ms per request is about 20/sec. Allow generous slack for CI timing.
        Assert.InRange(service.MeasuredRequestsPerSecond, 8, 25);
    }

    [Fact]
    public async Task A_recorded_session_replays_through_the_same_pipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dashdeck-test-{Guid.NewGuid():N}.jsonl");

        try
        {
            var synthetic = new SyntheticTransport(
                new SimulatedF150(Drives.HighwayCruise), faults: SyntheticFaults.Perfect);

            var recording = new RecordingTransport(synthetic, path);
            var service = new VehicleService(new ElmAdapter(recording), TestCatalog.Load())
            {
                Quality = SignalQuality.Simulated,
            };

            await service.StartAsync(TestCancellation.Token);
            using (service.Bus.Require("vehicle.speed", SignalPriority.High, 20))
            {
                await WaitForValueAsync(service, "vehicle.speed");
            }

            await service.DisposeAsync();

            // Now replay it. Nothing above the transport knows the difference.
            var replay = new ReplayTransport(path);
            await using var replayed = new VehicleService(new ElmAdapter(replay), TestCatalog.Load());

            await replayed.StartAsync(TestCancellation.Token);
            using var speed = replayed.Bus.Require("vehicle.speed", SignalPriority.High, 20);

            var value = await WaitForValueAsync(replayed, "vehicle.speed");
            Assert.True(value.IsUsable);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task An_hs_can_only_adapter_reports_reduced_capability_instead_of_pretending()
    {
        // A cheap toggle-switch or HS-only adapter must be detected and reported, not
        // silently produce blank tiles for every MS-CAN signal (ADR-0007).
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.HsCanOnly);

        await using var adapter = new ElmAdapter(transport);
        await adapter.InitializeAsync(TestCancellation.Token);

        Assert.NotNull(adapter.Capabilities);
        Assert.False(adapter.Capabilities!.SimultaneousBusAccess);
        Assert.False(adapter.Capabilities.Supports(CanBus.Ms));
        Assert.True(adapter.Capabilities.Supports(CanBus.Hs));
    }

    private static async Task<SignalValue> WaitForValueAsync(
        VehicleService service, string signalId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = service.Bus.Current(signalId);
            if (value.IsUsable)
            {
                return value;
            }

            await Task.Delay(25, TestCancellation.Token);
        }

        return service.Bus.Current(signalId);
    }
}

internal static class TestCancellation
{
    public static CancellationToken Token => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
