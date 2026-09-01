using DashDeck.Simulator;

namespace DashDeck.Tests;

/// <summary>
/// The simulator must misbehave realistically. A simulator that is too well-behaved
/// produces components that fail on first contact with the truck (ADR-0005).
/// </summary>
public class SimulatorTests
{
    private static SimulatedF150 Run(ScriptedDrive drive, double stepSeconds = 0.05)
    {
        var truck = new SimulatedF150(drive);
        var step = TimeSpan.FromSeconds(stepSeconds);

        for (var t = 0.0; t < drive.TotalSeconds && !truck.IsFinished; t += stepSeconds)
        {
            truck.Advance(step);
        }

        return truck;
    }

    [Fact]
    public void Engine_starts_cold_and_warms_up_over_minutes()
    {
        var truck = new SimulatedF150(Drives.ColdStartCity, ambientTempC: 4);
        Assert.Equal(4, truck.CoolantTempC, 1);

        for (var i = 0; i < 200; i++)
        {
            truck.Advance(TimeSpan.FromSeconds(0.5));
        }

        // Warm after a hundred seconds, but not instantly — a component that assumes a
        // warm engine at t=0 should fail here rather than in the truck.
        Assert.InRange(truck.CoolantTempC, 60, 95);
    }

    [Fact]
    public void Idling_burns_fuel_without_covering_distance()
    {
        var truck = Run(Drives.Idle);

        Assert.Equal(0, truck.DistanceKm, 3);
        Assert.InRange(truck.FuelUsedLitres, 0.05, 0.5);
    }

    [Fact]
    public void Highway_cruise_produces_a_plausible_economy_figure()
    {
        var truck = Run(Drives.HighwayCruise);

        Assert.InRange(truck.DistanceKm, 15, 22);

        var litresPer100Km = truck.FuelUsedLitres / truck.DistanceKm * 100;
        Assert.InRange(litresPer100Km, 8, 20);
    }

    [Fact]
    public void Towing_uphill_costs_more_fuel_per_kilometre_than_cruising()
    {
        var cruise = Run(Drives.HighwayCruise);
        var towing = Run(Drives.TowingPull);

        var cruiseRate = cruise.FuelUsedLitres / cruise.DistanceKm;
        var towingRate = towing.FuelUsedLitres / towing.DistanceKm;

        Assert.True(
            towingRate > cruiseRate * 1.5,
            $"towing {towingRate:0.###} L/km should far exceed cruising {cruiseRate:0.###} L/km");
    }

    [Fact]
    public void Drive_is_deterministic_so_fixtures_are_repeatable()
    {
        // The property that makes a scripted drive a usable test fixture at all.
        var a = Run(Drives.ColdStartCity);
        var b = Run(Drives.ColdStartCity);

        Assert.Equal(a.FuelUsedLitres, b.FuelUsedLitres, 9);
        Assert.Equal(a.DistanceKm, b.DistanceKm, 9);
    }

    [Fact]
    public void Fuel_level_falls_as_fuel_is_consumed()
    {
        var truck = new SimulatedF150(Drives.HighwayCruise);
        var startLevel = truck.FuelLevelPercent;

        for (var i = 0; i < 6000; i++)
        {
            truck.Advance(TimeSpan.FromSeconds(0.1));
        }

        Assert.True(truck.FuelLevelPercent < startLevel);
    }
}
