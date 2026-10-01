namespace DashDeck.Simulator;

/// <summary>
/// A physical model of a 2019 F-150 with the 2.7L EcoBoost, good enough to produce
/// believable OBD-II values and an exactly known fuel consumption.
/// </summary>
/// <remarks>
/// This is deliberately not a well-behaved data source. It models a cold start that warms
/// up over minutes, economy that responds to speed, grade and load, and an idle that
/// consumes fuel. A simulator that is too tidy produces components that break on contact
/// with the truck, which is the characteristic failure of mock-first development
/// (ADR-0005).
/// <para>
/// The fuel model is a road-load equation — rolling resistance plus aerodynamic drag plus
/// grade and inertia — converted to fuel through a constant brake-specific consumption.
/// It is not a claim about the real truck's economy. Its job is to be *internally exact*,
/// so <see cref="FuelUsedLitres"/> is the ground truth a trip computer must reproduce.
/// </para>
/// </remarks>
public sealed class SimulatedF150
{
    // Vehicle constants, roughly a 2019 SuperCrew 4x4.
    private const double MassKg = 2400;
    private const double DragArea = 1.15;          // Cd * frontal area, m^2
    private const double AirDensity = 1.225;       // kg/m^3
    private const double RollingCoefficient = 0.012;
    private const double Gravity = 9.81;
    private const double DrivelineEfficiency = 0.82;

    // Fuel constants.
    private const double IdleFuelLitresPerHour = 1.6;
    private const double BrakeSpecificFuelKgPerKwh = 0.30;
    private const double GasolineDensityKgPerLitre = 0.745;

    private const double TankCapacityLitres = 136.0;   // 36 US gal

    private readonly ScriptedDrive _drive;
    private readonly Random _random;

    private double _elapsed;
    private int _segmentIndex;
    private double _segmentElapsed;
    private double _tireWarmthPsi;

    // Cold tyre pressures, psi. A 2019 SuperCrew's placard is about 35 all round; the rear
    // left is deliberately low, so the TPMS component has something worth warning about and
    // the overhead view has a corner to light up. Real tyres gain a couple of psi as they
    // warm with driving, which is what _tireWarmthPsi models.
    private const double ColdFrontLeftPsi = 35.5;
    private const double ColdFrontRightPsi = 35.0;
    private const double ColdRearLeftPsi = 27.0;
    private const double ColdRearRightPsi = 34.5;

    public SimulatedF150(ScriptedDrive drive, double ambientTempC = 4.0, int seed = 20190612)
    {
        _drive = drive;
        _random = new Random(seed);
        AmbientTempC = ambientTempC;
        CoolantTempC = ambientTempC;
        IntakeAirTempC = ambientTempC;
        FuelLevelLitres = TankCapacityLitres * 0.62;
    }

    public double AmbientTempC { get; }

    public double SpeedKph { get; private set; }

    public double Rpm { get; private set; } = 700;

    public double CoolantTempC { get; private set; }

    public double IntakeAirTempC { get; private set; }

    public double ThrottlePercent { get; private set; }

    public double EnginePowerKw { get; private set; }

    public double FuelRateLitresPerHour { get; private set; } = IdleFuelLitresPerHour;

    public double MafGramsPerSecond { get; private set; } = 3.0;

    public double FuelLevelLitres { get; private set; }

    public double FuelLevelPercent => FuelLevelLitres / TankCapacityLitres * 100.0;

    public double EngineLoadPercent { get; private set; }

    /// <summary>Seconds since the engine started.</summary>
    public double RunTimeSeconds => _elapsed;

    /// <summary>Distance covered, in kilometres. Ground truth for economy assertions.</summary>
    public double DistanceKm { get; private set; }

    /// <summary>Fuel burned, in litres. The exact value a trip computer must reproduce.</summary>
    public double FuelUsedLitres { get; private set; }

    /// <summary>Front-left tyre pressure, psi — warms a little with driving.</summary>
    public double TirePsiFrontLeft => ColdFrontLeftPsi + _tireWarmthPsi;

    /// <summary>Front-right tyre pressure, psi.</summary>
    public double TirePsiFrontRight => ColdFrontRightPsi + _tireWarmthPsi;

    /// <summary>Rear-left tyre pressure, psi — deliberately low.</summary>
    public double TirePsiRearLeft => ColdRearLeftPsi + _tireWarmthPsi;

    /// <summary>Rear-right tyre pressure, psi.</summary>
    public double TirePsiRearRight => ColdRearRightPsi + _tireWarmthPsi;

    /// <summary>True once the scripted drive has run to completion.</summary>
    public bool IsFinished => _segmentIndex >= _drive.Segments.Count;

    public string CurrentSegment => IsFinished ? "finished" : _drive.Segments[_segmentIndex].Name;

