using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// Directions (ADR-0065's addendum): what is asked of a public OSRM, and what is made of it.
/// </summary>
/// <remarks>
/// <b>Not the navigation ADR-0058 describes.</b> That is a bundled engine with a graph in the
/// deploy, so guidance survives a dead zone. This is how far, how long and which way, worked out
/// while stopped — and the instruction wording is ours, because OSRM sends a manoeuvre type and a
/// street name and leaves the sentence to the client. Which is exactly why it is tested.
/// </remarks>
public sealed class RoutingTests
{
    private static readonly MapCamera Here = new(-97.1384, 49.8951, 15, 0);
    private static readonly MapCamera There = new(-97.1470, 49.8844, 15, 0);

    private const string TwoTurns = """
        {
          "code": "Ok",
          "routes": [
            {
              "distance": 1463.2,
              "duration": 182.5,
              "geometry": {
                "type": "LineString",
                "coordinates": [[-97.1384, 49.8951], [-97.1420, 49.8900], [-97.1470, 49.8844]]
              },
              "legs": [
                {
                  "steps": [
                    { "distance": 445.0, "name": "Main Street",
                      "maneuver": { "type": "depart", "modifier": "straight" } },
                    { "distance": 573.2, "name": "Portage Avenue",
                      "maneuver": { "type": "turn", "modifier": "right" } },
                    { "distance": 445.0, "name": "Osborne Street",
                      "maneuver": { "type": "turn", "modifier": "left" } },
                    { "distance": 0.0, "name": "",
                      "maneuver": { "type": "arrive" } }
                  ]
                }
              ]
            }
          ]
        }
        """;

    // --- The request ---

