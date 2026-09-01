namespace DashDeck.Abstractions;

/// <summary>
/// One reading of one signal, with everything needed to decide whether to show it.
/// </summary>
/// <param name="SignalId">Catalog id, e.g. <c>vehicle.speed</c>.</param>
/// <param name="Value">The decoded value, in <paramref name="Unit"/>.</param>
/// <param name="Unit">Unit symbol, e.g. <c>km/h</c>. Never assume it — read it.</param>
/// <param name="TimestampUtc">When the value was acquired, not when it was observed.</param>
/// <param name="Quality">How much this reading can be trusted.</param>
public readonly record struct SignalValue(
    string SignalId,
    double Value,
    string Unit,
    DateTimeOffset TimestampUtc,
    SignalQuality Quality)
{
    /// <summary>True when the value is recent enough to display as a number.</summary>
    public bool IsUsable => Quality is SignalQuality.Live or SignalQuality.Simulated;

    /// <summary>A reading that has never arrived.</summary>
    public static SignalValue Missing(string signalId, string unit = "") =>
        new(signalId, double.NaN, unit, DateTimeOffset.MinValue, SignalQuality.Unavailable);

    /// <summary>The same reading, re-flagged as stale. Used by the bus when a value ages out.</summary>
    public SignalValue AsStale() => this with { Quality = SignalQuality.Stale };
}
