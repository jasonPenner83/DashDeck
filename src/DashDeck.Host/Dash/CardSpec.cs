using System.Text.Json.Serialization;

namespace DashDeck.Host.Dash;

/// <summary>How a card draws its value.</summary>
public enum CardStyle
{
    /// <summary>A large number and its unit. The default, and right for most signals.</summary>
    Number,

    /// <summary>A number with a fill bar beneath it. For signals with a known range.</summary>
    Bar,
}

/// <summary>
/// One card on the dash, as it is stored.
/// </summary>
/// <remarks>
/// This is a widget <em>instance</em>, which is not the same thing as ADR-0012's widget
/// <em>definition</em> and the distinction is load-bearing. A definition says what a
/// TRANS TEMP card <i>is</i> and ships with whoever wrote it; an instance says "I put one
/// here, two columns wide, in Fahrenheit". They look identical while every card is a
/// built-in signal card, and they stop being identical the moment a card arrives from
/// <c>plugins/</c> — at which point <see cref="SignalId"/> becomes one kind of source among
/// several and the rest of this record is still exactly right.
/// <para>
/// Tolerant in both directions, for the same reason <c>UserSettings</c> is: every property
/// has a default and unknown ones are ignored, so a dashboard written by an older build
/// loads into a newer one and back again. Enums are stored as text and parsed leniently —
/// a value this build does not recognise falls back rather than throwing away the card.
/// </para>
/// </remarks>
public sealed record CardSpec
{
    /// <summary>Stable identity for this instance. Survives reordering and re-editing.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Catalog signal id, e.g. <c>vehicle.speed</c>.</summary>
    [JsonPropertyName("signal")]
    public string SignalId { get; init; } = "";

    /// <summary>The caption. Empty means "use the catalog's name", which is usually right.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>What survives when the request budget is oversubscribed.</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "Normal";

    /// <summary>
    /// Requested rate. Zero means the catalog's default, which the arbiter already
    /// substitutes — so an unset rate is honest rather than merely unspecified.
    /// </summary>
    [JsonPropertyName("rateHz")]
    public double RateHz { get; init; }

    /// <summary>Numeric format for the value, e.g. <c>0</c> or <c>0.0</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "0";

    /// <summary>Width in grid columns: 1 (270) or 2 (560). Three columns fit across.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; } = 1;

    /// <summary>Number or Bar.</summary>
    [JsonPropertyName("style")]
    public string Style { get; init; } = "Number";

    /// <summary>Display unit. <c>Auto</c> shows whatever the catalog reports.</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "Auto";

    /// <summary>The style, parsed. An unrecognised name renders as a number rather than nothing.</summary>
    [JsonIgnore]
    public CardStyle ParsedStyle =>
        Enum.TryParse<CardStyle>(Style, ignoreCase: true, out var style) ? style : CardStyle.Number;

    /// <summary>The priority, parsed. Unrecognised falls to Normal.</summary>
    [JsonIgnore]
    public DashDeck.Abstractions.SignalPriority ParsedPriority =>
        Enum.TryParse<DashDeck.Abstractions.SignalPriority>(Priority, ignoreCase: true, out var priority)
            ? priority
            : DashDeck.Abstractions.SignalPriority.Normal;

    /// <summary>The display unit, parsed. Unrecognised falls to Auto.</summary>
    [JsonIgnore]
    public DisplayUnit ParsedUnit =>
        Enum.TryParse<DisplayUnit>(Unit, ignoreCase: true, out var unit) ? unit : DisplayUnit.Auto;

    /// <summary>Columns actually occupied. Clamped, because a stored 7 must not break the packer.</summary>
    [JsonIgnore]
    public int Columns => Math.Clamp(Width, 1, BandGrid.ColumnsPerRow);
}
