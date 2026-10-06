namespace DashDeck.Simulator;

/// <summary>The synthetic truck's quantities by name — what the catalog and the data file ask it for (ADR-0052).</summary>
/// <remarks>
/// The names are the catalog's signal ids wherever a signal exists (<c>engine.rpm</c>,
/// <c>hvac.fanSpeed</c>), so the synthetic truck answers whatever request the catalog says a
/// signal lives at, and a placeholder by its id. A few more name what only the simulator's own
/// data file asks for (<c>cabin.*</c>, <c>transmission.temp</c>). <b>No identifier lives here</b>:
/// where each quantity is asked for, and how it is encoded, is the catalog's business, or the
/// data file's.
/// </remarks>
public sealed partial class SimulatedF150
{
    private Dictionary<string, Func<double>>? _values;

    /// <summary>Sensor noise per name, applied by <see cref="Reading"/>. Absent means none.</summary>
    private static readonly Dictionary<string, double> Noise = new(StringComparer.Ordinal)
    {
        ["engine.rpm"] = 8,
        ["engine.mafRate"] = 0.3,
        ["fuel.shortTermTrimB1"] = 3,
        ["fuel.shortTermTrimB2"] = 3,
        ["engine.commandedLambda"] = 0.02,
        ["tire.frontLeft.pressure"] = 0.1,
        ["tire.frontRight.pressure"] = 0.1,
        ["tire.rearLeft.pressure"] = 0.1,
        ["tire.rearRight.pressure"] = 0.1,
    };

    /// <summary>Every name the truck can answer.</summary>
    public IReadOnlyCollection<string> ValueNames => Values.Keys;

    /// <summary>True when the truck has a quantity by this name.</summary>
    public bool Knows(string name) => Values.ContainsKey(name);

    /// <summary>The quantity's true value now, with no noise, or null for a name it does not know.</summary>
    /// <remarks>A fault holding the name (<see cref="Faults"/>) wins.</remarks>
    public double? Value(string name) =>
        Faults.TryHeld(name, _elapsed, out var held) ? held
        : Values.TryGetValue(name, out var read) ? read() : null;

    /// <summary>The quantity as a sensor reports it: with its noise, or null for a name it does not know.</summary>
    public double? Reading(string name) =>
        Value(name) is { } value
            ? Noise.TryGetValue(name, out var magnitude) && !Faults.TryHeld(name, _elapsed, out _) ? Jitter(value, magnitude) : value
            : null;

    private Dictionary<string, Func<double>> Values => _values ??= BuildValues();

