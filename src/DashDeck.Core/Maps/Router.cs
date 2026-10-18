using System.Globalization;
using System.Text.Json;

namespace DashDeck.Core.Maps;

/// <summary>One instruction on a route.</summary>
/// <param name="Instruction">What to do, in words — "Turn left onto Main Street".</param>
/// <param name="Metres">How far from the previous instruction to this one.</param>
public readonly record struct RouteStep(string Instruction, double Metres)
{
    /// <summary>The distance, in the units a person reads.</summary>
    public string Distance => RouteSummary.Distance(Metres);
}

/// <summary>
/// A way to get somewhere.
/// </summary>
/// <param name="Metres">How far, in total.</param>
/// <param name="Seconds">How long, by the router's reckoning.</param>
/// <param name="Shape">The line to draw, as longitude/latitude pairs.</param>
/// <param name="Steps">The instructions, in order.</param>
public sealed record Route(
    double Metres,
    double Seconds,
    IReadOnlyList<(double Longitude, double Latitude)> Shape,
    IReadOnlyList<RouteStep> Steps)
{
    /// <summary>"12.4 km · 18 min", for the strip that says whether it is worth going.</summary>
    public string Summary => $"{RouteSummary.Distance(Metres)} · {RouteSummary.Duration(Seconds)}";

    /// <summary>True when there is a line worth drawing.</summary>
    public bool HasShape => Shape.Count > 1;
}

/// <summary>How a distance and a duration are written, in one place.</summary>
public static class RouteSummary
{
    /// <summary>
    /// Metres as a person reads them.
    /// </summary>
    /// <remarks>
    /// Metric, and switching at a kilometre: "850 m" then "1.2 km" then "34 km". A single decimal
    /// below ten kilometres and none above, because the second decimal of a forty-kilometre drive
    /// is noise and takes space from the number that matters.
    /// </remarks>
    public static string Distance(double metres)
    {
        if (!double.IsFinite(metres) || metres < 0)
        {
            return "—";
        }

        if (metres < 1000)
        {
            // Away from zero, not .NET's default to-even: 445 m rounds to 450 the way a person
            // expects, where to-even gives 440 and 455 gives 460, which looks like a bug on
            // consecutive steps of the same route.
            var tens = Math.Round(metres / 10, MidpointRounding.AwayFromZero) * 10;

            return string.Create(CultureInfo.InvariantCulture, $"{tens:0} m");
        }

        var km = metres / 1000;

        return km < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{km:0.0} km")
            : string.Create(CultureInfo.InvariantCulture, $"{km:0} km");
    }

    /// <summary>
    /// Seconds as a person reads them: "4 min", "18 min", "1 h 25 min".
    /// </summary>
    /// <remarks>
    /// Never seconds. A router's estimate is not accurate to the minute, let alone the second, and
    /// showing one would claim a precision it does not have.
    /// </remarks>
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return "—";
        }

        var minutes = (int)Math.Round(seconds / 60);

        if (minutes < 1)
        {
            return "under a minute";
        }

        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
        }

        var hours = minutes / 60;
        var rest = minutes % 60;

        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min");
    }
}

/// <summary>
/// Working out how to get there (ADR-0065's addendum), through a public OSRM.
/// </summary>
/// <remarks>
/// <b>This is not the navigation ADR-0058 describes, and must not be mistaken for it.</b> That is
/// GraphHopper bundled as a child process with a routing graph in the deploy, so that guidance
/// works where there is no signal — the thing that matters on a rural highway. This is the display
/// half: how far, how long, which way, drawn on the map while you are stopped. No voice, no
/// re-routing, nothing that updates as you drive.
/// <para>
/// The engine is FOSSGIS's public OSRM, on the same terms as everything else the map leans on:
/// attribution, an identifying User-Agent, at most one request a second, and no heavy use. One
/// route when a button is pressed is well inside that; following a vehicle would not be, which is
/// another reason live guidance waits for the bundled engine.
/// </para>
/// </remarks>
public static class Router
{
    /// <summary>FOSSGIS's public OSRM, car profile.</summary>
    public const string Endpoint = "https://routing.openstreetmap.de/routed-car/route/v1/driving";

    /// <summary>Who to credit, as the terms require.</summary>
    public const string Attribution = "Routing by OSRM · FOSSGIS · © OpenStreetMap contributors";

    /// <summary>The least time between two route requests, matching the geocoder's.</summary>
    public static readonly TimeSpan MinInterval = AddressQuery.MinInterval;

