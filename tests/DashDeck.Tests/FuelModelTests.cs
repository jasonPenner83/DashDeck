using DashDeck.Abstractions;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Bus;
using DashDeck.Core.Catalog;
using DashDeck.Core.Fuel;

namespace DashDeck.Tests;

/// <summary>
/// Fuel flow by speed-density, calibrated by fill-ups, and the economy and range worked out from
/// it (ADR-0030, ADR-0041).
/// </summary>
public sealed class FuelModelTests
{
    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private static readonly VehicleProfile F150 = new() { EngineDisplacementLitres = 2.7, FuelTankLitres = 136 };

    private sealed class Rig
    {
        public Rig(FuelLedger? ledger = null, VehicleProfile? profile = null)
        {
            Catalog = FuelModel.AddTo(TestCatalog.Load());
            Arbiter = new RequestArbiter(Catalog) { BudgetHz = 19 };
            Bus = new VehicleStateBus(Catalog, Arbiter, Clock);
            Model = new FuelModel(Bus, Clock, () => profile ?? F150, ledger ?? FuelLedger.Fresh, l => Saved.Add(l));
            Model.Start();
        }

        public ManualClock Clock { get; } = new();

        public SignalCatalog Catalog { get; }

        public RequestArbiter Arbiter { get; }

        public VehicleStateBus Bus { get; }

        public FuelModel Model { get; }

        public List<FuelLedger> Saved { get; } = [];

        public void Feed(string id, double value, SignalQuality quality = SignalQuality.Live) =>
            Bus.Publish(new SignalValue(id, value, "", Clock.UtcNow, quality));

        /// <summary>An engine at a steady state: slow inputs first, then the ones that step the model.</summary>
        public void Engine(double rpm, double mapKpa, double speedKmh, SignalQuality quality = SignalQuality.Live)
        {
            Feed(FuelModel.IntakeAirTemp, 30, quality);
            Feed(FuelModel.Lambda, 1, quality);
            Feed(FuelModel.Level, 50, quality);
            Feed(FuelModel.Rpm, rpm, quality);
            Feed(FuelModel.ManifoldPressure, mapKpa, quality);
            Feed(FuelModel.Speed, speedKmh, quality);
        }

        public SignalValue Read(string id) => Bus.Current(id);
    }

    [Fact]
    public void Speed_density_puts_a_warm_idle_where_a_27_litre_v6_idles()
    {
        // ~670 rpm, ~30 kPa, 30 °C, stoichiometric: a couple of litres an hour, not ten, not a tenth.
        var idle = FuelModel.SpeedDensityLitresPerHour(30, 30, 670, 1, 2.7);

        Assert.InRange(idle, 1.0, 3.0);
    }

    [Fact]
    public void Speed_density_follows_pressure_and_says_nothing_without_a_displacement()
    {
        var light = FuelModel.SpeedDensityLitresPerHour(40, 30, 2000, 1, 2.7);
        var heavy = FuelModel.SpeedDensityLitresPerHour(80, 30, 2000, 1, 2.7);

        Assert.Equal(light * 2, heavy, 6);
        Assert.Equal(0, FuelModel.SpeedDensityLitresPerHour(100, 20, 0, 1, 2.7));       // engine off
        Assert.True(double.IsNaN(FuelModel.SpeedDensityLitresPerHour(40, 30, 2000, 1, 0)));
        Assert.True(FuelModel.SpeedDensityLitresPerHour(40, 30, 2000, 0.8, 2.7) > light); // richer burns more
    }

    [Fact]
    public void Distance_to_empty_is_what_is_left_over_the_average()
    {
        Assert.Equal(136 * 0.5 / 12 * 100, FuelModel.DistanceToEmpty(50, 136, 12), 6);
        Assert.True(double.IsNaN(FuelModel.DistanceToEmpty(50, 136, double.NaN)));
    }

    [Fact]
    public void The_first_fill_up_starts_the_count_and_the_next_calibrates_it()
    {
        var (first, said) = FuelLedger.Fresh.RecordFill(60);
        Assert.True(first.HasBaseline);
        Assert.False(first.IsCalibrated);
        Assert.Contains("First fill-up", said);

        var driven = first.Add(estimatedLitres: 40, litres: 40, km: 300);
        var (second, outcome) = driven.RecordFill(44);

        Assert.True(second.IsCalibrated);
        Assert.Equal(1.1, second.Factor, 6);
        Assert.Equal(0, second.EstimatedSinceFill);
        Assert.Equal(0, second.KmSinceFill);
        Assert.Equal(44, second.LifetimeLitres);
        Assert.Equal(300, second.LifetimeKm);
        Assert.Contains("1.10", outcome);
    }

    [Fact]
    public void A_fill_up_far_off_the_estimate_is_kept_out_of_the_factor()
    {
        var ledger = FuelLedger.Fresh.RecordFill(60).Ledger.Add(40, 40, 300);

        var (after, outcome) = ledger.RecordFill(15);   // a partial fill: 0.375 of the estimate

        Assert.False(after.IsCalibrated);
        Assert.Equal(1, after.Factor);
        Assert.Contains("too far off", outcome);
    }

    [Fact]
    public void Later_fill_ups_are_weighed_by_their_litres()
    {
        var ledger = new FuelLedger { HasBaseline = true, Factor = 1.2, CalibratingFills = 3, CalibratingLitres = 150, EstimatedSinceFill = 20 };

        var (after, _) = ledger.RecordFill(20);   // observed 1.0, but only 20 L of it

        Assert.Equal(((1.2 * 150) + (1.0 * 20)) / 170, after.Factor, 6);
        Assert.Equal(4, after.CalibratingFills);
    }

