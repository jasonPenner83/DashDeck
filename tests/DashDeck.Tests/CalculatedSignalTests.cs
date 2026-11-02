using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Calculated;
using DashDeck.Core.Catalog;
using DashDeck.Simulator;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Signals the dash works out itself (ADR-0081): fuel flow by speed density, and instant economy
/// from it — L/100km moving, L/h stopped.
/// </summary>
public sealed class CalculatedSignalTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly FuelModel TwoSevenLitre = new(2.7);

    /// <summary>The truck at idle in the owner's Infocar log: 40 kPa, 36 °C, 674 rpm, stopped.</summary>
    private static Dictionary<string, double> Idle() => new()
    {
        ["engine.intakeManifoldPressure"] = 40,
        ["engine.intakeAirTemp"] = 36,
        ["engine.rpm"] = 674.25,
        ["vehicle.speed"] = 0,
    };

    private static Func<string, SignalValue> Read(Dictionary<string, double> values, SignalQuality quality = SignalQuality.Live) =>
        id => values.TryGetValue(id, out var v)
            ? new SignalValue(id, v, "", Noon, quality)
            : SignalValue.Missing(id);

    private static SignalValue Economy(Dictionary<string, double> values, FuelModel? model = null, SignalQuality quality = SignalQuality.Live) =>
        Calculations.Calculate("fuel.economy", Calculations.FuelEconomy, Read(values, quality), model ?? TwoSevenLitre, "L/100km");

    private static SignalValue Flow(Dictionary<string, double> values, FuelModel? model = null) =>
        Calculations.Calculate("fuel.flowEstimate", Calculations.FuelFlow, Read(values), model ?? TwoSevenLitre, "L/h");

    [Fact]
    public void Stopped_at_idle_reads_litres_per_hour()
    {
        var economy = Economy(Idle());

        Assert.Equal(Calculations.FlowUnit, economy.Unit);
        Assert.Equal(1.91, economy.Value, 2);
        Assert.Equal(SignalQuality.Live, economy.Quality);
    }

    [Fact]
    public void Moving_reads_litres_per_hundred_kilometres_from_the_same_flow()
    {
        var cruising = Idle();
        cruising["vehicle.speed"] = 50;

        var flow = Flow(cruising).Value;
        var economy = Economy(cruising);

        Assert.Equal(Calculations.EconomyUnit, economy.Unit);
        Assert.Equal(flow / 50 * 100, economy.Value, 6);
    }

    [Theory]
    [InlineData(2.9, Calculations.FlowUnit)]
    [InlineData(3.0, Calculations.EconomyUnit)]
    public void The_switch_is_at_walking_pace(double speed, string unit)
    {
        var values = Idle();
        values["vehicle.speed"] = speed;

        Assert.Equal(unit, Economy(values).Unit);
    }

    [Fact]
    public void Creeping_with_the_engine_working_is_capped_rather_than_absurd()
    {
        var values = Idle();
        values["engine.intakeManifoldPressure"] = 150;
        values["engine.rpm"] = 3000;
        values["vehicle.speed"] = 4;

        Assert.Equal(Calculations.MaximumEconomy, Economy(values).Value);
    }

    [Fact]
    public void Trims_add_fuel_averaged_over_the_banks_that_answered()
    {
        var plain = Flow(Idle()).Value;

        var trimmed = Idle();
        trimmed["fuel.shortTermTrimB1"] = 3.125;
        trimmed["fuel.longTermTrimB1"] = 1.5625;
        trimmed["fuel.shortTermTrimB2"] = 0.78125;
        trimmed["fuel.longTermTrimB2"] = 3.90625;

        Assert.Equal(plain * 1.046875, Flow(trimmed).Value, 6);

        var oneBank = Idle();
        oneBank["fuel.shortTermTrimB1"] = 5;
        oneBank["fuel.longTermTrimB1"] = 5;

        Assert.Equal(plain * 1.10, Flow(oneBank).Value, 6);
    }

    [Fact]
    public void Running_rich_burns_more_and_the_fuel_cut_burns_none()
    {
        var plain = Flow(Idle()).Value;

        var rich = Idle();
        rich["fuel.commandedLambda"] = 0.8;
        Assert.Equal(plain / 0.8, Flow(rich).Value, 6);

        var coasting = Idle();
        coasting["fuel.commandedLambda"] = 1.99;
        Assert.Equal(0, Flow(coasting).Value);
    }

    [Fact]
    public void The_correction_scales_it_and_is_kept_in_range()
    {
        var plain = Flow(Idle()).Value;

        Assert.Equal(plain * 1.2, Flow(Idle(), new FuelModel(2.7, 1.2)).Value, 6);
        Assert.Equal(plain * FuelModel.MaximumCorrection, Flow(Idle(), new FuelModel(2.7, 9)).Value, 6);
        Assert.Equal(plain * FuelModel.MinimumCorrection, Flow(Idle(), new FuelModel(2.7, 0.01)).Value, 6);
    }

    [Fact]
    public void The_engine_off_burns_nothing()
    {
        var off = Idle();
        off["engine.rpm"] = 0;

        Assert.Equal(0, Economy(off).Value);
    }

    [Fact]
    public void Without_a_required_input_there_is_no_reading_never_a_zero()
    {
        var values = Idle();
        values.Remove("engine.intakeManifoldPressure");

        var economy = Economy(values);

        Assert.Equal(SignalQuality.Unavailable, economy.Quality);
        Assert.True(double.IsNaN(economy.Value));
    }

    [Fact]
    public void Without_the_engine_size_there_is_no_reading()
    {
        var economy = Economy(Idle(), FuelModel.Unknown);

        Assert.Equal(SignalQuality.Unavailable, economy.Quality);
        Assert.Equal("L/100km", economy.Unit);
    }

    [Theory]
    [InlineData(SignalQuality.Simulated)]
    [InlineData(SignalQuality.Stale)]
    public void It_is_no_more_trustworthy_than_its_inputs(SignalQuality quality) =>
        Assert.Equal(quality, Economy(Idle(), quality: quality).Quality);

    [Fact]
    public void A_layout_that_says_litres_per_hundred_shows_litres_per_hour_when_the_reading_does()
    {
        Assert.Equal("L/h", Calculations.UnitShown("L/100km", "L/h"));
        Assert.Equal("L/100km", Calculations.UnitShown("L/100km", "L/100km"));
        Assert.Equal("°", Calculations.UnitShown("°", "°C"));
    }

    [Fact]
    public void The_shipped_economy_is_worked_out_not_waiting_for_an_id()
    {
        var economy = TestCatalog.Load()["fuel.economy"];

        Assert.Equal(Calculations.FuelEconomy, economy.Calculate);
        Assert.False(economy.Placeholder);
        Assert.False(economy.HasRequest);
    }

    [Fact]
    public void An_unknown_calculation_is_refused()
    {
        var problems = SignalCatalog.Check(new SignalDefinition
        {
            Id = "x.y",
            Name = "X",
            Calculate = "perpetualMotion",
            Decode = new DecodeSpec(0, 1, false, 1, 0, ""),
        });

        Assert.Contains(problems, p => p.Contains("perpetualMotion", StringComparison.Ordinal));
    }

    [Fact]
    public void Asking_for_economy_asks_for_its_inputs_and_costs_nothing_itself()
    {
        var arbiter = new RequestArbiter(TestCatalog.Load()) { BudgetHz = 20 };

        using var demand = arbiter.Declare("fuel.economy", SignalPriority.Normal, 1);
        var plan = arbiter.CurrentPlan.Entries.ToDictionary(e => e.Signal.Id, e => e.RateHz);

        Assert.Equal(1, plan["engine.rpm"]);
        Assert.Equal(1, plan["vehicle.speed"]);
        Assert.Equal(1, plan["engine.intakeManifoldPressure"]);
        Assert.Equal(1, plan["fuel.commandedLambda"]);

        // No faster than intake temperature is usually asked.
        Assert.Equal(0.2, plan["engine.intakeAirTemp"]);

        // Inputs that are not in the catalog are simply not asked for.
        Assert.False(plan.ContainsKey("fuel.shortTermTrimB2"));

        Assert.True(plan.ContainsKey("fuel.economy"));
        Assert.DoesNotContain(arbiter.CurrentPlan.Entries, e => e.Signal.Id == "fuel.economy" && e.Signal.HasRequest);
    }

    [Fact]
    public void Withdrawing_economy_withdraws_its_inputs()
    {
        var arbiter = new RequestArbiter(TestCatalog.Load()) { BudgetHz = 20 };

        arbiter.Declare("fuel.economy", SignalPriority.Normal, 1).Dispose();

        Assert.Empty(arbiter.CurrentPlan.Entries);
    }

    [Fact]
    public async Task The_simulated_truck_gives_an_economy_flagged_simulated()
    {
        var transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        await using var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load())
        {
            Quality = SignalQuality.Simulated,
            SimulatedValues = transport.Truck.Reading,
            Fuel = TwoSevenLitre,
        };

        await service.StartAsync(TestCancellation.Token);
        using var demand = service.Bus.Require("fuel.economy", SignalPriority.High, 2);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && !service.Bus.Current("fuel.economy").IsUsable)
        {
            await Task.Delay(50, TestCancellation.Token);
        }

        var economy = service.Bus.Current("fuel.economy");
        Assert.Equal(SignalQuality.Simulated, economy.Quality);
        Assert.True(double.IsFinite(economy.Value) && economy.Value >= 0, $"economy read {economy.Value}");
        Assert.Contains(economy.Unit, new[] { Calculations.FlowUnit, Calculations.EconomyUnit });
    }
}