    /// <summary>
    /// The URL for a route between two points.
    /// </summary>
    /// <remarks>
    /// <c>overview=full</c> and <c>geometries=geojson</c> because the line is drawn on a map that
    /// speaks GeoJSON already, and <c>steps=true</c> for the instructions. A simplified overview
    /// would be fewer bytes and visibly cuts corners at junctions, which on a map is read as the
    /// route going the wrong way.
    /// </remarks>
    public static string Url(MapCamera from, MapCamera to) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Endpoint}/{AddressQuery.Coordinate(from.Longitude)},{AddressQuery.Coordinate(from.Latitude)};" +
        $"{AddressQuery.Coordinate(to.Longitude)},{AddressQuery.Coordinate(to.Latitude)}" +
        $"?overview=full&geometries=geojson&steps=true&alternatives=false");

    /// <summary>
    /// Read a route, or null for anything that is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, and null rather than an empty route: "no way to get there"
    /// and "the server said something unexpected" both end up as the dash saying it could not find
    /// a route, which is the truth either way and all a person can act on.
    /// </remarks>
    public static Route? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("routes", out var routes)
                || routes.ValueKind != JsonValueKind.Array
                || routes.GetArrayLength() == 0)
            {
                return null;
            }

            var first = routes[0];

            if (!Number(first, "distance", out var metres) || !Number(first, "duration", out var seconds))
            {
                return null;
            }

            var shape = Shape(first);

            return shape.Count < 2 ? null : new Route(metres, seconds, shape, Steps(first));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<(double Longitude, double Latitude)> Shape(JsonElement route)
    {
        var shape = new List<(double, double)>();

        if (!route.TryGetProperty("geometry", out var geometry)
            || !geometry.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array)
        {
            return shape;
        }

        foreach (var pair in coordinates.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array
                && pair.GetArrayLength() >= 2
                && pair[0].TryGetDouble(out var longitude)
                && pair[1].TryGetDouble(out var latitude)
                && double.IsFinite(longitude)
                && double.IsFinite(latitude))
            {
                shape.Add((longitude, latitude));
            }
        }

        return shape;
    }

    private static List<RouteStep> Steps(JsonElement route)
    {
        var steps = new List<RouteStep>();

        if (!route.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array)
        {
            return steps;
        }

        foreach (var leg in legs.EnumerateArray())
        {
            if (!leg.TryGetProperty("steps", out var found) || found.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var step in found.EnumerateArray())
            {
                Number(step, "distance", out var metres);

                if (Instruction(step) is { Length: > 0 } instruction)
                {
                    steps.Add(new RouteStep(instruction, metres));
                }
            }
        }

        return steps;
    }

    /// <summary>
    /// One step as a sentence.
    /// </summary>
    /// <remarks>
    /// OSRM gives a manoeuvre type, a modifier and a street name, and leaves the wording to the
    /// client — there is no instruction text in the response. So the words are ours, which is why
    /// they are here, in one place, tested, rather than assembled in a view.
    /// </remarks>
    private static string Instruction(JsonElement step)
    {
        var road = Text(step, "name");

        if (!step.TryGetProperty("maneuver", out var manoeuvre) || manoeuvre.ValueKind != JsonValueKind.Object)
        {
            return road;
        }

        var type = Text(manoeuvre, "type");
        var modifier = Text(manoeuvre, "modifier");
        var onto = road.Length > 0 ? $" onto {road}" : string.Empty;

        return type switch
        {
            "depart" => road.Length > 0 ? $"Start on {road}" : "Start",
            "arrive" => "Arrive",
            "roundabout" or "rotary" => $"Take the roundabout{onto}",
            "merge" => $"Merge{Side(modifier)}{onto}",
            "on ramp" => $"Take the ramp{Side(modifier)}{onto}",
            "off ramp" => $"Take the exit{Side(modifier)}{onto}",
            "fork" => $"Keep{Side(modifier)}{onto}",
            "end of road" => $"At the end of the road, turn{Side(modifier)}{onto}",
            "continue" or "new name" => road.Length > 0 ? $"Continue on {road}" : "Continue",
            "turn" => modifier is "straight" or ""
                ? (road.Length > 0 ? $"Continue on {road}" : "Continue")
                : $"Turn{Side(modifier)}{onto}",
            _ => road.Length > 0 ? $"Continue on {road}" : "Continue",
        };
    }

    private static string Side(string modifier) => modifier switch
    {
        "left" => " left",
        "right" => " right",
        "sharp left" => " sharp left",
        "sharp right" => " sharp right",
        "slight left" => " slightly left",
        "slight right" => " slightly right",
        "uturn" => " back",
        _ => string.Empty,
    };

    private static bool Number(JsonElement element, string property, out double value)
    {
        value = 0;

        return element.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
