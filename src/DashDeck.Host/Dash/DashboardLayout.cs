using System.Text.Json.Serialization;

namespace DashDeck.Host.Dash;

/// <summary>
/// The cards the user has arranged, in order.
/// </summary>
/// <remarks>
/// Order is the whole layout. Cards flow into rows and rows into pages, so moving a card is
/// moving it in this list and nothing stores a row or a page number. That is what lets the
/// same dashboard render into two rows behind a four-band stage and three behind a
/// three-band one without anything being re-saved.
/// </remarks>
public sealed record DashboardLayout
{
    /// <summary>The cards, left to right and top to bottom.</summary>
    [JsonPropertyName("cards")]
    public IReadOnlyList<CardSpec> Cards { get; init; } = [];

    /// <summary>
    /// The dash as it ships.
    /// </summary>
    /// <remarks>
    /// Deliberately identical to the six cards the shell used to build in code, so an
    /// existing install sees no change on the first launch after this lands. The rates are
    /// the honest ones from before: the whole app shares one serialised link and asking for
    /// more than you need degrades everyone (ADR-0004).
    /// </remarks>
    public static DashboardLayout Default() => new()
    {
        Cards =
        [
            new CardSpec { Id = "speed", SignalId = "vehicle.speed", Label = "SPEED", Priority = "High", RateHz = 4, Format = "0" },
            new CardSpec { Id = "rpm", SignalId = "engine.rpm", Label = "RPM", Priority = "High", RateHz = 4, Format = "0" },
            new CardSpec { Id = "coolant", SignalId = "engine.coolantTemp", Label = "COOLANT", Priority = "Normal", RateHz = 0.5, Format = "0" },
            new CardSpec { Id = "fuel", SignalId = "fuel.levelPercent", Label = "FUEL", Priority = "Low", RateHz = 0.2, Format = "0", Style = "Bar" },
            new CardSpec { Id = "load", SignalId = "engine.load", Label = "ENGINE LOAD", Priority = "Normal", RateHz = 2, Format = "0", Style = "Bar" },
            new CardSpec { Id = "fuelrate", SignalId = "engine.fuelRate", Label = "FUEL RATE", Priority = "Normal", RateHz = 2, Format = "0.0" },
        ],
    };
}