    [Fact]
    public void Deriving_a_signal_costs_the_adapter_nothing()
    {
        var catalog = FuelModel.AddTo(TestCatalog.Load());
        var arbiter = new RequestArbiter(catalog) { BudgetHz = 19 };

        using var economy = arbiter.Declare(FuelModel.Economy, SignalPriority.High, 4);

        Assert.Equal(4, economy.EffectiveRateHz);
        Assert.DoesNotContain(arbiter.CurrentPlan.Entries, e => e.Signal.IsDerived);
    }

    [Fact]
    public void The_model_asks_for_its_inputs_at_low_priority()
    {
        var rig = new Rig();

        var plan = rig.Arbiter.CurrentPlan.Entries.ToDictionary(e => e.Signal.Id);

        Assert.Equal(SignalPriority.Low, plan[FuelModel.Rpm].Priority);
        Assert.Contains(FuelModel.ManifoldPressure, plan.Keys);
        Assert.Contains(FuelModel.Lambda, plan.Keys);
        Assert.True(rig.Arbiter.CurrentPlan.AllocatedHz < 6, "the standing cost must stay a small share of ~19 req/s");
    }

    [Fact]
    public void The_truck_s_own_fuel_rate_wins_when_it_gives_one()
    {
        var rig = new Rig();
        rig.Feed(FuelModel.TruckFuelRate, 3.5);

        Assert.Equal(3.5, rig.Read(FuelModel.FlowRate).Value);
        Assert.Equal((double)FuelFlowSource.Truck, rig.Read(FuelModel.FlowSourceId).Value);
    }

    [Fact]
    public void Without_a_fuel_rate_the_flow_is_the_estimate_and_says_it_is_uncalibrated()
    {
        var rig = new Rig();
        rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);

        var expected = FuelModel.SpeedDensityLitresPerHour(60, 30, 2000, 1, 2.7);
        Assert.Equal(expected, rig.Read(FuelModel.FlowRate).Value, 6);
        Assert.Equal((double)FuelFlowSource.Uncalibrated, rig.Read(FuelModel.FlowSourceId).Value);
        Assert.Equal(SignalQuality.Live, rig.Read(FuelModel.FlowRate).Quality);
        Assert.Equal(expected / 90 * 100, rig.Read(FuelModel.Economy).Value, 6);
    }

    [Fact]
    public void A_calibrated_ledger_scales_the_estimate()
    {
        var rig = new Rig(new FuelLedger { Factor = 1.25, CalibratingFills = 1, CalibratingLitres = 60, HasBaseline = true });
        rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);

        Assert.Equal(FuelModel.SpeedDensityLitresPerHour(60, 30, 2000, 1, 2.7) * 1.25, rig.Read(FuelModel.FlowRate).Value, 6);
        Assert.Equal((double)FuelFlowSource.Calibrated, rig.Read(FuelModel.FlowSourceId).Value);
    }

    [Fact]
    public void Driving_the_real_truck_counts_fuel_and_distance()
    {
        var rig = new Rig(FuelLedger.Fresh.RecordFill(60).Ledger);
        rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);

        for (var s = 0; s < 60; s++)
        {
            rig.Clock.UtcNow += TimeSpan.FromSeconds(1);
            rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);
        }

        var ledger = rig.Model.Ledger;
        Assert.Equal(1.5, ledger.KmSinceFill, 1);                         // 90 km/h for a minute
        Assert.True(ledger.EstimatedSinceFill > 0);
        Assert.Equal(ledger.EstimatedSinceFill, ledger.LitresSinceFill, 6); // uncalibrated: factor 1
    }

    [Fact]
    public void A_synthetic_drive_never_touches_the_ledger()
    {
        var start = FuelLedger.Fresh.RecordFill(60).Ledger;
        var rig = new Rig(start);

        for (var s = 0; s < 30; s++)
        {
            rig.Clock.UtcNow += TimeSpan.FromSeconds(1);
            rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90, SignalQuality.Simulated);
        }

        rig.Model.Flush();
        Assert.Equal(start, rig.Model.Ledger);
        Assert.Empty(rig.Saved);
        Assert.Equal(SignalQuality.Simulated, rig.Read(FuelModel.FlowRate).Quality);
        Assert.Equal(SignalQuality.Simulated, rig.Read(FuelModel.Range).Quality);   // range reads at a desk, marked
    }

    [Fact]
    public void A_gap_in_the_readings_is_not_counted_as_driving()
    {
        var rig = new Rig(FuelLedger.Fresh.RecordFill(60).Ledger);
        rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);

        rig.Clock.UtcNow += TimeSpan.FromMinutes(10);   // the cable was out
        rig.Engine(rpm: 2000, mapKpa: 60, speedKmh: 90);

        Assert.Equal(0, rig.Model.Ledger.KmSinceFill);
    }

    [Fact]
    public void Stopped_there_is_no_economy_figure()
    {
        var rig = new Rig();
        rig.Engine(rpm: 700, mapKpa: 30, speedKmh: 0);

        Assert.Equal(SignalQuality.Unavailable, rig.Read(FuelModel.Economy).Quality);
        Assert.True(rig.Read(FuelModel.FlowRate).Value > 0);
    }

    [Fact]
    public void Range_uses_the_average_since_the_fill_up_and_the_tank()
    {
        var ledger = new FuelLedger { HasBaseline = true, LitresSinceFill = 24, KmSinceFill = 200 };   // 12 L/100 km
        var rig = new Rig(ledger);
        rig.Engine(rpm: 700, mapKpa: 30, speedKmh: 0);

        Assert.Equal(12, rig.Read(FuelModel.EconomyAverage).Value, 6);
        Assert.Equal(136 * 0.5 / 12 * 100, rig.Read(FuelModel.Range).Value, 1);
    }
}
