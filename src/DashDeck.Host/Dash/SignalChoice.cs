using DashDeck.Core.Catalog;

namespace DashDeck.Host.Dash;

/// <summary>
/// One signal, as the card editor offers it.
/// </summary>
/// <remarks>
/// A projection of the catalog rather than the catalog itself, and the shape matters: the
/// editor needs a display name, a unit and a sensible starting rate, and it must not be
/// handed a <see cref="SignalDefinition"/> — that carries mode, PID, bus and a decode spec,
/// none of which the UI has any business knowing about. Components subscribe to named
/// signals and know nothing about PIDs; an editor that picks signals is held to the same
/// line.
/// </remarks>
/// <param name="Id">Catalog id, e.g. <c>vehicle.speed</c>.</param>
/// <param name="Name">Human name from the catalog, e.g. <c>Vehicle Speed</c>.</param>
/// <param name="Unit">Unit symbol the readings arrive in.</param>
/// <param name="DefaultRateHz">What the catalog considers a sensible rate for this signal.</param>
/// <param name="Min">Bottom of the declared physical range, when there is one.</param>
/// <param name="Max">Top of the declared physical range, when there is one.</param>
public sealed record SignalChoice(
    string Id,
    string Name,
    string Unit,
    double DefaultRateHz,
    double? Min,
    double? Max)
{
    /// <summary>The name as the dash draws it.</summary>
    public string Caption => Name.ToUpperInvariant();

    /// <summary>Name and unit together, for the picker.</summary>
    public string Detail => string.IsNullOrEmpty(Unit) ? Id : $"{Id}  ·  {Unit}";

    /// <summary>
    /// True when this signal can be drawn as a bar.
    /// </summary>
    /// <remarks>
    /// A bar needs a range to be a fraction of. The catalog's <c>min</c>/<c>max</c> already
    /// exist as a correctness guard on decoding, so the information is there — a signal
    /// without them gets a number and the editor says so rather than drawing a bar against
    /// a range it invented.
    /// </remarks>
    public bool HasRange => Min is not null && Max is not null && Max > Min;

    /// <summary>Which display units a card on this signal may offer.</summary>
    public IReadOnlyList<DisplayUnit> UnitChoices => DisplayUnits.ChoicesFor(Unit);

    /// <summary>Project the loaded catalog into what the editor can show, sorted by name.</summary>
    public static IReadOnlyList<SignalChoice> From(SignalCatalog catalog) =>
        [.. catalog.Definitions
            .Select(d => new SignalChoice(d.Id, d.Name, d.Decode.Unit, d.DefaultRateHz, d.Min, d.Max))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
}
