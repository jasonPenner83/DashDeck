using DashDeck.Abstractions;
using DashDeck.Core.Emulation;
using DashDeck.Core.Location;

namespace DashDeck.Tests;

/// <summary>What Android Automotive is told about the truck (ADR-0075 addendum).</summary>
public class CarFeedTests
{
    private static Func<string, SignalValue> Readings(params (string Id, double Value)[] live) => id =>
        live.FirstOrDefault(l => l.Id == id) is { Id: not null } found
            ? new SignalValue(id, found.Value, id == CarFeed.Speed ? "km/h" : "", DateTimeOffset.UnixEpoch, SignalQuality.Live)
            : SignalValue.Missing(id);

    [Fact]
    public void Moving_is_drive_whatever_else_is_known()
    {
        var state = CarFeed.From(Readings((CarFeed.Speed, 80)), fix: null, night: false, tankLitres: 0);

        Assert.Equal((80.0, CarGear.Drive), (state.SpeedKmh, state.Gear));
    }

    [Fact]
    public void Stopped_with_the_engine_running_is_still_drive_and_with_it_off_is_park()
    {
        Assert.Equal(CarGear.Drive, CarFeed.From(Readings((CarFeed.Speed, 0), (CarFeed.EngineRpm, 700)), null, false, 0).Gear);
        Assert.Equal(CarGear.Park, CarFeed.From(Readings((CarFeed.Speed, 0), (CarFeed.EngineRpm, 0)), null, false, 0).Gear);
        Assert.Equal(CarGear.Park, CarFeed.From(Readings((CarFeed.Speed, 0), (CarFeed.EngineRpm, 700), (CarFeed.ParkingBrake, 1)), null, false, 0).Gear);

        // Stopped, and nothing says whether it is parked: say nothing about the gear.
        Assert.Null(CarFeed.From(Readings((CarFeed.Speed, 0)), null, false, 0).Gear);
    }

    [Fact]
    public void The_truck_s_own_gear_wins_when_it_has_one()
    {
        Assert.Equal(CarGear.Reverse, CarFeed.From(Readings((CarFeed.Speed, 5), (CarFeed.Gear, 1)), null, false, 0).Gear);
        Assert.Equal(CarGear.Park, CarFeed.From(Readings((CarFeed.Speed, 0), (CarFeed.Gear, 0), (CarFeed.EngineRpm, 700)), null, false, 0).Gear);
        Assert.Equal(CarGear.Drive, CarFeed.From(Readings((CarFeed.Speed, 40), (CarFeed.Gear, 4)), null, false, 0).Gear);   // M
    }

    [Fact]
    public void Without_a_live_speed_the_gps_stands_in_and_without_either_nothing_is_said()
    {
        var fix = new LocationFix(49.9, -97.1, 62, 90, SignalQuality.Live, DateTimeOffset.UnixEpoch);

        Assert.Equal(62.0, CarFeed.From(Readings(), fix, false, 0).SpeedKmh);
        Assert.Equal(CarGear.Drive, CarFeed.From(Readings(), fix, false, 0).Gear);

        // The truck silent and the GPS still: the ignition is off, so it is parked.
        Assert.Equal(CarGear.Park, CarFeed.From(Readings(), fix with { GroundSpeedKmh = 0.8 }, false, 0).Gear);

        var none = CarFeed.From(Readings(), fix: null, night: false, tankLitres: 0);
        Assert.Null(none.SpeedKmh);
        Assert.Null(none.Gear);
    }

    [Fact]
    public void Fuel_is_litres_of_the_tank_and_only_with_a_tank_size()
    {
        Assert.Equal(68.0, CarFeed.From(Readings((CarFeed.FuelLevel, 50)), null, false, 136).FuelLitres);
        Assert.Null(CarFeed.From(Readings((CarFeed.FuelLevel, 50)), null, false, 0).FuelLitres);
    }

    [Fact]
    public void Automotive_is_told_through_its_vehicle_hal_gear_before_speed()
    {
        var (commands, told) = CarFeed.Changes(null, new CarState(SpeedKmh: 36, Gear: CarGear.Drive, Night: true), AndroidFlavour.Automotive, VhalRoute.Store);

        Assert.Equal(
        [
            "dumpsys android.hardware.automotive.vehicle.IVehicle/default --set 0x11400400 -i 8",
            "dumpsys android.hardware.automotive.vehicle.IVehicle/default --set 0x11600207 -f 10",
            "dumpsys android.hardware.automotive.vehicle.IVehicle/default --set 0x11200407 -i 1",
        ], commands);
        Assert.Equal(new CarState(SpeedKmh: 36, Gear: CarGear.Drive, Night: true), told);
    }

    [Fact]
    public void Older_images_get_the_same_as_car_service_events()
    {
        var (commands, _) = CarFeed.Changes(null, new CarState(Gear: CarGear.Park), AndroidFlavour.Automotive, VhalRoute.Event);

        Assert.Equal(["cmd car_service inject-vhal-event 0x11400400 4"], commands);
    }

    [Fact]
    public void Only_what_changed_enough_is_sent_again()
    {
        var told = new CarState(SpeedKmh: 50, Gear: CarGear.Drive, Night: false, EngineRpm: 1500);

        Assert.Empty(CarFeed.Changes(told, told with { SpeedKmh = 50.4, EngineRpm = 1520 }, AndroidFlavour.Automotive, VhalRoute.Store).Commands);
        Assert.Single(CarFeed.Changes(told, told with { SpeedKmh = 52 }, AndroidFlavour.Automotive, VhalRoute.Store).Commands);

        // Coming to a stop is told at once, however small the last step.
        var crawling = told with { SpeedKmh = 3.2 };
        Assert.Single(CarFeed.Changes(crawling, crawling with { SpeedKmh = 2.8 }, AndroidFlavour.Automotive, VhalRoute.Store).Commands);

        // Not knowing is not a change: Android keeps what it was told.
        var (none, still) = CarFeed.Changes(told, new CarState(), AndroidFlavour.Automotive, VhalRoute.Store);
        Assert.Empty(none);
        Assert.Equal(told, still);
    }

    [Fact]
    public void A_tablet_only_follows_the_dash_into_the_dark()
    {
        var (commands, told) = CarFeed.Changes(null, new CarState(SpeedKmh: 80, Gear: CarGear.Drive, Night: true), AndroidFlavour.Regular, VhalRoute.Store);

        Assert.Equal(["cmd uimode night yes"], commands);
        Assert.Empty(CarFeed.Changes(told, new CarState(Night: true), AndroidFlavour.Regular, VhalRoute.Store).Commands);
        Assert.Equal(["cmd uimode night no"], CarFeed.Changes(told, new CarState(Night: false), AndroidFlavour.Regular, VhalRoute.Store).Commands);
    }

    [Fact]
    public void The_store_route_is_used_only_when_the_hal_answers_with_a_value()
    {
        Assert.True(CarFeed.StoreAnswers("VehiclePropValue{timestamp: 1, areaId: 0, prop: 291504647, status: AVAILABLE, value: RawPropValues{int32Values: [], floatValues: [0.000000]}}"));
        Assert.False(CarFeed.StoreAnswers("Caller must be root, system or shell"));
        Assert.False(CarFeed.StoreAnswers("Can't find service: android.hardware.automotive.vehicle.IVehicle/default"));
        Assert.True(CarFeed.Refused("Unknown command: inject-vhal-event"));
        Assert.False(CarFeed.Refused(""));
    }
}
