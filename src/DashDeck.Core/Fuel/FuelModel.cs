using DashDeck.Abstractions;
using DashDeck.Core.Bus;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Fuel;

/// <summary>Where <c>fuel.flowRate</c> came from — the value of <c>fuel.flowSource</c>.</summary>
public enum FuelFlowSource
{
    /// <summary>The truck's own fuel rate (PID 5E).</summary>
    Truck = 0,

    /// <summary>Speed-density, calibrated by at least one full-to-full fill-up.</summary>
    Calibrated = 1,

    /// <summary>Speed-density, not yet calibrated — an estimate that may be well off.</summary>
    Uncalibrated = 2,
}

/// <summary>
/// Fuel flow, economy and range for a truck that reports neither fuel rate nor mass air flow
/// (ADR-0030, built by ADR-0041).
/// </summary>
/// <remarks>
/// <b>Truck first.</b> When the truck answers <c>engine.fuelRate</c>, that is the flow. When it
/// does not — the 2019 F-150 does not — flow is worked out by speed-density: air mass from
/// manifold pressure, intake temperature, rpm and displacement, fuel from that and the commanded
/// equivalence ratio. The estimate is multiplied by the <see cref="FuelLedger.Factor"/> that
/// full-to-full fill-ups teach, and <c>fuel.flowSource</c> says which of the three it is, so a
/// screen can mark an uncalibrated estimate as one.
/// <para>
/// <b>It runs all the time</b>, at Low priority, which is the one exception to "only what is on
/// screen asks" (ADR-0015): a fill-up can only be compared with fuel counted since the last one,
/// and fuel not counted while the console was off is fuel the calibration never sees. Its inputs
/// cost about 4 of the ~19 requests a second, and Low priority gives way to anything on screen.
/// </para>
/// <para>
/// The results are published to the bus as derived signals — <see cref="Definitions"/> — so a
/// layout, a card or a component reads them like any other.
/// </para>
/// </remarks>
public sealed class FuelModel : IDisposable
{
    // ── The derived signals ───────────────────────────────────────────────────

    public const string FlowRate = "fuel.flowRate";
    public const string FlowSourceId = "fuel.flowSource";
    public const string Economy = "fuel.economy";
    public const string EconomyAverage = "fuel.economyAverage";
    public const string Range = "fuel.range";
    public const string UsedSinceFill = "fuel.usedSinceFill";

    // ── What it reads ─────────────────────────────────────────────────────────

    public const string TruckFuelRate = "engine.fuelRate";
    public const string Rpm = "engine.rpm";
    public const string ManifoldPressure = "engine.intakeManifoldPressure";
    public const string IntakeAirTemp = "engine.intakeAirTemp";
    public const string Lambda = "engine.commandedLambda";
    public const string Speed = "vehicle.speed";
    public const string Level = "fuel.levelPercent";

    /// <summary>The volumetric efficiency assumed before calibration. Calibration absorbs the rest.</summary>
    public const double BaseVolumetricEfficiency = 0.85;

    /// <summary>Stoichiometric air–fuel ratio for gasoline, by mass.</summary>
    public const double StoichiometricAfr = 14.7;

    /// <summary>Gasoline, kg per litre.</summary>
    public const double FuelDensity = 0.745;

    /// <summary>The gas constant for dry air, J/(kg·K).</summary>
    private const double AirGasConstant = 287.05;

    /// <summary>The tank assumed when the profile does not say — the F-150's 136 L, as the Range Estimator assumes.</summary>
    public const double FallbackTankLitres = 136;

    /// <summary>Below this speed, litres per 100 km runs to infinity and means nothing.</summary>
    public const double MinimumEconomySpeed = 5;

    /// <summary>A gap longer than this between samples is not driving at that rate — it is a lost link.</summary>
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(5);

    private readonly VehicleStateBus _bus;
    private readonly IClock _clock;
    private readonly Func<VehicleProfile> _profile;
    private readonly Action<FuelLedger>? _save;
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, SignalValue> _latest = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly Lock _stepping = new();

    /// <summary>
    /// What a synthetic drive counts instead of the ledger: never saved, and seeded with a
    /// plausible average so range reads something at a desk — every value it feeds is Simulated.
    /// </summary>
    private FuelLedger _session = new() { HasBaseline = true, LifetimeLitres = 15, LifetimeKm = 100 };
    private DateTimeOffset _lastStep = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private double _smoothedEconomy = double.NaN;
    private bool _dirty;

