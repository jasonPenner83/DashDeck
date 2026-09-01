using System.Diagnostics;
using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Simulator;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// The plan is a promise. If the loop cannot execute it, every component silently runs
/// slower than the arbiter believes, and the arbiter's whole accounting is fiction.
/// </summary>
public class PollingThroughputTests
{
    [Fact]
    public async Task Loop_achieves_the_planned_rate_when_the_adapter_is_fast_enough()
    {
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.HighwayCruise),
            faults: new SyntheticFaults(LatencyMs: 5, DropProbability: 0, SupportsMsCan: true));

        await using var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load())
        {
            Quality = SignalQuality.Simulated,
        };

        await service.StartAsync(TestCancellation.Token);

        // 12 Hz of demand against an adapter good for ~200/sec. There is no excuse for
        // falling short here.
        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 4);
        using var rpm = service.Bus.Require("engine.rpm", SignalPriority.High, 4);
        using var load = service.Bus.Require("engine.load", SignalPriority.Normal, 2);
        using var fuel = service.Bus.Require("engine.fuelRate", SignalPriority.Normal, 2);

        var before = transport.RequestCount;
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(4), TestCancellation.Token);
        watch.Stop();

        var achieved = (transport.RequestCount - before) / watch.Elapsed.TotalSeconds;
        var planned = service.Arbiter.CurrentPlan.AllocatedHz;

        Assert.True(
            achieved >= planned * 0.85,
            $"planned {planned:0.#} Hz but achieved only {achieved:0.#} Hz — the loop is not " +
            "executing its own plan");
    }
}

public class RealisticLatencyThroughputTests
{
    [Fact]
    public async Task Loop_keeps_up_at_realistic_adapter_latency()
    {
        // Mirrors the debug console's demand exactly: 12.8 Hz against a 60 ms adapter,
        // which needs 77% of the adapter's time. There is headroom, so falling far short
        // means the loop is wasting the budget rather than the hardware being the limit.
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.ColdStartCity),
            faults: new SyntheticFaults(LatencyMs: 60, DropProbability: 0, SupportsMsCan: true));

        await using var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load());
        await service.StartAsync(TestCancellation.Token);

        using var a = service.Bus.Require("vehicle.speed", SignalPriority.High, 4);
        using var b = service.Bus.Require("engine.rpm", SignalPriority.High, 4);
        using var c = service.Bus.Require("engine.fuelRate", SignalPriority.Normal, 2);
        using var d = service.Bus.Require("engine.load", SignalPriority.Normal, 2);
        using var e = service.Bus.Require("engine.coolantTemp", SignalPriority.Normal, 0.5);
        using var f = service.Bus.Require("fuel.levelPercent", SignalPriority.Low, 0.2);
        using var g = service.Bus.Require("ambient.airTemp", SignalPriority.Low, 0.1);

        var before = transport.RequestCount;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(6), TestCancellation.Token);
        watch.Stop();

        var achieved = (transport.RequestCount - before) / watch.Elapsed.TotalSeconds;
        var planned = service.Arbiter.CurrentPlan.AllocatedHz;

        Assert.True(
            achieved >= planned * 0.8,
            $"planned {planned:0.#} Hz, achieved {achieved:0.#} Hz " +
            $"(measured ceiling {service.MeasuredRequestsPerSecond:0.#} Hz)");
    }
}
