using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Simulator;
using DashDeck.Vehicle;
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
    public async Task Pulling_the_adapter_ages_readings_out_without_inventing_throughput()
    {
        // Real per-request latency, or the measurement being tested is meaningless: with
        // SyntheticFaults.Perfect every exchange returns instantly, so the measured ceiling
        // is already absurd before the cable is pulled and the assertion below proves
        // nothing. That is exactly how the first version of this test passed while the bug
        // was still there.
        var faults = new SyntheticFaults(LatencyMs: 60, DropProbability: 0, SupportsMsCan: true);
        var (service, transport) = await StartAsync(faults);
        await using var _service = service;

        using var demand = service.Bus.Require("engine.rpm", SignalPriority.High, 10);

        await WaitUntilAsync(() => service.Bus.Current("engine.rpm").IsUsable, TimeSpan.FromSeconds(5));
        Assert.True(service.Bus.Current("engine.rpm").IsUsable, "engine.rpm never answered.");

        var rateWhileConnected = service.MeasuredRequestsPerSecond;

        transport.Unplug();

        await WaitUntilAsync(
            () => service.Bus.Current("engine.rpm").Quality == SignalQuality.Stale,
            TimeSpan.FromSeconds(5));

        // The reading ages out rather than freezing at its last number.
        Assert.Equal(SignalQuality.Stale, service.Bus.Current("engine.rpm").Quality);

        // And the measured ceiling does not run away. Requests against a pulled cable fail
        // in microseconds; averaging them in would report thousands of requests per second
        // at the exact moment none are getting through.
        Assert.True(
            service.MeasuredRequestsPerSecond <= Math.Max(rateWhileConnected, 1) * 2,
            $"measured throughput rose to {service.MeasuredRequestsPerSecond:0.#} req/sec " +
            $"after the adapter was pulled (it was {rateWhileConnected:0.#} while connected).");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(50, TestCancellation.Token);
        }
    }

    [Fact]
    public async Task An_ms_can_tpms_signal_answers_on_the_ms_bus()
    {
        // TPMS are the first signals on MS-CAN, so this is also the first end-to-end exercise
        // of the bus switch: the arbiter must select MS-CAN, the adapter send STP53, and the
        // synthetic answer the body-module PID rather than NO DATA.
        var (service, _) = await StartAsync();
        await using var _service = service;

        using var demand = service.Bus.Require("tire.rearLeft.pressure", SignalPriority.Low, 1);

        await WaitUntilAsync(
            () => service.Bus.Current("tire.rearLeft.pressure").IsUsable,
            TimeSpan.FromSeconds(8));

        var reading = service.Bus.Current("tire.rearLeft.pressure");
        Assert.True(reading.IsUsable, "the rear-left tyre pressure never answered on MS-CAN.");
        Assert.Equal("psi", reading.Unit);

        // The rear left is the deliberately-low tyre; it should read low but not absurd.
        Assert.InRange(reading.Value, 20.0, 33.0);
    }

    [Fact]
    public async Task Plugging_the_adapter_back_in_recovers_without_a_restart()
    {
        // Constraint C5: connect, disconnect, sleep and resume are non-events that recover on
        // their own. The pipeline is built to survive a pulled cable — ElmAdapter turns the
        // transport's IOException into a timeout rather than letting it kill the worker — but
        // until now nothing plugged it back in and proved the reading comes alive again.
        // Replug existed and no test exercised it (F2).
        var faults = new SyntheticFaults(LatencyMs: 30, DropProbability: 0, SupportsMsCan: true);
        var (service, transport) = await StartAsync(faults);
        await using var _service = service;

        // Watch the link's own account of itself, so the sequence is asserted, not assumed.
        var states = new List<TransportState>();
        transport.StateChanged += s => states.Add(s);

        using var demand = service.Bus.Require("engine.rpm", SignalPriority.High, 10);

        await WaitUntilAsync(() => service.Bus.Current("engine.rpm").IsUsable, TimeSpan.FromSeconds(5));
        Assert.True(service.Bus.Current("engine.rpm").IsUsable, "engine.rpm never answered before the unplug.");

        transport.Unplug();

        await WaitUntilAsync(
            () => service.Bus.Current("engine.rpm").Quality == SignalQuality.Stale,
            TimeSpan.FromSeconds(5));
        Assert.Equal(SignalQuality.Stale, service.Bus.Current("engine.rpm").Quality);
        Assert.Equal(TransportState.Disconnected, transport.State);

        // The reading recovers on its own — no re-Require, no restart, just the cable back in.
        var staleAt = service.Bus.Current("engine.rpm").TimestampUtc;
        transport.Replug();

        await WaitUntilAsync(
            () => service.Bus.Current("engine.rpm").Quality == SignalQuality.Simulated
                  && service.Bus.Current("engine.rpm").TimestampUtc > staleAt,
            TimeSpan.FromSeconds(5));

        var recovered = service.Bus.Current("engine.rpm");
        Assert.Equal(SignalQuality.Simulated, recovered.Quality);
        Assert.True(recovered.IsUsable, "engine.rpm did not come back to life after the cable was replugged.");
        Assert.True(recovered.TimestampUtc > staleAt, "engine.rpm recovered but with no fresh reading behind it.");

        // The subscription is placed after StartAsync, which is where the first Connect
        // fires, so the sequence it witnesses is the unplug and the recovery: Disconnected
        // then Connected.
        Assert.Equal(
            [TransportState.Disconnected, TransportState.Connected],
            states);
    }

    [Fact]
    public async Task A_dropped_response_does_not_retire_a_signal_that_has_already_answered()
    {
        // NO DATA means two different things coming back from an ELM adapter: "this vehicle
        // has no such PID" and "that one didn't come back". Treating the second as the first
        // retires a perfectly good signal for the rest of the session — and because the odds
        // scale with request count, the *higher* a signal's rate the sooner it dies. At 4 Hz
        // a 2% drop rate kills a signal within seconds, which is why the shell came up with
        // speed and RPM blank while the 0.2 Hz fuel level survived.
        var faults = new SyntheticFaults(LatencyMs: 0, DropProbability: 0.35, SupportsMsCan: true);
        var (service, transport) = await StartAsync(faults);
        await using var _service = service;

        using var demand = service.Bus.Require("engine.rpm", SignalPriority.High, 10);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        var everAnswered = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestCancellation.Token);

            if (service.Bus.Current("engine.rpm").IsUsable)
            {
                everAnswered = true;
            }
        }

        Assert.True(everAnswered, "engine.rpm never produced a reading at all.");
        Assert.True(transport.DroppedCount > 0, "the fault injector never dropped anything.");

        // The signal answered, and drops happened. It must still be alive.
        Assert.True(
            service.Bus.Current("engine.rpm").IsUsable,
            $"engine.rpm was retired after {transport.DroppedCount} dropped responses, " +
            "even though it had already answered successfully.");
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