    /// <param name="bus">Read from and published to.</param>
    /// <param name="profile">Displacement and tank size, read fresh each time (Settings ▸ Vehicle can change them).</param>
    /// <param name="ledger">What was learned before, or a fresh one.</param>
    /// <param name="save">Keeps the ledger. Only ever called with what the real truck taught it.</param>
    public FuelModel(VehicleStateBus bus, IClock clock, Func<VehicleProfile> profile, FuelLedger ledger, Action<FuelLedger>? save = null)
    {
        _bus = bus;
        _clock = clock;
        _profile = profile;
        _save = save;
        Ledger = ledger;
    }

    /// <summary>
    /// The derived signals, to add to the catalog so they can be declared, chosen for a card and
    /// read by a layout. Never asked of the truck.
    /// </summary>
    public static IReadOnlyList<SignalDefinition> Definitions { get; } =
    [
        Derived(FlowRate, "Fuel Flow (truck, or speed-density estimate)", "L/h", 0, 100),
        Derived(FlowSourceId, "Fuel Flow Source (0 truck, 1 calibrated estimate, 2 uncalibrated estimate)", "", 0, 2),
        Derived(Economy, "Fuel Economy — Instant", "L/100km", 0, 99.9),
        Derived(EconomyAverage, "Fuel Economy — Since Fill-Up", "L/100km", 0, 99.9),
        Derived(Range, "Distance to Empty", "km", 0, 2000),
        Derived(UsedSinceFill, "Fuel Used Since Fill-Up", "L", 0, 500),
    ];

    private static SignalDefinition Derived(string id, string name, string unit, double min, double max) => new()
    {
        Id = id,
        Name = name,
        Category = "Fuel",
        Kind = SignalSourceKind.Derived,
        Pid = 0,
        Decode = new DecodeSpec(0, 1, false, 1, 0, unit),
        DefaultRateHz = 1,
        StalenessSeconds = 10,
        Min = min,
        Max = max,
    };

    /// <summary>The catalog with the derived signals added, unless it already defines them.</summary>
    public static SignalCatalog AddTo(SignalCatalog catalog) =>
        SignalCatalog.FromDefinitions(catalog.Definitions.Concat(Definitions.Where(d => !catalog.TryGet(d.Id, out _))));

    /// <summary>What has been learned and counted. Changes as the truck is driven and filled.</summary>
    public FuelLedger Ledger
    {
        get
        {
            lock (_sync)
            {
                return field;
            }
        }

        private set
        {
            lock (_sync)
            {
                field = value;
            }
        }
    }

    /// <summary>Where the flow came from at the last step.</summary>
    public FuelFlowSource CurrentSource { get; private set; } = FuelFlowSource.Uncalibrated;

    /// <summary>Raised when the ledger changes from outside driving — a fill-up, a reset.</summary>
    public event EventHandler? LedgerChanged;

    /// <summary>Declare the inputs and start counting.</summary>
    public void Start()
    {
        // Low priority: anything on screen comes first, and this degrades before it does.
        Hold(TruckFuelRate, 1);
        Hold(Rpm, 1);
        Hold(ManifoldPressure, 1);
        Hold(Speed, 1);
        Hold(Lambda, 0.5);
        Hold(IntakeAirTemp, 0.2);
        Hold(Level, 0.1);

        Step();
    }

    private void Hold(string id, double rateHz)
    {
        if (!_bus.KnownSignals.Contains(id))
        {
            return;
        }

        _held.Add(_bus.Require(id, SignalPriority.Low, rateHz));
        _held.Add(_bus.Subscribe(id, v =>
        {
            lock (_sync)
            {
                _latest[id] = v;
            }

            // Flow moves with rpm and pressure; distance with speed. The slow inputs just wait.
            if (id is Rpm or ManifoldPressure or Speed or TruckFuelRate)
            {
                Step();
            }
        }));
    }

    /// <summary>A full-to-full fill-up, entered in Settings ▸ Vehicle.</summary>
    public string RecordFill(double litres)
    {
        var (ledger, outcome) = Ledger.RecordFill(litres);
        Ledger = ledger;
        _save?.Invoke(ledger);
        LedgerChanged?.Invoke(this, EventArgs.Empty);
        Step();
        return outcome;
    }

