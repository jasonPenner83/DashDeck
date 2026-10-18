using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// Address search (ADR-0065): what the dash sends, what it makes of the answer, and the keyboard
/// it is typed on.
/// </summary>
/// <remarks>
/// All three are pure for the same reason: the parts that matter are the ones that cannot be tried
/// in a truck. A geocoder's usage policy is not a thing to remember at a call site — it is one
/// request a second, an identifying agent, no auto-complete and a cache — so it is code, and this
/// is where it is held to account.
/// </remarks>
public sealed class AddressSearchTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // --- What is worth sending ---

    [Fact]
    public void Whitespace_is_collapsed_so_the_same_search_is_the_same_search()
    {
        // For the cache as much as the geocoder: a double space would miss an answer already held
        // and spend a request the policy counts.
        Assert.Equal("123 Main Street", AddressQuery.Normalise("  123   Main  Street  "));
        Assert.Equal("123 Main Street", AddressQuery.Normalise("123 Main Street"));
        Assert.Equal(
            AddressQuery.CacheKey("123  main street"),
            AddressQuery.CacheKey(" 123 Main Street "));
    }

    [Fact]
    public void A_query_too_short_to_mean_anything_is_never_sent()
    {
        Assert.False(AddressQuery.IsSearchable(null));
        Assert.False(AddressQuery.IsSearchable("  "));
        Assert.False(AddressQuery.IsSearchable("ab"));
        Assert.True(AddressQuery.IsSearchable("R3C"));
        Assert.True(AddressQuery.IsSearchable("123 Main"));
    }

    [Fact]
    public void A_refusal_says_what_to_do_about_it()
    {
        Assert.Equal("Type an address.", AddressQuery.WhyNot(""));
        Assert.Contains("3 at least", AddressQuery.WhyNot("ab"), StringComparison.Ordinal);
        Assert.Null(AddressQuery.WhyNot("123 Main"));
    }

    [Fact]
    public void A_query_is_cut_rather_than_refused_when_it_runs_long()
    {
        var long_ = new string('a', AddressQuery.MaxLength + 40);

        Assert.Equal(AddressQuery.MaxLength, AddressQuery.Normalise(long_).Length);
        Assert.True(AddressQuery.IsSearchable(long_));
    }

    [Fact]
    public void The_cache_key_folds_case_without_asking_the_locale()
    {
        // A Turkish locale lower-cases I to a dotless one, which would make a search miss its own
        // cache entry on a tablet set to it.
        Assert.Equal(AddressQuery.CacheKey("Main Street"), AddressQuery.CacheKey("MAIN STREET"));
        Assert.Equal("ISABEL", AddressQuery.CacheKey("isabel"));
    }

    // --- One request a second, as the policy asks ---

    [Fact]
    public void The_first_request_is_always_allowed()
    {
        Assert.True(AddressQuery.MayAsk(Noon, null));
        Assert.Equal(TimeSpan.Zero, AddressQuery.Wait(Noon, null));
    }

    [Fact]
    public void A_second_request_within_the_second_is_refused()
    {
        // Checked rather than trusted to the UI: the search is behind a deliberate tap, but a
        // double-tap is one event on a rough road.
        Assert.False(AddressQuery.MayAsk(Noon.AddMilliseconds(200), Noon));
        Assert.True(AddressQuery.MayAsk(Noon.AddSeconds(1), Noon));
        Assert.True(AddressQuery.MayAsk(Noon.AddSeconds(5), Noon));
    }

    [Fact]
    public void How_long_to_wait_is_answerable_so_a_caption_can_say_it()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), AddressQuery.Wait(Noon.AddMilliseconds(600), Noon));
        Assert.Equal(TimeSpan.Zero, AddressQuery.Wait(Noon.AddSeconds(2), Noon));
    }

    // --- The request ---

    [Fact]
    public void The_search_goes_to_openstreetmaps_geocoder_with_the_query_escaped()
    {
        var url = Geocoder.Url("123 Main Street, Winnipeg");

        Assert.StartsWith(Geocoder.Endpoint, url, StringComparison.Ordinal);
        Assert.Contains("q=123%20Main%20Street%2C%20Winnipeg", url, StringComparison.Ordinal);
        Assert.Contains("format=jsonv2", url, StringComparison.Ordinal);
        Assert.Contains("limit=5", url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_is_biased_to_where_the_truck_is_but_not_fenced_in()
    {
        // "Main Street" from a cab in Manitoba should find the one up the road, and a genuinely
        // distant address should still be findable — so a viewbox, and no "bounded".
        var url = Geocoder.Url("Main Street", new MapCamera(-97.1384, 49.8951, 15, 0));

        Assert.Contains("viewbox=-98.1384,48.8951,-96.1384,50.8951", url, StringComparison.Ordinal);
        Assert.DoesNotContain("bounded=1", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bias_box_never_runs_off_the_top_of_the_world()
    {
        var url = Geocoder.Url("anywhere", new MapCamera(0, 84.5, 10, 0), degrees: 5);

        Assert.Contains("viewbox=-5,79.5,5,85.05113", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_position_the_search_is_still_made()
    {
        // No fix is not a reason to refuse a search: you may be looking up where to go next.
        var url = Geocoder.Url("Winnipeg");

        Assert.DoesNotContain("viewbox", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Whoever_is_asking_says_who_they_are_and_where_to_complain()
    {
        // A generic agent is grounds for being blocked under the policy.
        var agent = Geocoder.UserAgent("0.10.0");

        Assert.StartsWith("DashDeck/0.10.0", agent, StringComparison.Ordinal);
        Assert.Contains("github.com", agent, StringComparison.Ordinal);
    }

    // --- The answer ---

    private const string TwoResults = """
        [
          {
            "lat": "49.8951", "lon": "-97.1384",
            "name": "",
            "display_name": "123 Main Street, Winnipeg, Manitoba, R3C 1A1, Canada"
          },
          {
            "lat": "49.8844", "lon": "-97.1470",
            "name": "Tim Hortons",
            "display_name": "456 Portage Avenue, Winnipeg, Manitoba, Canada"
          }
        ]
        """;

    [Fact]
    public void A_result_is_split_into_the_line_you_read_and_the_line_that_tells_two_apart()
    {
        var results = Geocoder.Parse(TwoResults);

        Assert.Equal(2, results.Count);

        Assert.Equal("123 Main Street", results[0].Label);
        Assert.Equal("Winnipeg, Manitoba, R3C 1A1, Canada", results[0].Detail);
        Assert.Equal(49.8951, results[0].Latitude, 4);
        Assert.Equal(-97.1384, results[0].Longitude, 4);
    }

    [Fact]
    public void A_place_with_a_name_of_its_own_is_shown_by_it()
    {
        // A shop reads better by its name than by its house number, and the address stays below.
        var found = Geocoder.Parse(TwoResults)[1];

        Assert.Equal("Tim Hortons", found.Label);
        Assert.StartsWith("456 Portage Avenue", found.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinates_arrive_as_strings_and_are_read_invariant()
    {
        // The tablet's locale writes a decimal comma, which would turn 49.8951 into 498951 and
        // put the truck in the Pacific.
        var comma = (System.Globalization.CultureInfo)
            System.Globalization.CultureInfo.InvariantCulture.Clone();

        comma.NumberFormat.NumberDecimalSeparator = ",";
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = comma;
            Assert.Equal("49,8951", 49.8951.ToString("0.####"));

            Assert.Equal(49.8951, Geocoder.Parse(TwoResults)[0].Latitude, 4);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_result_knows_the_camera_that_would_show_it()
    {
        var found = Geocoder.Parse(TwoResults)[0];

        Assert.Equal(49.8951, found.Camera.Latitude, 4);
        Assert.Equal(MapCamera.DrivingZoom, found.Camera.Zoom);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"error":"unable to geocode"}""")]
    [InlineData("[]")]
    public void Anything_unexpected_is_no_results_rather_than_a_crash_in_the_cab(string? json)
    {
        Assert.Empty(Geocoder.Parse(json));
    }

    [Fact]
    public void A_result_missing_its_coordinates_is_dropped_and_the_rest_are_kept()
    {
        // Somebody else's JSON over a connection that drops. One bad entry must not lose the
        // others, and a result with no position is not a destination.
        var results = Geocoder.Parse("""
            [
              { "display_name": "Nowhere" },
              { "lat": "fifty", "lon": "-97", "display_name": "Also nowhere" },
              { "lat": "49.9", "lon": "-97.1", "display_name": "Somewhere, Manitoba" }
            ]
            """);

        Assert.Single(results);
        Assert.Equal("Somewhere", results[0].Label);
    }

}

/// <summary>
/// The gate on typing a destination (ADR-0058's distraction rule).
/// </summary>
public sealed class SearchGateTests
{
    [Fact]
    public void Stopped_is_allowed()
    {
        Assert.True(SearchGate.MayType(0));
        Assert.True(SearchGate.MayType(4.9));
        Assert.True(SearchGate.MayType(SearchGate.MaxSpeedKmh));
    }

    [Fact]
    public void Moving_is_refused_and_says_so_with_the_speed()
    {
        Assert.False(SearchGate.MayType(30));

        var why = SearchGate.WhyNot(90);

        Assert.NotNull(why);
        Assert.Contains("Stop first", why, StringComparison.Ordinal);
        Assert.Contains("90 km/h", why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_speed_is_allowed_which_is_the_known_hole()
    {
        // No position means no map, so search is unreachable anyway; what is left is a fix that
        // went stale while moving. Refusing on unknown would lock out every desk and every parked
        // truck with the phone off, which is where this is mostly used.
        Assert.True(SearchGate.MayType(null));
        Assert.True(SearchGate.MayType(double.NaN));
        Assert.Null(SearchGate.WhyNot(null));
    }

    [Fact]
    public void It_is_the_same_threshold_the_compass_already_uses()
    {
        // One number rather than two that nearly agree (ADR-0027's moving-heading rule).
        Assert.Equal(5, SearchGate.MaxSpeedKmh);
    }
}
