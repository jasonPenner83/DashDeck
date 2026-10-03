using System.Text.Json.Serialization;

namespace DashDeck.Core.Fuel;

/// <summary>
/// What DashDeck has learned about this truck's fuel use, kept across launches (ADR-0041):
/// the calibration factor, and what has been used since the last fill-up.
/// </summary>
/// <remarks>
/// Speed-density is an estimate whose error is mostly one number — how well the engine fills
/// its cylinders against the constant assumed. A full-to-full fill-up measures that number: the
/// litres the pump put back, over the litres the estimate thinks were used. That ratio is the
/// <see cref="Factor"/>, and every later estimate is multiplied by it.
/// <para>
/// Only the real truck is ever written here. A synthetic drive would teach it a factor for an
/// engine that does not exist.
/// </para>
/// </remarks>
public sealed record FuelLedger
{
    /// <summary>The smallest fill-up that teaches anything: a few litres is mostly pump rounding.</summary>
    public const double MinimumFillLitres = 10;

    /// <summary>The smallest estimate since the last fill-up worth comparing a fill-up against.</summary>
    public const double MinimumEstimatedLitres = 5;

    /// <summary>A factor outside this is a partial fill, a missed fill-up or a wrong tank — not the engine.</summary>
    public const double MinimumFactor = 0.5;

    /// <inheritdoc cref="MinimumFactor"/>
    public const double MaximumFactor = 2.0;

    /// <summary>Multiplies the raw speed-density estimate. 1 until the first full-to-full fill-up.</summary>
    [JsonPropertyName("factor")]
    public double Factor { get; init; } = 1;

    /// <summary>How many fill-ups have calibrated <see cref="Factor"/>. Zero means uncalibrated.</summary>
    [JsonPropertyName("calibratingFills")]
    public int CalibratingFills { get; init; }

    /// <summary>The litres of fill-ups that calibrated the factor, so a new one is weighed against them.</summary>
    [JsonPropertyName("calibratingLitres")]
    public double CalibratingLitres { get; init; }

    /// <summary>True once a fill-up has started the count — before that, "since the last fill-up" means nothing.</summary>
    [JsonPropertyName("hasBaseline")]
    public bool HasBaseline { get; init; }

    /// <summary>The uncalibrated speed-density estimate since the last fill-up, litres.</summary>
    [JsonPropertyName("estimatedSinceFill")]
    public double EstimatedSinceFill { get; init; }

    /// <summary>The best figure for litres used since the last fill-up: the truck's own, or the calibrated estimate.</summary>
    [JsonPropertyName("litresSinceFill")]
    public double LitresSinceFill { get; init; }

    /// <summary>Distance since the last fill-up, km.</summary>
    [JsonPropertyName("kmSinceFill")]
    public double KmSinceFill { get; init; }

    /// <summary>Fuel put in by fill-ups that closed a full-to-full count, litres — for the long-run average.</summary>
    [JsonPropertyName("lifetimeLitres")]
    public double LifetimeLitres { get; init; }

    /// <summary>Distance covered by those fill-ups, km.</summary>
    [JsonPropertyName("lifetimeKm")]
    public double LifetimeKm { get; init; }

    [JsonIgnore]
    public bool IsCalibrated => CalibratingFills > 0;

    /// <summary>Add one stretch of driving: the raw estimate, the best figure, and the distance.</summary>
    public FuelLedger Add(double estimatedLitres, double litres, double km) => this with
    {
        EstimatedSinceFill = EstimatedSinceFill + Math.Max(0, estimatedLitres),
        LitresSinceFill = LitresSinceFill + Math.Max(0, litres),
        KmSinceFill = KmSinceFill + Math.Max(0, km),
    };

    /// <summary>
    /// Litres per 100 km since the last fill-up once there is enough distance to mean something,
    /// else the long-run average from past fill-ups, else NaN.
    /// </summary>
    public double AverageEconomy()
    {
        if (KmSinceFill >= 10 && LitresSinceFill > 0)
        {
            return LitresSinceFill / KmSinceFill * 100;
        }

        return LifetimeKm >= 50 && LifetimeLitres > 0 ? LifetimeLitres / LifetimeKm * 100 : double.NaN;
    }

    /// <summary>
    /// A full-to-full fill-up: what it teaches the factor, and the count starting again.
    /// </summary>
    /// <param name="litres">What the pump put in, filled to full.</param>
    /// <returns>The new ledger, and in words what it did.</returns>
    public (FuelLedger Ledger, string Outcome) RecordFill(double litres)
    {
        if (!double.IsFinite(litres) || litres <= 0)
        {
            return (this, "Enter the litres the pump put in.");
        }

        var restarted = this with { HasBaseline = true, EstimatedSinceFill = 0, LitresSinceFill = 0, KmSinceFill = 0 };

        if (!HasBaseline)
        {
            return (restarted, "First fill-up recorded. Drive, fill to full again, and enter that one — it calibrates the estimate.");
        }

        if (litres < MinimumFillLitres || EstimatedSinceFill < MinimumEstimatedLitres)
        {
            return (restarted, $"Recorded, but too little to calibrate from ({litres:0.0} L in, {EstimatedSinceFill:0.0} L estimated). The count starts again.");
        }

        var observed = litres / EstimatedSinceFill;
        var lifetime = restarted with
        {
            LifetimeLitres = LifetimeLitres + litres,
            LifetimeKm = LifetimeKm + KmSinceFill,
        };

        if (observed is < MinimumFactor or > MaximumFactor)
        {
            return (lifetime, $"Recorded, but {litres:0.0} L against {EstimatedSinceFill:0.0} L estimated is too far off to be the engine — a partial fill or a missed one? The factor stays {Factor:0.00}.");
        }

        // Each fill-up counts by its litres, so one short fill cannot swing a well-learned factor.
        var factor = IsCalibrated
            ? ((Factor * CalibratingLitres) + (observed * litres)) / (CalibratingLitres + litres)
            : observed;

        return (lifetime with
        {
            Factor = factor,
            CalibratingFills = CalibratingFills + 1,
            CalibratingLitres = CalibratingLitres + litres,
        }, $"Calibrated: {litres:0.0} L in against {EstimatedSinceFill:0.0} L estimated. The factor is now {factor:0.00}.");
    }

    /// <summary>Forget the calibration and the counts — after an engine change, or a bad entry.</summary>
    public static FuelLedger Fresh { get; } = new();
}
