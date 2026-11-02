using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Calculated;
using DashDeck.Core.Catalog;
using DashDeck.Simulator;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Signals the dash works out itself, from the formula the catalog writes (ADR-0081): fuel flow by
/// speed density, and instant economy from it — L/100km moving, L/h stopped.
/// </summary>
public sealed class CalculatedSignalTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly CalculationFacts TwoSevenLitre = new(2.7);

    private static readonly SignalCatalog Catalog = TestCatalog.Load();

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

    private static SignalDefinition? Find(string id) => Catalog.TryGet(id, out var d) ? d : null;

    private static SignalValue Economy(Dictionary<string, double> values, CalculationFacts? facts = null, SignalQuality quality = SignalQuality.Live) =>
        Calculations.Calculate(Catalog["fuel.economy"], Find, Read(values, quality), facts ?? TwoSevenLitre);

    private static SignalValue Flow(Dictionary<string, double> values, CalculationFacts? facts = null) =>
        Calculations.Calculate(Catalog["fuel.flowEstimate"], Find, Read(values), facts ?? TwoSevenLitre);

    /// <summary>What the first version worked out in code, before the formula moved into the catalog.</summary>
    private static double WhatTheCodeDid(double map, double iat, double rpm, double lambda, double trim, double displacement, double correction)
    {
        if (lambda > 1.5)
        {
            return 0;
        }

        var density = map * 1000 / (287.05 * (iat + 273.15));
        var swept = displacement / 1000 * rpm / 120;
        var air = density * swept * 0.85 * 1000;
        var fuel = air / (14.7 * lambda) * (1 + trim / 100);
        return Math.Max(0, fuel * 3600 / 745 * correction);
    }

    [Theory]
    [InlineData(40, 36, 674.25, 1.0, 0, 0, 1.0)]
    [InlineData(40, 36, 674.25, 1.0, 3.125, 1.5625, 1.0)]
    [InlineData(185, 48, 4200, 0.82, -1.5, 2.3, 1.0)]
    [InlineData(95, 20, 1800, 1.0, 0, 0, 0.9)]
    [InlineData(25, 30, 2400, 1.99, 0, 0, 1.0)]
    public void The_formula_in_the_catalog_gives_what_the_code_did(double map, double iat, double rpm, double lambda, double stft, double ltft, double correction)
    {
        var values = new Dictionary<string, double>
        {
            ["engine.intakeManifoldPressure"] = map,
            ["engine.intakeAirTemp"] = iat,
            ["engine.rpm"] = rpm,
            ["fuel.commandedLambda"] = lambda,
            ["fuel.shortTermTrimB1"] = stft,
            ["fuel.longTermTrimB1"] = ltft,
        };

        var flow = Flow(values, new CalculationFacts(2.7, correction));

        Assert.Equal(WhatTheCodeDid(map, iat, rpm, lambda, stft + ltft, 2.7, correction), flow.Value, 9);
    }

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
        Assert.Equal(flow / 50 * 100, economy.Value, 9);
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

        Assert.Equal(99.9, Economy(values).Value);
    }

    [Fact]
    public void Trims_add_fuel()
    {
        var plain = Flow(Idle()).Value;

        var trimmed = Idle();
        trimmed["fuel.shortTermTrimB1"] = 5;
        trimmed["fuel.longTermTrimB1"] = 5;

        Assert.Equal(plain * 1.10, Flow(trimmed).Value, 9);
    }

    [Fact]
    public void Running_rich_burns_more_and_the_fuel_cut_burns_none()
    {
        var plain = Flow(Idle()).Value;

        var rich = Idle();
        rich["fuel.commandedLambda"] = 0.8;
        Assert.Equal(plain / 0.8, Flow(rich).Value, 9);

        var coasting = Idle();
        coasting["fuel.commandedLambda"] = 1.99;
        Assert.Equal(0, Flow(coasting).Value);
    }

    [Fact]
    public void The_correction_scales_it_and_is_kept_in_range()
    {
        var plain = Flow(Idle()).Value;

        Assert.Equal(plain * 1.2, Flow(Idle(), new CalculationFacts(2.7, 1.2)).Value, 9);
        Assert.Equal(plain * CalculationFacts.MaximumCorrection, Flow(Idle(), new CalculationFacts(2.7, 9)).Value, 9);
        Assert.Equal(plain * CalculationFacts.MinimumCorrection, Flow(Idle(), new CalculationFacts(2.7, 0.01)).Value, 9);
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
        var economy = Economy(Idle(), CalculationFacts.Unknown);

        Assert.Equal(SignalQuality.Unavailable, economy.Quality);
        Assert.Equal("L/100km", economy.Unit);
    }

    [Theory]
    [InlineData(SignalQuality.Simulated)]
    [InlineData(SignalQuality.Stale)]
    public void It_is_no_more_trustworthy_than_its_inputs(SignalQuality quality) =>
        Assert.Equal(quality, Economy(Idle(), quality: quality).Quality);

    [Fact]
    public void The_working_says_what_went_in_and_what_came_out()
    {
        var working = Calculations.Explain(Catalog["fuel.flowEstimate"], Find, Read(Idle()), TwoSevenLitre).ToString();

        Assert.Contains("map 40", working);
        Assert.Contains("rpm 674.25", working);
        Assert.Contains("lambda 1 (default)", working);
        Assert.Contains("ve 0.85", working);
        Assert.Contains("displacement 2.7", working);
        Assert.EndsWith("→  1.91 L/h", working);
    }

    [Fact]
    public void The_working_of_an_input_with_no_reading_says_so()
    {
        var values = Idle();
        values.Remove("engine.rpm");

        var working = Calculations.Explain(Catalog["fuel.flowEstimate"], Find, Read(values), TwoSevenLitre).ToString();

        Assert.Contains("rpm —", working);
        Assert.EndsWith("no reading", working);
    }

    [Fact]
    public void A_layout_that_says_litres_per_hundred_shows_litres_per_hour_when_the_reading_does()
    {
        Assert.Equal("L/h", Calculations.UnitShown("L/100km", "L/h"));
        Assert.Equal("L/100km", Calculations.UnitShown("L/100km", "L/100km"));
        Assert.Equal("°", Calculations.UnitShown("°", "°C"));
    }

    [Fact]
    public void The_shipped_economy_is_worked_out_in_the_open()
    {
        var economy = Catalog["fuel.economy"];

        Assert.NotNull(economy.Calculate);
        Assert.False(economy.Placeholder);
        Assert.False(economy.HasRequest);
        Assert.Equal("L/100km", economy.Decode.Unit);
        Assert.Equal("fuel.flowEstimate", economy.Calculate!.Inputs["flow"].Signal);
    }

    // ── Written by the person ─────────────────────────────────────────────────

    private const string Boost = """
        [{
          "id": "engine.boost", "name": "Boost", "category": "Engine",
          "calculate": {
            "unit": "kPa",
            "inputs": { "map": "engine.intakeManifoldPressure", "baro": { "signal": "engine.barometricPressure", "default": 101.3 } },
            "formula": "map - baro"
          }
        }]
        """;

    [Fact]
    public void A_new_calculation_can_be_written_in_the_signal_file()
    {
        var boost = Assert.Single(SignalCatalog.ParseList(Boost));
        var merged = SignalCatalog.Overlay(Catalog, [boost]);

        var values = new Dictionary<string, double> { ["engine.intakeManifoldPressure"] = 180, ["engine.barometricPressure"] = 98 };
        var reading = Calculations.Calculate(merged["engine.boost"], id => merged.TryGet(id, out var d) ? d : null, Read(values), CalculationFacts.Unknown);

        // Needs no engine size, so it reads without one.
        Assert.Equal(82, reading.Value, 9);
        Assert.Equal("kPa", reading.Unit);
    }

    [Fact]
    public void A_calculation_reads_back_as_it_was_written()
    {
        var written = SignalCatalog.ToJson([Catalog["fuel.flowEstimate"], Catalog["fuel.economy"]]);
        var back = SignalCatalog.ParseList(written);

        Assert.DoesNotContain("\"decode\"", written);
        Assert.Contains("\"default\": 1", written);

        foreach (var d in back)
        {
            var original = Catalog[d.Id];
            Assert.Equal(original.Calculate!.Formulas, d.Calculate!.Formulas);
            Assert.Equal(original.Calculate.Inputs.OrderBy(i => i.Key), d.Calculate.Inputs.OrderBy(i => i.Key));
            Assert.Equal(original.Calculate.Constants?.OrderBy(i => i.Key), d.Calculate.Constants?.OrderBy(i => i.Key));
            Assert.Equal(original.Decode, d.Decode);
        }
    }

    [Theory]
    [InlineData("\"formula\": \"map - bar0\"", "'bar0'")]
    [InlineData("\"formula\": \"map -\"", "ends where")]
    [InlineData("\"formula\": \"sqrt(map)\"", "'sqrt' is not a function")]
    [InlineData("\"formula\": \"map\", \"cases\": [ { \"formula\": \"map\" } ]", "one of the two")]
    [InlineData("\"cases\": [ { \"when\": \"map > 1\", \"formula\": \"map\" } ]", "last case")]
    [InlineData("\"constants\": { \"map\": 1 }, \"formula\": \"map\"", "given twice")]
    [InlineData("\"constants\": { \"displacement\": 3 }, \"formula\": \"map\"", "given twice")]
    public void A_calculation_written_wrong_is_refused_with_the_reason(string body, string reason)
    {
        var json = $$"""
            [{ "id": "x.y", "name": "X", "calculate": { "inputs": { "map": "engine.intakeManifoldPressure" }, {{body}} } }]
            """;

        var problems = SignalCatalog.Check(Assert.Single(SignalCatalog.ParseList(json)));

        Assert.Contains(problems, p => p.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_calculation_that_reads_itself_is_refused()
    {
        var json = """
            [
              { "id": "a.a", "name": "A", "calculate": { "inputs": { "b": "b.b" }, "formula": "b" } },
              { "id": "b.b", "name": "B", "calculate": { "inputs": { "a": "a.a" }, "formula": "a" } }
            ]
            """;

        var ex = Assert.Throws<InvalidDataException>(() => SignalCatalog.FromJson(json));

        Assert.Contains("reads itself", ex.Message);
    }

    [Fact]
    public void A_calculation_cannot_also_point_into_a_database()
    {
        var json = """
            [{ "id": "x.y", "name": "X", "dbc": { "file": "truck-hs.dbc", "message": "M", "signal": "S" },
               "calculate": { "inputs": { "map": "engine.intakeManifoldPressure" }, "formula": "map" } }]
            """;

        var ex = Assert.Throws<InvalidDataException>(() => SignalCatalog.ParseList(json));

        Assert.Contains("cannot also refer to a database", ex.Message);
    }

    // ── Asking for it ─────────────────────────────────────────────────────────

    [Fact]
    public void Asking_for_economy_asks_for_its_inputs_and_theirs_and_costs_nothing_itself()
    {
        var arbiter = new RequestArbiter(Catalog) { BudgetHz = 20 };

        using var demand = arbiter.Declare("fuel.economy", SignalPriority.Normal, 1);
        var plan = arbiter.CurrentPlan.Entries.ToDictionary(e => e.Signal.Id, e => e.RateHz);

        Assert.Equal(1, plan["vehicle.speed"]);

        // The fuel flow's inputs, through it.
        Assert.Equal(1, plan["engine.rpm"]);
        Assert.Equal(1, plan["engine.intakeManifoldPressure"]);
        Assert.Equal(1, plan["fuel.commandedLambda"]);
        Assert.Equal(0.5, plan["fuel.longTermTrimB1"]);

        // No faster than intake temperature is usually asked.
        Assert.Equal(0.2, plan["engine.intakeAirTemp"]);

        Assert.DoesNotContain(arbiter.CurrentPlan.Entries, e => e.Signal.IsCalculated && e.Signal.HasRequest);
        Assert.Equal(arbiter.CurrentPlan.AllocatedHz, arbiter.CurrentPlan.Entries.Where(e => !e.Signal.IsCalculated).Sum(e => e.RateHz), 9);
    }

    [Fact]
    public void Withdrawing_economy_withdraws_its_inputs()
    {
        var arbiter = new RequestArbiter(Catalog) { BudgetHz = 20 };

        arbiter.Declare("fuel.economy", SignalPriority.Normal, 1).Dispose();

        Assert.Empty(arbiter.CurrentPlan.Entries);
    }

    [Fact]
    public async Task The_simulated_truck_gives_an_economy_flagged_simulated()
    {
        var transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        await using var service = new VehicleService(new ElmAdapter(transport), Catalog)
        {
            Quality = SignalQuality.Simulated,
            SimulatedValues = transport.Truck.Reading,
            Facts = TwoSevenLitre,
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
