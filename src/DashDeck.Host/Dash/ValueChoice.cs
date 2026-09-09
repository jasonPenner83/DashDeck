using DashDeck.Core.Catalog;
using DashDeck.Host.Sensors;

namespace DashDeck.Host.Dash;

/// <summary>
/// One thing a card can be pointed at, as the editor offers it.
/// </summary>
/// <remarks>
/// Was <c>SignalChoice</c>, and the rename is the point rather than tidying: it now describes
/// tablet sensors as well as vehicle signals, and a type called <c>SignalChoice</c> holding
/// <c>attitude.pitch</c> would be a small lie in a codebase that spends a lot of effort not
/// telling those.
/// <para>
/// A projection of whichever catalog it came from, never the catalog's own type. A
/// <c>SignalDefinition</c> carries a mode, a PID, a bus and a decode spec, and the UI has no
/// business with any of it — components subscribe to named things and know nothing about
/// PIDs, and an editor that picks them is held to the same line.
/// </para>
/// </remarks>
/// <param name="Id">Catalog id, e.g. <c>vehicle.speed</c> or <c>motion.lateralG</c>.</param>
/// <param name="Name">Human name from the catalog.</param>
/// <param name="Unit">Unit symbol the readings arrive in.</param>
/// <param name="DefaultRateHz">A sensible rate. Meaningless for sensors, which cost nothing.</param>
/// <param name="Min">Bottom of the declared range, when there is one.</param>
/// <param name="Max">Top of the declared range, when there is one.</param>
/// <param name="Source">Which catalog defined it.</param>
public sealed record ValueChoice(
    string Id,
    string Name,
    string Unit,
    double DefaultRateHz,
    double? Min,
    double? Max,
    CardSource Source,
    string Category)
{
    /// <summary>The name as the dash draws it.</summary>
    public string Caption => Name.ToUpperInvariant();

    /// <summary>
    /// The quiet second line in the picker.
    /// </summary>
    /// <remarks>
    /// Sensors say so. They behave differently in two ways a person choosing one should know
    /// about: they cost nothing against the request budget, and most of them show nothing at
    /// all until the mount has been levelled.
    /// </remarks>
    public string Detail => Source is CardSource.Sensor
        ? $"{Id}  ·  {Unit}  ·  sensor"
        : string.IsNullOrEmpty(Unit) ? Id : $"{Id}  ·  {Unit}";

    /// <summary>True when this can be drawn as a bar, which needs a range to be a fraction of.</summary>
    public bool HasRange => Min is not null && Max is not null && Max > Min;

    /// <summary>Which display units a card on this may offer.</summary>
    public IReadOnlyList<DisplayUnit> UnitChoices => DisplayUnits.ChoicesFor(Unit);

    /// <summary>Project the signal catalog into what the editor can show.</summary>
    public static IEnumerable<ValueChoice> From(SignalCatalog catalog) =>
        catalog.Definitions.Select(d => new ValueChoice(
            d.Id, d.Name, d.Decode.Unit, d.DefaultRateHz, d.Min, d.Max, CardSource.Signal, d.Category));

    /// <summary>Project the sensor catalog the same way. Tablet sensors and the phone's GPS
    /// (ADR-0027) are their own groups, so the picker separates the truck's opinion from the
    /// tablet's from the phone's.</summary>
    public static IEnumerable<ValueChoice> From(SensorCatalog catalog) =>
        catalog.Definitions.Select(d => new ValueChoice(
            d.Id, d.Name, d.Unit, d.DefaultRateHz, d.Min, d.Max, CardSource.Sensor,
            d.Source is SensorSource.Gps ? "Location" : "Tablet Sensors"));

    /// <summary>
    /// Everything a card can be pointed at, signals first.
    /// </summary>
    /// <remarks>
    /// Signals lead because they are what the dash is for; sensors follow because they are
    /// the tablet's opinion until the truck has one. Sorted within each group by name, so the
    /// list is stable as catalogs grow.
    /// </remarks>
    public static IReadOnlyList<ValueChoice> All(SignalCatalog signals, SensorCatalog sensors) =>
    [
        .. From(signals).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        .. From(sensors).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
    ];
}
