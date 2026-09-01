namespace DashDeck.Abstractions;

/// <summary>
/// How much a <see cref="SignalValue"/> can be trusted right now.
/// </summary>
/// <remarks>
/// Components are required to render quality rather than hide it. A confidently wrong
/// number on a dash is worse than a blank one.
/// </remarks>
public enum SignalQuality
{
    /// <summary>No value has been received yet, or the vehicle does not support this signal.</summary>
    Unavailable,

    /// <summary>Last value is older than the signal's staleness budget. Show it as stale, or not at all.</summary>
    Stale,

    /// <summary>A real, recent value from the vehicle.</summary>
    Live,

    /// <summary>
    /// A recent value from the synthetic vehicle. Never present this as real data —
    /// the distinction exists so mock data cannot be mistaken for a truck (ADR-0005).
    /// </summary>
    Simulated,
}