    private Dictionary<string, Func<double>> BuildValues()
    {
        static double On(bool b) => b ? 1 : 0;

        return new Dictionary<string, Func<double>>(StringComparer.Ordinal)
        {
            // Engine.
            ["engine.rpm"] = () => Rpm,
            ["engine.load"] = () => EngineLoadPercent,
            ["engine.absoluteLoad"] = () => EngineLoadPercent * 2.5,
            ["engine.timingAdvance"] = () => 10 + (EnginePowerKw * 0.3),
            ["engine.runTime"] = () => RunTimeSeconds,
            ["engine.warmupsSinceClear"] = () => 8,
            ["engine.driverDemandTorque"] = () => EngineLoadPercent,
            ["engine.actualTorque"] = () => EngineLoadPercent * 0.9,
            ["engine.referenceTorque"] = () => 542,
            ["engine.throttlePosition"] = () => ThrottlePercent,
            ["engine.relativeThrottle"] = () => ThrottlePercent,
            ["engine.throttleB"] = () => ThrottlePercent,
            ["engine.acceleratorPedalD"] = () => ThrottlePercent,
            ["engine.acceleratorPedalE"] = () => ThrottlePercent,
            ["engine.commandedThrottle"] = () => ThrottlePercent,
            ["engine.mafRate"] = () => MafGramsPerSecond,
            ["engine.intakeManifoldPressure"] = () => 30 + (EnginePowerKw * 2.2),
            ["engine.barometricPressure"] = () => 101,
            ["engine.coolantTemp"] = () => CoolantTempC,
            ["engine.oilTemp"] = () => CoolantTempC - 3,
            ["engine.intakeAirTemp"] = () => IntakeAirTempC,
            ["engine.catalystTempB1S1"] = () => 250 + (EnginePowerKw * 3),
            ["engine.catalystTempB2S1"] = () => 245 + (EnginePowerKw * 3),
            ["engine.commandedLambda"] = () => 1.0,
            ["engine.fuelRate"] = () => FuelRateLitresPerHour,
            ["ambient.airTemp"] = () => AmbientTempC,

            // Fuel.
            ["fuel.levelPercent"] = () => FuelLevelPercent,
            ["fuel.pressure"] = () => 381,
            ["fuel.railGaugePressure"] = () => 38000,
            ["fuel.shortTermTrimB1"] = () => 0,
            ["fuel.longTermTrimB1"] = () => -2.5,
            ["fuel.shortTermTrimB2"] = () => 0,
            ["fuel.longTermTrimB2"] = () => -1.6,
            ["fuel.ethanolPercent"] = () => 10,
            ["fuel.economy"] = () => EconomyL100,
            ["fuel.range"] = () => RangeKm,

            // Vehicle and diagnostics.
            ["vehicle.speed"] = () => SpeedKph,
            ["vehicle.odometer"] = () => OdometerKm,
            ["vehicle.distanceSinceClear"] = () => 1240 + DistanceKm,
            ["vehicle.distanceWithMil"] = () => 0,
            ["vehicle.controlModuleVoltage"] = () => SpeedKph > 0 ? 14.2 : 12.6,
            ["emissions.commandedEgr"] = () => EnginePowerKw > 5 ? 8 : 0,
            ["emissions.evapPurge"] = () => Math.Clamp(Jitter(6, 6), 0, 100),
            ["diagnostics.checkEngine"] = () => On(CheckEngine),
            ["diagnostics.dtcCount"] = () => StoredCodes,

            // Tyres (TPMS placeholders) and warning lights (placeholders, all off).
            ["tire.frontLeft.pressure"] = () => TirePsiFrontLeft,
            ["tire.frontRight.pressure"] = () => TirePsiFrontRight,
            ["tire.rearLeft.pressure"] = () => TirePsiRearLeft,
            ["tire.rearRight.pressure"] = () => TirePsiRearRight,
            ["warning.oilPressure"] = () => 0,
            ["warning.seatbelt"] = () => 0,
            ["warning.doorAjar"] = () => 0,
            ["warning.parkingBrake"] = () => 0,
            ["warning.tirePressure"] = () => 0,

            // Climate (placeholders).
            ["hvac.driverSetTemp"] = () => DriverSetTempC,
            ["hvac.passengerSetTemp"] = () => PassengerSetTempC,
            ["hvac.cabinTemp"] = () => CabinTempC,
            ["hvac.fanSpeed"] = () => FanSpeed,
            ["hvac.airConditioning"] = () => On(AirConditioning),
            ["hvac.auto"] = () => On(AutoMode),
            ["hvac.recirculate"] = () => On(Recirculate),
            ["hvac.frontDefrost"] = () => On(FrontDefrost),
            ["hvac.rearDefrost"] = () => On(RearDefrost),
            ["hvac.airflow"] = () => Airflow,
            ["seat.driver.climate"] = () => DriverSeat,
            ["seat.passenger.climate"] = () => PassengerSeat,
            ["steeringWheel.heat"] = () => On(SteeringWheelHeat),

            // Body (placeholder): the tailgate, set by hand like the cabin's switches.
            ["body.tailgate"] = () => On(Cabin.TailgateOpen),

            // Multi-state (ADR-0056), by the catalog's order of states.
            ["drivetrain.4wdMode"] = () => Cabin.FourWheelDrive,
            ["transmission.gearSelector"] = () => GearSelector,
            ["vehicle.driveMode"] = () => Towing ? 3 : Cabin.DriveMode,
            ["body.wipers"] = () => Cabin.Wipers,
            ["body.headlights"] = () => Cabin.Headlights,

            // Only the simulator's own data file asks for these (catalog/simulator/).
            ["oil.temp"] = () => OilTempC,
            ["transmission.temp"] = () => TransmissionTempC,
            ["battery.voltage"] = () => 14.1,
            ["cluster.economy"] = () => Math.Max(EconomyL100, 13.4),
            ["cabin.driverDoor"] = () => On(Cabin.DriverDoorOpen),
            ["cabin.passengerDoor"] = () => On(Cabin.PassengerDoorOpen),
            ["cabin.driverSeat"] = () => Cabin.DriverSeat,
            ["cabin.passengerSeat"] = () => Cabin.PassengerSeat,
            ["cabin.wheelHeat"] = () => On(Cabin.WheelHeat),
            ["cabin.fan"] = () => Cabin.Fan,
            ["cabin.ac"] = () => On(Cabin.AirConditioning),
            ["cabin.recirc"] = () => On(Cabin.Recirculate),
            ["cabin.rearDefrost"] = () => On(Cabin.RearDefrost),
            ["cabin.auto"] = () => On(Cabin.Auto),
            ["cabin.driverSetTemp"] = () => Cabin.DriverSetTempC,
            ["cabin.seatbeltUnbuckled"] = () => On(!Cabin.DriverSeatbeltBuckled),
            ["cabin.parkingBrake"] = () => On(Cabin.ParkingBrake),
        };
    }
}
