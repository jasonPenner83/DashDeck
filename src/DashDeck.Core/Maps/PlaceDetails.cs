namespace DashDeck.Core.Maps;

/// <summary>
/// What is known about a place beyond where it is (ADR-0065's addendum).
/// </summary>
/// <remarks>
/// All of it comes back on the <em>same</em> search request — <c>extratags=1</c> and
/// <c>addressdetails=1</c> are output options, not extra calls — so the popup costs nothing
/// against a usage policy that counts requests. That is the whole reason it is worth having.
/// <para>
/// Every field is optional and most places have none of them: OpenStreetMap knows what a house is
/// and rarely anything else about it. A business is where this pays off.
/// </para>
/// </remarks>
/// <param name="Kind">What it is, in words — "Fast food", "Fuel station", "House".</param>
/// <param name="Website">Its site, if OSM has one.</param>
/// <param name="Phone">Its number, if OSM has one.</param>
/// <param name="Hours">Opening hours, as OSM writes them.</param>
public readonly record struct PlaceDetails(
    string? Kind = null,
    string? Website = null,
    string? Phone = null,
    string? Hours = null)
{
    /// <summary>True when there is anything at all worth putting in a popup.</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Kind)
        || !string.IsNullOrWhiteSpace(Website)
        || !string.IsNullOrWhiteSpace(Phone)
        || !string.IsNullOrWhiteSpace(Hours);

    /// <summary>
    /// A readable name for an OSM class and type — <c>amenity</c>/<c>fast_food</c> becomes
    /// "Fast food".
    /// </summary>
    /// <remarks>
    /// OSM's own words are machine words, and "fast_food" on a dash reads like a bug. The generic
    /// containers are dropped rather than shown: "Amenity" and "Building" say nothing a person
    /// wants, so a place whose only description is one of those gets no kind line at all.
    /// </remarks>
    public static string? Describe(string? osmClass, string? osmType)
    {
        var type = Humanise(osmType);

        if (type is not null && !Generic(type))
        {
            return type;
        }

        var kind = Humanise(osmClass);
        return kind is not null && !Generic(kind) ? kind : null;
    }

    /// <summary>
    /// Words that describe nothing worth reading.
    /// </summary>
    /// <remarks>
    /// The kind line exists to identify a <em>business</em> — a cafe, a fuel station, a hardware
    /// shop. "House" is in here with the machine words because you have just typed the address of
    /// a house: telling you it is a house is worse than silence.
    /// </remarks>
    private static bool Generic(string word) =>
        word is "Yes" or "Amenity" or "Building" or "Place" or "House" or "Residential";

    private static string? Humanise(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        var spaced = word.Trim().Replace('_', ' ');

        return spaced.Length == 0
            ? null
            : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