    /// <summary>Advance the model. Called with small steps; 50 ms is a good default.</summary>
    public void Advance(TimeSpan delta)
    {
        var dt = delta.TotalSeconds;
        if (dt <= 0 || IsFinished)
        {
            return;
        }

        _elapsed += dt;
        _segmentElapsed += dt;

        var segment = _drive.Segments[_segmentIndex];
        if (_segmentElapsed >= segment.Seconds)
        {
            _segmentElapsed = 0;
            _segmentIndex++;
            if (IsFinished)
            {
                return;
            }

            segment = _drive.Segments[_segmentIndex];
        }

        StepSpeed(segment, dt);
        StepEngine(segment, dt);
        StepThermal(dt);
        StepTires(dt);
    }

    /// <summary>
    /// Tyres warm and gain pressure as they roll, and cool when stopped.
    /// </summary>
    /// <remarks>
    /// Not physics, just believable movement: enough that the numbers on the overhead view are
    /// alive rather than frozen, and enough that a cold start reads lower than a highway cruise.
    /// The rear left stays proportionally low throughout, so the warning is a property of the
    /// tyre and not of the moment.
    /// </remarks>
    private void StepTires(double dt)
    {
        var heatTarget = Math.Min(2.5, SpeedKph / 40.0 * 2.5);
        _tireWarmthPsi += (heatTarget - _tireWarmthPsi) * Math.Min(0.008 * dt, 1.0);
    }

    /// <summary>Move toward the segment's target speed at a plausible rate.</summary>
    private void StepSpeed(DriveSegment segment, double dt)
    {
        var target = segment.TargetSpeedKph;
        var error = target - SpeedKph;

        // Trucks accelerate more slowly than they brake, and more slowly when loaded.
        var loadFactor = MassKg / (MassKg + segment.TowingKg);
        var rate = error > 0 ? 8.0 * loadFactor : 14.0;

        var change = Math.Clamp(error, -rate * dt, rate * dt);
        SpeedKph = Math.Max(0, SpeedKph + change);

        DistanceKm += SpeedKph / 3600.0 * dt;
    }

    /// <summary>Road-load power, then fuel. This is where the ground truth comes from.</summary>
    private void StepEngine(DriveSegment segment, double dt)
    {
        var speedMs = SpeedKph / 3.6;
        var totalMass = MassKg + segment.TowingKg;

        var rolling = RollingCoefficient * totalMass * Gravity;
        var drag = 0.5 * AirDensity * DragArea * speedMs * speedMs;
        var grade = totalMass * Gravity * (segment.GradePercent / 100.0);

        var tractiveForce = rolling + drag + grade;
        var roadPowerKw = Math.Max(0, tractiveForce * speedMs / 1000.0);

        EnginePowerKw = roadPowerKw / DrivelineEfficiency;

        // Fuel: a constant idle draw plus brake-specific consumption on delivered power.
        var powerFuelKgPerHour = EnginePowerKw * BrakeSpecificFuelKgPerKwh;
        var powerFuelLph = powerFuelKgPerHour / GasolineDensityKgPerLitre;

        FuelRateLitresPerHour = IdleFuelLitresPerHour + powerFuelLph;

        var burned = FuelRateLitresPerHour / 3600.0 * dt;
        FuelUsedLitres += burned;
        FuelLevelLitres = Math.Max(0, FuelLevelLitres - burned);

        // RPM: idle when stopped, otherwise a plausible cruising band that steps with speed.
        Rpm = SpeedKph < 2
            ? 700 + (_random.NextDouble() * 40) - 20
            : 1100 + (SpeedKph * 7) + (EnginePowerKw * 4);

        // A naturally-aspirated-style MAF estimate: air scales with power demand.
        MafGramsPerSecond = 2.5 + (EnginePowerKw * 0.55);

        EngineLoadPercent = Math.Clamp(EnginePowerKw / 2.5, 0, 100);
        ThrottlePercent = Math.Clamp(12 + (EnginePowerKw * 0.45), 0, 100);
    }

    /// <summary>
    /// Warm-up. The engine reaches operating temperature over minutes, not instantly —
    /// a component that assumes a warm engine at t=0 should fail here, not on the truck.
    /// </summary>
    private void StepThermal(double dt)
    {
        const double OperatingTempC = 92.0;

        // Heating accelerates with load; a cold idle warms slowly, which is the real
        // behaviour that makes cold-start economy so poor.
        var heatingRate = (0.055 + (EnginePowerKw * 0.0022)) * dt;
        CoolantTempC += (OperatingTempC - CoolantTempC) * Math.Min(heatingRate, 1.0);

        // Intake air warms toward ambient plus a little underhood heat soak.
        var intakeTarget = AmbientTempC + Math.Min(18, CoolantTempC * 0.12);
        IntakeAirTempC += (intakeTarget - IntakeAirTempC) * Math.Min(0.02 * dt, 1.0);
    }

    /// <summary>Small sensor noise, so nothing downstream assumes perfectly clean values.</summary>
    public double Jitter(double value, double magnitude) =>
        value + ((_random.NextDouble() - 0.5) * 2 * magnitude);

    internal Random Random => _random;
}