    /// <summary>Forget the calibration and the counts.</summary>
    public void Reset()
    {
        Ledger = FuelLedger.Fresh;
        _save?.Invoke(Ledger);
        LedgerChanged?.Invoke(this, EventArgs.Empty);
        Step();
    }

    /// <summary>Write the ledger now, if driving has changed it — at shutdown.</summary>
    public void Flush()
    {
        if (_dirty)
        {
            _dirty = false;
            _save?.Invoke(Ledger);
        }
    }

    // ── The arithmetic ────────────────────────────────────────────────────────

    /// <summary>
    /// Speed-density fuel flow in litres an hour, before calibration — or NaN when an input is missing.
    /// </summary>
    /// <param name="mapKpa">Manifold absolute pressure.</param>
    /// <param name="iatC">Intake air temperature.</param>
    /// <param name="rpm">Engine speed. Zero is zero flow: the engine is off.</param>
    /// <param name="lambda">Commanded equivalence ratio; 1 is stoichiometric.</param>
    /// <param name="displacementLitres">Engine displacement.</param>
    public static double SpeedDensityLitresPerHour(double mapKpa, double iatC, double rpm, double lambda, double displacementLitres)
    {
        if (!double.IsFinite(mapKpa) || !double.IsFinite(iatC) || !double.IsFinite(rpm) || displacementLitres <= 0)
        {
            return double.NaN;
        }

        if (rpm <= 0)
        {
            return 0;
        }

        // A four-stroke fills each cylinder once every two turns: displacement × rpm / 2 a minute.
        var airDensity = mapKpa * 1000 / (AirGasConstant * (iatC + 273.15));                 // kg/m³
        var airVolume = displacementLitres / 1000 * (rpm / 120) * BaseVolumetricEfficiency;  // m³/s
        var airMass = airDensity * airVolume;                                               // kg/s
        var ratio = StoichiometricAfr * (double.IsFinite(lambda) && lambda > 0.5 ? lambda : 1);
        var fuelMass = airMass / ratio;                                                     // kg/s
        return fuelMass / FuelDensity * 3600;                                               // L/h
    }

    /// <summary>Distance to empty: what is left in the tank over the average economy.</summary>
    public static double DistanceToEmpty(double levelPercent, double tankLitres, double litresPer100Km) =>
        double.IsFinite(levelPercent) && double.IsFinite(litresPer100Km) && litresPer100Km > 0 && tankLitres > 0
            ? Math.Max(0, levelPercent) / 100 * tankLitres / litresPer100Km * 100
            : double.NaN;

    private SignalValue Read(string id)
    {
        lock (_sync)
        {
            if (!_latest.ContainsKey(id))
            {
                return SignalValue.Missing(id);
            }
        }

        // The bus applies staleness; the copy here is only to know it has been heard from.
        return _bus.Current(id);
    }

    /// <summary>Work everything out again from the latest inputs and publish it.</summary>
    private void Step()
    {
        // The polling worker and a fill-up entered on the UI thread can both get here.
        lock (_stepping)
        {
            StepLocked();
        }
    }

