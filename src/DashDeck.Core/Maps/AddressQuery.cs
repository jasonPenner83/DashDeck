using System.Globalization;

namespace DashDeck.Core.Maps;

/// <summary>
/// What the dash will and will not send to a geocoder (ADR-0065).
/// </summary>
/// <remarks>
/// Nominatim's usage policy is the reason this is a type rather than a <c>Trim()</c> at the call
/// site. It is somebody else's donated infrastructure, used under an acceptable-use policy, and
/// the policy's requirements are code here so they cannot be forgotten: no request for a query too
/// short to mean anything, no repeat of a query just asked, and no request at all more than once a
/// second.
/// </remarks>
public static class AddressQuery
{
    /// <summary>
    /// The shortest query worth sending.
    /// </summary>
    /// <remarks>
    /// Two characters matches most of the planet and wastes a request the policy counts. Three is
    /// enough for a postal-code fragment, which is the shortest useful thing anyone types.
    /// </remarks>
    public const int MinLength = 3;

    /// <summary>The longest the field accepts. Beyond this it is not an address.</summary>
    public const int MaxLength = 120;

    /// <summary>The least time between two requests, as the policy requires.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The query as it will be sent: trimmed, with runs of whitespace collapsed.
    /// </summary>
    /// <remarks>
    /// Collapsing matters for the cache as much as for the geocoder: <c>"main st"</c> typed with a
    /// double space is the same search, and a cache that missed on it would send a second request
    /// for an answer already held.
    /// </remarks>
    public static string Normalise(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var parts = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', parts);

        return joined.Length > MaxLength ? joined[..MaxLength].TrimEnd() : joined;
    }

    /// <summary>Whether a query is worth a request.</summary>
    public static bool IsSearchable(string? query) => Normalise(query).Length >= MinLength;

    /// <summary>
    /// The key a result is cached under: the normalised query, case-folded.
    /// </summary>
    /// <remarks>
    /// Invariant upper-casing, not the current culture's. A Turkish locale lower-cases <c>I</c> to
    /// a dotless <c>ı</c>, which would make the same search miss its own cache entry.
    /// </remarks>
    public static string CacheKey(string? query) =>
        Normalise(query).ToUpperInvariant();

    /// <summary>
    /// Whether enough time has passed since the last request.
    /// </summary>
    /// <remarks>
    /// The policy asks for no more than one request a second. This is checked rather than trusted
    /// to the UI: the search is already behind a deliberate tap, but a double-tap is one event in
    /// a vehicle on a rough road.
    /// </remarks>
    public static bool MayAsk(DateTimeOffset nowUtc, DateTimeOffset? lastAskedUtc) =>
        lastAskedUtc is not { } last || nowUtc - last >= MinInterval;

    /// <summary>How long until the next request is allowed, for a caption that says so.</summary>
    public static TimeSpan Wait(DateTimeOffset nowUtc, DateTimeOffset? lastAskedUtc)
    {
        if (lastAskedUtc is not { } last)
        {
            return TimeSpan.Zero;
        }

        var waited = nowUtc - last;
        return waited >= MinInterval ? TimeSpan.Zero : MinInterval - waited;
    }

    /// <summary>
    /// Why a query cannot be searched, or null when it can.
    /// </summary>
    public static string? WhyNot(string? query) => Normalise(query).Length switch
    {
        0 => "Type an address.",
        < MinLength => $"A few more letters — {MinLength} at least.",
        _ => null,
    };

    /// <summary>A coordinate for a URL, invariant and rounded to about a metre.</summary>
    public static string Coordinate(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}
