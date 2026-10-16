using System.Globalization;
using System.Text.Json;

namespace DashDeck.Core.Maps;

/// <summary>
/// One place a search found.
/// </summary>
/// <param name="Label">The short name, big enough to read at a glance — the street address.</param>
/// <param name="Detail">Where it is: town, province, country.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
public readonly record struct GeocodeResult(string Label, string Detail, double Latitude, double Longitude)
{
    /// <summary>The camera that would put this on screen.</summary>
    public MapCamera Camera => new MapCamera(Longitude, Latitude, MapCamera.DrivingZoom, 0).Clamped();

    /// <summary>Both lines, for a list that has one line, and for <c>--shot</c>.</summary>
    public override string ToString() => Detail.Length == 0 ? Label : $"{Label} — {Detail}";
}

/// <summary>
/// Turning an address into a position (ADR-0065), through OpenStreetMap's own geocoder.
/// </summary>
/// <remarks>
/// <b>Nominatim, used politely.</b> It is donated infrastructure under an acceptable-use policy,
/// and the policy's requirements shape this type rather than being remembered at the call site:
/// one request a second at most, a <see cref="UserAgent"/> that identifies the application and
/// says where to complain, results cached so a repeat costs nothing, and — the one that decides
/// the whole interface — <b>no auto-complete</b>. The policy says plainly that auto-complete "is
/// not yet supported by Nominatim and you must not implement such a service on the client side
/// using the API", so the dash searches when a search button is pressed and never per keystroke.
/// <para>
/// The HTTP call is not here. It arrives as a function, the way <c>UpdateService</c> takes one, so
/// the URL, the parsing and the politeness are all tested without a network.
/// </para>
/// <para>
/// <b>Online only</b>, as ADR-0058 decided. There is no offline answer yet; when there is, it is
/// Photon beside GraphHopper — Apache-2.0, built on Nominatim's own import, and GraphHopper
/// already publishes its dumps (Q46).
/// </para>
/// </remarks>
public static class Geocoder
{
    /// <summary>Where the search goes.</summary>
    public const string Endpoint = "https://nominatim.openstreetmap.org/search";

    /// <summary>
    /// How many results to ask for.
    /// </summary>
    /// <remarks>
    /// Five. A touch list in a vehicle that needs scrolling to reach the answer is a list that is
    /// too long, and each result is bytes over the truck's connection.
    /// </remarks>
    public const int Limit = 5;

    /// <summary>
    /// Who is asking.
    /// </summary>
    /// <remarks>
    /// The policy requires an identifiable application with a way to make contact, and a generic
    /// agent is grounds for being blocked. The repository is the contact: it is public, and an
    /// issue there reaches the person responsible.
    /// </remarks>
    public static string UserAgent(string version) =>
        $"DashDeck/{version} (+https://github.com/jasonPenner83/DashDeck)";

    /// <summary>
    /// The search URL.
    /// </summary>
    /// <remarks>
    /// Biased towards where the truck is, when it is known: a search for "Main Street" from a
    /// cab in Manitoba should find the one up the road rather than the most famous one on the
    /// planet. The bias is a <c>viewbox</c> without <c>bounded</c>, so a genuinely distant
    /// address is still found — it is a preference, not a fence.
    /// </remarks>
    /// <param name="query">What was typed. Normalised here, so callers need not.</param>
    /// <param name="near">Where the truck is, or null.</param>
    /// <param name="degrees">Half the width of the preferred box, in degrees. About 110 km at 1.</param>
    public static string Url(string? query, MapCamera? near = null, double degrees = 1.0)
    {
        var normalised = AddressQuery.Normalise(query);
        var url = $"{Endpoint}?q={Uri.EscapeDataString(normalised)}" +
            $"&format=jsonv2&limit={Limit.ToString(CultureInfo.InvariantCulture)}&accept-language=en";

        if (near is not { } centre || !centre.IsUsable)
        {
            return url;
        }

        var west = AddressQuery.Coordinate(centre.Longitude - degrees);
        var east = AddressQuery.Coordinate(centre.Longitude + degrees);
        var south = AddressQuery.Coordinate(Math.Max(centre.Latitude - degrees, -MapCamera.MaxLatitude));
        var north = AddressQuery.Coordinate(Math.Min(centre.Latitude + degrees, MapCamera.MaxLatitude));

        return $"{url}&viewbox={west},{south},{east},{north}";
    }

    /// <summary>
    /// Read what came back. An empty list for anything unexpected.
    /// </summary>
    /// <remarks>
    /// Every field is treated as optional, because this is somebody else's JSON over a connection
    /// that drops: a result missing its name still has coordinates and is worth offering, and a
    /// response that is not a list at all is no results rather than an exception in the cab.
    /// <para>
    /// Latitude and longitude arrive as <em>strings</em>, and are parsed invariant — the tablet's
    /// own locale writes a decimal comma, which would turn 49.8951 into 498951.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GeocodeResult> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<GeocodeResult>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryCoordinate(item, "lat", out var latitude)
                    || !TryCoordinate(item, "lon", out var longitude))
                {
                    continue;
                }

                var display = Text(item, "display_name");
                var (label, detail) = Split(Text(item, "name"), display);

                if (label.Length == 0)
                {
                    continue;
                }

                results.Add(new GeocodeResult(label, detail, latitude, longitude));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The two lines a result is shown as.
    /// </summary>
    /// <remarks>
    /// Nominatim's <c>display_name</c> is one long comma-separated line — "123 Main Street,
    /// Winnipeg, Manitoba, R3C 1A1, Canada" — which is unreadable at a glance and far too wide
    /// for the region. The first part is what you are looking for; the rest is what tells two
    /// Main Streets apart, so it stays, smaller, underneath.
    /// </remarks>
    private static (string Label, string Detail) Split(string name, string display)
    {
        var parts = display.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return (name, string.Empty);
        }

        // The name is preferred for the first line when there is one and it is not already the
        // first part: a shop or a landmark reads better by its own name than by its house number.
        var label = name.Length > 0 ? name : parts[0];
        var rest = name.Length > 0 && !parts[0].Equals(name, StringComparison.OrdinalIgnoreCase)
            ? parts
            : parts[1..];

        return (label, string.Join(", ", rest));
    }

    private static bool TryCoordinate(JsonElement item, string property, out double value)
    {
        value = 0;

        if (!item.TryGetProperty(property, out var found))
        {
            return false;
        }

        // Strings in jsonv2, but a number would be just as valid JSON and costs nothing to accept.
        var text = found.ValueKind switch
        {
            JsonValueKind.String => found.GetString(),
            JsonValueKind.Number => found.GetRawText(),
            _ => null,
        };

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