    private void StepLocked()
    {
        var now = _clock.UtcNow;
        var truck = Read(TruckFuelRate);
        var rpm = Read(Rpm);
        var map = Read(ManifoldPressure);
        var iat = Read(IntakeAirTemp);
        var lambda = Read(Lambda);
        var speed = Read(Speed);
        var level = Read(Level);
        var profile = _profile();
        var ledger = Ledger;

        // The estimate, raw. Intake temperature changes slowly; a stale one still describes the air.
        var estimateInputs = Worst(rpm, map);
        var raw = rpm.IsUsable && map.IsUsable && !double.IsNaN(iat.Value)
            ? SpeedDensityLitresPerHour(map.Value, iat.Value, rpm.Value, lambda.IsUsable ? lambda.Value : 1, profile.EngineDisplacementLitres)
            : double.NaN;

        // Truck first (ADR-0016): its own fuel rate when it gives one.
        double flow;
        FuelFlowSource source;
        SignalQuality flowQuality;

        if (truck.IsUsable)
        {
            flow = truck.Value;
            source = FuelFlowSource.Truck;
            flowQuality = truck.Quality;
        }
        else if (double.IsFinite(raw))
        {
            flow = raw * ledger.Factor;
            source = ledger.IsCalibrated ? FuelFlowSource.Calibrated : FuelFlowSource.Uncalibrated;
            flowQuality = estimateInputs;
        }
        else
        {
            flow = double.NaN;
            source = ledger.IsCalibrated ? FuelFlowSource.Calibrated : FuelFlowSource.Uncalibrated;
            flowQuality = SignalQuality.Unavailable;
        }

        CurrentSource = source;

        // Count what was used since the last step — only from the real truck, only while moving
        // numbers are fresh, and never across a gap long enough to be a lost link.
        var dt = _lastStep == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _lastStep;
        _lastStep = now;

        if (dt > TimeSpan.Zero && dt <= MaxStep && flowQuality is SignalQuality.Live)
        {
            var hours = dt.TotalHours;
            var km = speed.Quality is SignalQuality.Live ? speed.Value * hours : 0;
            ledger = ledger.Add(double.IsFinite(raw) ? raw * hours : 0, flow * hours, km);
            Ledger = ledger;
            _dirty = true;

            if (now - _lastSave > TimeSpan.FromSeconds(60))
            {
                _lastSave = now;
                Flush();
            }
        }
        else if (dt > TimeSpan.Zero && dt <= MaxStep && flowQuality is SignalQuality.Simulated)
        {
            var hours = dt.TotalHours;
            _session = _session.Add(double.IsFinite(raw) ? raw * hours : 0, flow * hours, speed.IsUsable ? speed.Value * hours : 0);
        }

        // A synthetic drive reads from its own count, so it never teaches or shows the real one.
        var counted = flowQuality is SignalQuality.Simulated ? _session : ledger;

        // Instant economy: flow over speed, lightly smoothed, blank when crawling or unknown.
        double economy;
        var economyQuality = Worse(flowQuality, speed.Quality);

        if (double.IsFinite(flow) && speed.IsUsable && speed.Value >= MinimumEconomySpeed && flowQuality is not SignalQuality.Unavailable)
        {
            var instant = Math.Clamp(flow / speed.Value * 100, 0, 99.9);
            _smoothedEconomy = double.IsNaN(_smoothedEconomy) ? instant : _smoothedEconomy + (0.25 * (instant - _smoothedEconomy));
            economy = _smoothedEconomy;
        }
        else
        {
            _smoothedEconomy = double.NaN;
            economy = double.NaN;
        }

        var average = counted.AverageEconomy();
        var tank = profile.FuelTankLitres > 0 ? profile.FuelTankLitres : FallbackTankLitres;
        var range = DistanceToEmpty(level.IsUsable ? level.Value : double.NaN, tank, average);
        var ledgerQuality = flowQuality is SignalQuality.Simulated ? SignalQuality.Simulated : SignalQuality.Live;

        Publish(FlowRate, flow, "L/h", flowQuality);
        Publish(FlowSourceId, (double)source, "", flowQuality is SignalQuality.Unavailable && !truck.IsUsable && double.IsNaN(raw) ? SignalQuality.Unavailable : SignalQuality.Live);
        Publish(Economy, economy, "L/100km", double.IsNaN(economy) ? SignalQuality.Unavailable : economyQuality);
        Publish(EconomyAverage, average, "L/100km", double.IsNaN(average) ? SignalQuality.Unavailable : ledgerQuality);
        Publish(Range, range, "km", double.IsNaN(range) ? SignalQuality.Unavailable : Worse(level.Quality, ledgerQuality));
        Publish(UsedSinceFill, counted.HasBaseline ? counted.LitresSinceFill : double.NaN, "L", counted.HasBaseline ? ledgerQuality : SignalQuality.Unavailable);
    }

    private void Publish(string id, double value, string unit, SignalQuality quality) =>
        _bus.Publish(new SignalValue(id, quality is SignalQuality.Unavailable ? double.NaN : value, unit, _clock.UtcNow, quality));

    private static SignalQuality Worst(params SignalValue[] values) =>
        values.Select(v => v.Quality).Aggregate(SignalQuality.Live, Worse);

    /// <summary>Unavailable beats Stale beats Simulated beats Live: the least trustworthy input wins.</summary>
    private static SignalQuality Worse(SignalQuality a, SignalQuality b)
    {
        static int Rank(SignalQuality q) => q switch
        {
            SignalQuality.Unavailable => 3,
            SignalQuality.Stale => 2,
            SignalQuality.Simulated => 1,
            _ => 0,
        };

        return Rank(a) >= Rank(b) ? a : b;
    }

    public void Dispose()
    {
        foreach (var held in _held)
        {
            held.Dispose();
        }

        _held.Clear();
        Flush();
    }
}
