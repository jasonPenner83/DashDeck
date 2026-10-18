using DashDeck.Core.Maps;

namespace DashDeck.Host.Dash;

/// <summary>
/// One line of words the dash knows and a card can show.
/// </summary>
/// <remarks>
/// A function rather than a value, so nothing has to be pushed at this and nothing goes stale: the
/// card asks, the closure reads whatever the view-model holds now, and a destination chosen a
/// second ago is on the card a moment later with no wiring between them.
/// </remarks>
/// <param name="Id">The id a card stores, e.g. <c>nav.destinationName</c>.</param>
/// <param name="Name">What the picker calls it.</param>
/// <param name="Category">Which group it sorts under in the picker.</param>
/// <param name="Read">Where the words come from. Null or blank means there are none right now.</param>
public sealed record ReadoutDefinition(
    string Id,
    string Name,
    string Category,
    Func<string?> Read);

/// <summary>
/// The words the dash can show on a card (ADR-0067).
/// </summary>
/// <remarks>
/// <b>Not a JSON catalog, unlike the other two, and deliberately.</b> A signal or a sensor is a
/// thing the user can add — a PID they discovered, a channel their tablet happens to have — and
/// the catalogs are files so that adding one is an edit rather than a build. A readout is not like
/// that: it is a particular fact the dash already holds, reachable only through the code that
/// holds it, so a file naming one would need a matching closure compiled in anyway. Declaring them
/// here keeps the pair in one place instead of two that can disagree.
/// <para>
/// It is also why this stays small on purpose. Every entry is a decision that a fact is worth a
/// card, not a row anyone can add.
/// </para>
/// </remarks>
public sealed class ReadoutCatalog
{
    private readonly Dictionary<string, ReadoutDefinition> _byId;

    public ReadoutCatalog(IEnumerable<ReadoutDefinition> definitions) =>
        _byId = definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>No readouts at all. What a dash built without a map has.</summary>
    public static ReadoutCatalog Empty { get; } = new([]);

    public IReadOnlyCollection<ReadoutDefinition> Definitions => _byId.Values;

    public int Count => _byId.Count;

    public bool TryGet(string id, out ReadoutDefinition definition) =>
        _byId.TryGetValue(id, out definition!);

    /// <summary>The words that come with a destination and a route to it.</summary>
    /// <remarks>
    /// The other half of ADR-0066. The numbers from a searched place became sensors; these are the
    /// things about it that are not numbers and never could be — a name, an address, what kind of
    /// place it is, when it is open, how to telephone it.
    /// <para>
    /// Everything here comes off the search request that has already been made, so a card showing
    /// a shop's opening hours costs nothing against Nominatim's usage policy.
    /// </para>
    /// </remarks>
    public static ReadoutCatalog ForNavigation(
        Func<GeocodeResult?> destination,
        Func<Route?> route) =>
        new(
        [
            new("nav.destinationName", "Destination", "Navigation", () => destination()?.Label),
            new("nav.destinationAddress", "Destination Address", "Navigation", () => destination()?.Detail),
            new("nav.destinationKind", "Destination Kind", "Navigation", () => destination()?.Details.Kind),
            new("nav.destinationHours", "Opening Hours", "Navigation", () => destination()?.Details.Hours),
            new("nav.destinationPhone", "Telephone", "Navigation", () => destination()?.Details.Phone),
            new("nav.destinationWebsite", "Website", "Navigation", () => destination()?.Details.Website),
            new("nav.routeSummary", "Route", "Navigation", () => route()?.Summary),
        ]);
}