    [Fact]
    public void A_route_is_asked_for_between_two_points_in_the_order_osrm_wants()
    {
        // Longitude first, which is the opposite of how everyone says it out loud.
        var url = Router.Url(Here, There);

        Assert.StartsWith(Router.Endpoint, url, StringComparison.Ordinal);
        Assert.Contains("/-97.1384,49.8951;-97.147,49.8844", url, StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_for_the_full_line_and_the_steps()
    {
        // A simplified overview is fewer bytes and visibly cuts corners at junctions, which on a
        // map reads as the route going the wrong way.
        var url = Router.Url(Here, There);

        Assert.Contains("overview=full", url, StringComparison.Ordinal);
        Assert.Contains("geometries=geojson", url, StringComparison.Ordinal);
        Assert.Contains("steps=true", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_router_is_credited_as_its_terms_require()
    {
        Assert.Contains("OSRM", Router.Attribution, StringComparison.Ordinal);
        Assert.Contains("OpenStreetMap", Router.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_held_to_the_same_one_a_second_as_the_search()
    {
        Assert.Equal(AddressQuery.MinInterval, Router.MinInterval);
    }

    // --- The answer ---

    [Fact]
    public void A_route_carries_its_distance_its_time_and_its_line()
    {
        var route = Router.Parse(TwoTurns);

        Assert.NotNull(route);
        Assert.Equal(1463.2, route!.Metres, 1);
        Assert.Equal(182.5, route.Seconds, 1);
        Assert.Equal(3, route.Shape.Count);
        Assert.True(route.HasShape);
        Assert.Equal(-97.1384, route.Shape[0].Longitude, 4);
        Assert.Equal(49.8951, route.Shape[0].Latitude, 4);
    }

    [Fact]
    public void The_summary_is_the_two_numbers_anyone_actually_wants()
    {
        Assert.Equal("1.5 km · 3 min", Router.Parse(TwoTurns)!.Summary);
    }

    [Fact]
    public void The_steps_read_as_sentences_rather_than_as_osrm_fields()
    {
        // OSRM sends no instruction text at all — a type, a modifier and a street name. The
        // wording is ours, which is why it is here and not in a view.
        var steps = Router.Parse(TwoTurns)!.Steps;

        Assert.Equal(4, steps.Count);
        Assert.Equal("Start on Main Street", steps[0].Instruction);
        Assert.Equal("Turn right onto Portage Avenue", steps[1].Instruction);
        Assert.Equal("Turn left onto Osborne Street", steps[2].Instruction);
        Assert.Equal("Arrive", steps[3].Instruction);
    }

    [Fact]
    public void Each_step_says_how_far_it_runs()
    {
        var steps = Router.Parse(TwoTurns)!.Steps;

        Assert.Equal("450 m", steps[0].Distance);
        Assert.Equal("570 m", steps[1].Distance);
    }

    [Theory]
    [InlineData("roundabout", "", "Highway 59", "Take the roundabout onto Highway 59")]
    [InlineData("off ramp", "right", "Highway 1", "Take the exit right onto Highway 1")]
    [InlineData("merge", "left", "Perimeter", "Merge left onto Perimeter")]
    [InlineData("fork", "slight right", "Bishop Grandin", "Keep slightly right onto Bishop Grandin")]
    [InlineData("end of road", "left", "Pembina", "At the end of the road, turn left onto Pembina")]
    [InlineData("turn", "uturn", "Main Street", "Turn back onto Main Street")]
    [InlineData("new name", "", "St Mary Road", "Continue on St Mary Road")]
    [InlineData("turn", "straight", "Main Street", "Continue on Main Street")]
    [InlineData("something unheard of", "", "Main Street", "Continue on Main Street")]
    [InlineData("continue", "", "", "Continue")]
    public void Every_manoeuvre_turns_into_something_a_person_can_follow(
        string type, string modifier, string road, string expected)
    {
        var json = $$"""
            { "routes": [ { "distance": 10, "duration": 10,
              "geometry": { "coordinates": [[0, 0], [1, 1]] },
              "legs": [ { "steps": [
                { "distance": 10, "name": "{{road}}",
                  "maneuver": { "type": "{{type}}", "modifier": "{{modifier}}" } } ] } ] } ] }
            """;

        Assert.Equal(expected, Router.Parse(json)!.Steps[0].Instruction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "code": "NoRoute", "routes": [] }""")]
    [InlineData("""{ "code": "Ok" }""")]
    [InlineData("""{ "routes": [ { "duration": 10, "geometry": { "coordinates": [[0,0],[1,1]] } } ] }""")]
    public void Anything_that_is_not_a_route_is_no_route_rather_than_a_crash(string? json)
    {
        // "No way to get there" and "the server said something odd" both come out as the dash
        // saying it could not find a route, which is the truth either way.
        Assert.Null(Router.Parse(json));
    }

    [Fact]
    public void A_route_with_no_line_is_no_route()
    {
        // A distance with nothing to draw would put a summary on screen over an empty map.
        Assert.Null(Router.Parse("""
            { "routes": [ { "distance": 100, "duration": 60,
              "geometry": { "coordinates": [[0, 0]] }, "legs": [] } ] }
            """));
    }

    // --- How the numbers read ---

    [Theory]
    [InlineData(0, "0 m")]
    [InlineData(94, "90 m")]
    [InlineData(850, "850 m")]
    [InlineData(999, "1000 m")]
    [InlineData(1000, "1.0 km")]
    [InlineData(1463, "1.5 km")]
    [InlineData(9949, "9.9 km")]
    [InlineData(34000, "34 km")]
    [InlineData(412500, "413 km")]
    public void Distance_is_written_the_way_a_person_reads_it(double metres, string expected)
    {
        Assert.Equal(expected, RouteSummary.Distance(metres));
    }

    [Theory]
    [InlineData(20, "under a minute")]
    [InlineData(90, "2 min")]
    [InlineData(1080, "18 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5100, "1 h 25 min")]
    [InlineData(32400, "9 h")]
    public void Duration_never_claims_a_precision_a_router_does_not_have(double seconds, string expected)
    {
        Assert.Equal(expected, RouteSummary.Duration(seconds));
    }

    [Fact]
    public void Nonsense_numbers_read_as_a_dash_rather_than_as_a_measurement()
    {
        Assert.Equal("—", RouteSummary.Distance(double.NaN));
        Assert.Equal("—", RouteSummary.Distance(-5));
        Assert.Equal("—", RouteSummary.Duration(double.NaN));
    }
}

/// <summary>What a search says about a place beyond where it is (ADR-0065's addendum).</summary>
public sealed class PlaceDetailsTests
{
    private const string Business = """
        [ { "lat": "49.8844", "lon": "-97.1470",
            "name": "Tim Hortons", "category": "amenity", "type": "cafe",
            "display_name": "Tim Hortons, 456 Portage Avenue, Winnipeg, Manitoba, Canada",
            "extratags": {
              "website": "https://www.timhortons.ca",
              "phone": "+1-204-555-0100",
              "opening_hours": "Mo-Su 05:00-23:00",
              "cuisine": "coffee_shop"
            } } ]
        """;

    [Fact]
    public void A_business_brings_its_details_with_it_on_the_same_request()
    {
        // extratags is an output option, not another call — which is the only reason this is
        // affordable under a policy that counts requests.
        var found = Geocoder.Parse(Business)[0];

        Assert.Equal("Cafe", found.Details.Kind);
        Assert.Equal("https://www.timhortons.ca", found.Details.Website);
        Assert.Equal("+1-204-555-0100", found.Details.Phone);
        Assert.Equal("Mo-Su 05:00-23:00", found.Details.Hours);
        Assert.True(found.Details.HasAny);
    }

    [Fact]
    public void The_request_asks_for_them()
    {
        var url = Geocoder.Url("tim hortons winnipeg");

        Assert.Contains("extratags=1", url, StringComparison.Ordinal);
        Assert.Contains("addressdetails=1", url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_house_has_nothing_to_say_and_says_nothing()
    {
        // The normal case. OSM knows what a house is and rarely anything else about it.
        var found = Geocoder.Parse("""
            [ { "lat": "49.9", "lon": "-97.1", "category": "place", "type": "house",
                "display_name": "123 Main Street, Winnipeg, Manitoba" } ]
            """)[0];

        Assert.False(found.Details.HasAny);
        Assert.Null(found.Details.Website);
    }

    [Fact]
    public void Osms_machine_words_are_turned_into_readable_ones()
    {
        Assert.Equal("Fast food", PlaceDetails.Describe("amenity", "fast_food"));
        Assert.Equal("Fuel", PlaceDetails.Describe("amenity", "fuel"));
    }

    [Fact]
    public void A_place_whose_only_description_says_nothing_gets_no_line()
    {
        // "Amenity" and "Building" on a dash are worse than silence.
        Assert.Null(PlaceDetails.Describe("building", "yes"));
        Assert.Null(PlaceDetails.Describe("amenity", ""));
        Assert.Null(PlaceDetails.Describe(null, null));
    }

    [Fact]
    public void A_site_is_found_whichever_way_the_mapper_spelled_the_tag()
    {
        var found = Geocoder.Parse("""
            [ { "lat": "49.9", "lon": "-97.1", "display_name": "Somewhere",
                "extratags": { "contact:website": "https://example.org",
                               "contact:phone": "204-555-0101" } } ]
            """)[0];

        Assert.Equal("https://example.org", found.Details.Website);
        Assert.Equal("204-555-0101", found.Details.Phone);
    }
}
