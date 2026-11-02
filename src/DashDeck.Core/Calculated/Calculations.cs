using DashDeck.Abstractions;

namespace DashDeck.Core.Calculated;

/// <summary>
/// What an estimate of fuel use needs to know about the engine that the bus does not say (ADR-0081).
/// </summary>
/// <param name="DisplacementLitres">The engine's size, from the VIN decode. Zero when not known: nothing is estimated.</param>
/// <param name="Correction">
/// The person's factor, tuned against fill-ups: litres pumped over litres the dash added up.
/// One until they have a reason to change it.
/// </param>
public sealed record FuelModel(double DisplacementLitres, double Correction = 1.0)
{
    /// <summary>How much of the cylinder fills each intake stroke, before the person's correction.</summary>
    public const double VolumetricEfficiency = 0.85;

    /// <summary>Petrol's air to fuel mass ratio at λ = 1.</summary>
    public const double StoichiometricAfr = 14.7;

    /// <summary>Petrol's density, grams per litre.</summary>
    public const double FuelGramsPerLitre = 745;

    /// <summary>The specific gas constant of air, J/(kg·K).</summary>
    public const double AirGasConstant = 287.05;

    /// <summary>Above this commanded λ the engine is taken to have cut fuel, coasting.</summary>
    public const double FuelCutLambda = 1.5;

    /// <summary>The smallest and largest correction the person can set.</summary>
    public const double MinimumCorrection = 0.5;

    /// <inheritdoc cref="MinimumCorrection"/>
    public const double MaximumCorrection = 1.5;

    /// <summary>Nothing known: every estimate reads Unavailable.</summary>
    public static FuelModel Unknown { get; } = new(0);

    /// <summary>True when there is enough to estimate with.</summary>
    public bool CanEstimate => DisplacementLitres > 0 && double.IsFinite(DisplacementLitres);

    /// <summary>The correction, kept inside its range.</summary>
    public double ClampedCorrection => double.IsFinite(Correction) ? Math.Clamp(Correction, MinimumCorrection, MaximumCorrection) : 1.0;
}

/// <summary>One input a calculation reads: the signal, and whether it can do without it.</summary>
public sealed record CalculationInput(string SignalId, bool Required);

/// <summary>
/// Signals the dash works out from other signals instead of asking the truck for (ADR-0081).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The 2.7 EcoBoost reports no airflow (<c>01 10</c>) and no fuel rate (<c>01 5E</c>),
/// and the cluster shows no instant economy, so there is nothing to find. Fuel use is estimated
/// from what the engine computer does report — manifold pressure, intake air temperature and RPM —
/// by speed density, as phone apps do. Commanded λ (<c>01 44</c>) and the fuel trims correct it
/// when the truck answers them.
/// </para>
/// <para>
/// <b>An estimate, and said to be one.</b> Volumetric efficiency is taken as a constant, so the
/// number is off by however far the engine is from it, most under boost. The person's correction,
/// set from fill-ups, takes out the steady part of that error.
/// </para>
/// <para>
/// <b>Economy stopped.</b> Litres per hundred kilometres has no value at a standstill, so below
/// <see cref="MovingKph"/> the economy signal reads litres per hour instead and says so in its
/// unit. Anything that shows the reading's unit shows the change.
/// </para>
/// <para>Pure: the polling loop hands it the latest readings and publishes what comes back.</para>
/// </remarks>
public static class Calculations
{
    /// <summary>Estimated fuel flow, L/h.</summary>
    public const string FuelFlow = "fuelFlow";

    /// <summary>Instant economy, L/100km moving and L/h stopped.</summary>
    public const string FuelEconomy = "fuelEconomy";

    /// <summary>Below this speed the economy reads litres per hour.</summary>
    public const double MovingKph = 3;

    /// <summary>The unit economy is in while moving.</summary>
    public const string EconomyUnit = "L/100km";

    /// <summary>The unit fuel flow is in, and economy while stopped.</summary>
    public const string FlowUnit = "L/h";

    /// <summary>The highest economy shown; slower than this is walking pace with the engine working.</summary>
    public const double MaximumEconomy = 99.9;

    /// <summary>Every calculation there is.</summary>
    public static IReadOnlyList<string> Known { get; } = [FuelFlow, FuelEconomy];

    private static readonly CalculationInput[] FlowInputs =
    [
        new("engine.intakeManifoldPressure", Required: true),
        new("engine.intakeAirTemp", Required: true),
        new("engine.rpm", Required: true),
        new("fuel.commandedLambda", Required: false),
        new("fuel.shortTermTrimB1", Required: false),
        new("fuel.longTermTrimB1", Required: false),
        new("fuel.shortTermTrimB2", Required: false),
        new("fuel.longTermTrimB2", Required: false),
    ];

    private static readonly CalculationInput[] EconomyInputs =
        [.. FlowInputs, new("vehicle.speed", Required: true)];

    /// <summary>True for a calculation this build knows.</summary>
    public static bool IsKnown(string? name) => name is not null && Known.Contains(name, StringComparer.Ordinal);

