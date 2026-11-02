using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Link;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Simulated data off (ADR-0082): with no adapter, the pipeline runs and reads nothing, writes
/// nothing off, and goes live the moment the adapter answers.
/// </summary>
public sealed class NoAdapterTests
{
    [Fact]
    public async Task Setup_commands_are_accepted_and_questions_to_the_truck_are_not()
    {
        var link = new NoAdapterTransport();

        Assert.Equal(TransportState.Connected, link.State);
        Assert.Equal("OK\r\r>", await link.ExchangeAsync("ATE0", TestCancellation.Token));
        Assert.Equal("OK\r\r>", await link.ExchangeAsync("STP53", TestCancellation.Token));
        Assert.StartsWith(NoAdapterTransport.Identity, await link.ExchangeAsync("ATI", TestCancellation.Token), StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(() => link.ExchangeAsync("010C", TestCancellation.Token));
    }

    [Fact]
    public async Task Nothing_is_read_and_nothing_is_written_off()
    {
        var adapter = new ElmAdapter(new NoAdapterTransport());

        await using var service = new VehicleService(adapter, TestCatalog.Load()) { Quality = SignalQuality.Unavailable };
        await service.StartAsync(TestCancellation.Token);

        using var rpm = service.Bus.Require("engine.rpm", SignalPriority.High, 10);
        using var economy = service.Bus.Require("fuel.economy", SignalPriority.Normal, 2);
        using var tyre = service.Bus.Require("tire.frontLeft.pressure", SignalPriority.Normal, 2);

        // Long enough for many more refusals than retire a signal that answers NO DATA.
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestCancellation.Token);

        Assert.Equal(SignalQuality.Unavailable, service.Bus.Current("engine.rpm").Quality);
        Assert.Equal(SignalPollStatus.Asked, service.StatusOf("engine.rpm"));
        Assert.Equal(SignalQuality.Unavailable, service.Bus.Current("fuel.economy").Quality);
        Assert.Equal(SignalQuality.Unavailable, service.Bus.Current("tire.frontLeft.pressure").Quality);
    }

    [Fact]
    public async Task The_dash_goes_live_when_the_adapter_answers()
    {
        var truck = new SyntheticTransport(new SimulatedF150(Drives.HighwayCruise), faults: SyntheticFaults.Perfect);
        var switchable = new SwitchableTransport(new NoAdapterTransport());
        var adapter = new ElmAdapter(switchable);

        await using var service = new VehicleService(adapter, TestCatalog.Load()) { Quality = SignalQuality.Unavailable };
        await service.StartAsync(TestCancellation.Token);
        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 10);

        await using var failover = new AdapterFailover(service, switchable, async _ =>
        {
            await truck.ConnectAsync(CancellationToken.None);
            return truck;
        }, TimeSpan.FromMilliseconds(10));

        failover.Start();

        for (var i = 0; i < 250 && service.Bus.Current("vehicle.speed").Quality != SignalQuality.Live; i++)
        {
            await Task.Delay(20, TestCancellation.Token);
        }

        Assert.True(failover.IsLive);
        Assert.Equal(SignalQuality.Live, service.Bus.Current("vehicle.speed").Quality);
    }
}