    /// <summary>What a calculation reads; empty for one this build does not know.</summary>
    public static IReadOnlyList<CalculationInput> InputsOf(string? name) => name switch
    {
        FuelFlow => FlowInputs,
        FuelEconomy => EconomyInputs,
        _ => [],
    };

    /// <summary>
    /// Work a calculated signal out from the latest readings.
    /// </summary>
    /// <param name="signalId">The calculated signal's id, which the reading carries.</param>
    /// <param name="name">Which calculation.</param>
    /// <param name="read">The latest reading of an input, as the bus has it, staleness included.</param>
    /// <param name="model">The engine's facts and the person's correction.</param>
    /// <param name="unit">The catalog's unit for the signal, used when nothing can be worked out.</param>
    public static SignalValue Calculate(string signalId, string name, Func<string, SignalValue> read, FuelModel model, string unit)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(model);

        var inputs = InputsOf(name);

        if (inputs.Count == 0 || !model.CanEstimate)
        {
            return SignalValue.Missing(signalId, unit);
        }

        var readings = inputs.ToDictionary(i => i.SignalId, i => read(i.SignalId), StringComparer.Ordinal);
        var required = inputs.Where(i => i.Required).Select(i => readings[i.SignalId]).ToList();

        if (required.Any(v => !HasNumber(v)))
        {
            return SignalValue.Missing(signalId, unit);
        }

        var quality = required.Select(v => v.Quality).Aggregate(SignalQuality.Live, Worse);

        // As new as its newest input. An input gone stale already makes the whole reading Stale,
        // through its quality; a slow input such as intake temperature must not age a fresh one.
        var at = required.Max(v => v.TimestampUtc);

        var flow = FlowLitresPerHour(readings, model);

        if (name == FuelFlow)
        {
            return new SignalValue(signalId, flow, FlowUnit, at, quality);
        }

        var speed = readings["vehicle.speed"].Value;

        return speed < MovingKph
            ? new SignalValue(signalId, flow, FlowUnit, at, quality)
            : new SignalValue(signalId, Math.Min(flow / speed * 100, MaximumEconomy), EconomyUnit, at, quality);
    }

    /// <summary>
    /// The unit to print beside a reading on a layout that wrote its own: the layout's, unless the
    /// reading has switched to the stopped unit of economy, which the layout cannot know about.
    /// </summary>
    public static string UnitShown(string layoutUnit, string? readingUnit) =>
        readingUnit == FlowUnit && layoutUnit == EconomyUnit ? FlowUnit : layoutUnit;

    /// <summary>Speed density: air in from pressure, temperature and engine speed; fuel from air.</summary>
    private static double FlowLitresPerHour(IReadOnlyDictionary<string, SignalValue> r, FuelModel model)
    {
        var mapKpa = r["engine.intakeManifoldPressure"].Value;
        var iatC = r["engine.intakeAirTemp"].Value;
        var rpm = r["engine.rpm"].Value;

        if (rpm <= 0 || mapKpa <= 0)
        {
            return 0;
        }

        var lambda = Optional(r, "fuel.commandedLambda") is { } l && l > 0 ? l : 1.0;

        if (lambda > FuelModel.FuelCutLambda)
        {
            // Coasting in gear with the injectors off: no fuel, whatever the air.
            return 0;
        }

        // kg/m³ of the charge, then m³/s swept: a four-stroke fills each cylinder every other turn.
        var density = mapKpa * 1000 / (FuelModel.AirGasConstant * (iatC + 273.15));
        var swept = model.DisplacementLitres / 1000 * rpm / 120;
        var airGramsPerSecond = density * swept * FuelModel.VolumetricEfficiency * 1000;

        var fuelGramsPerSecond = airGramsPerSecond / (FuelModel.StoichiometricAfr * lambda) * (1 + Trim(r) / 100);

        return Math.Max(0, fuelGramsPerSecond * 3600 / FuelModel.FuelGramsPerLitre * model.ClampedCorrection);
    }

    /// <summary>The fuel trims, short plus long, averaged over the banks that answered; zero when none did.</summary>
    private static double Trim(IReadOnlyDictionary<string, SignalValue> r)
    {
        var banks = new[]
        {
            (Optional(r, "fuel.shortTermTrimB1"), Optional(r, "fuel.longTermTrimB1")),
            (Optional(r, "fuel.shortTermTrimB2"), Optional(r, "fuel.longTermTrimB2")),
        }
        .Where(b => b.Item1 is not null || b.Item2 is not null)
        .Select(b => (b.Item1 ?? 0) + (b.Item2 ?? 0))
        .ToList();

        return banks.Count == 0 ? 0 : banks.Average();
    }

    private static double? Optional(IReadOnlyDictionary<string, SignalValue> r, string id) =>
        r.TryGetValue(id, out var v) && HasNumber(v) ? v.Value : null;

    /// <summary>A number to work with: usable, or stale with one still in it.</summary>
    private static bool HasNumber(SignalValue v) =>
        double.IsFinite(v.Value) && v.Quality is SignalQuality.Live or SignalQuality.Simulated or SignalQuality.Stale;

    /// <summary>Stale beats Simulated beats Live: the least trustworthy input wins.</summary>
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
}
