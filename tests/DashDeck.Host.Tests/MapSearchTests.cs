using System.Net.Http;
using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// Address search as the dash drives it (ADR-0065).
/// </summary>
/// <remarks>
/// No network: the HTTP call arrives as a function, so what is pinned here is the behaviour that
/// a usage policy and a moving vehicle demand — that nothing searches while typing, that a repeat
/// comes from the cache, that two taps in a second make one request, and that a destination cannot
/// be typed above walking pace.
/// </remarks>
public sealed class MapSearchTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private const string OneResult = """
        [ { "lat": "49.8951", "lon": "-97.1384",
            "display_name": "123 Main Street, Winnipeg, Manitoba, Canada" } ]
        """;

    private sealed class Server
    {
        public List<string> Asked { get; } = [];

        public string Reply { get; set; } = OneResult;

        public Exception? Throws { get; set; }

        public Task<string> FetchAsync(string url, CancellationToken ct)
        {
            Asked.Add(url);
            return Throws is not null ? Task.FromException<string>(Throws) : Task.FromResult(Reply);
        }
    }

    private static (MapSearchViewModel Search, Server Server, Clock Clock) Build(
        double? speed = 0, MapCamera? near = null)
    {
        var server = new Server();
        var clock = new Clock(Noon);
        var search = new MapSearchViewModel(server.FetchAsync, clock, () => near, () => speed);

        return (search, server, clock);
    }

    private static async Task TypeAsync(MapSearchViewModel search, string text)
    {
        foreach (var c in text)
        {
            var key = c == ' '
                ? new Key("SPACE", KeyAction.Space)
                : new Key(c.ToString().ToUpperInvariant());

            search.TypeCommand.Execute(key);
        }

        await Task.CompletedTask;
    }

    // --- No auto-complete, because the policy forbids it ---

    [Fact]
    public async Task Typing_never_searches()
    {
        // Nominatim's policy says plainly that auto-complete must not be implemented client-side.
        // This is the test that keeps a well-meaning "search as you type" out of the dash.
        var (search, server, _) = Build();

        await TypeAsync(search, "123 Main Street");

        Assert.Empty(server.Asked);
        Assert.Equal("123 MAIN STREET", search.Query);
    }

    [Fact]
    public async Task A_search_happens_when_the_button_is_pressed()
    {
        var (search, server, _) = Build();
        await TypeAsync(search, "123 Main");

        await search.SearchCommand.ExecuteAsync(null);

        Assert.Single(server.Asked);
        Assert.Contains("q=123%20MAIN", server.Asked[0], StringComparison.Ordinal);
        Assert.Single(search.Results);
        Assert.Equal("123 Main Street", search.Results[0].Label);
        Assert.True(search.HasResults);
    }

    // --- One request a second, and a cache ---

    [Fact]
    public async Task Two_taps_within_the_second_make_one_request()
    {
        // A double-tap is one event on a rough road, and the policy counts both.
        var (search, server, _) = Build();
        await TypeAsync(search, "123 Main");

        await search.SearchCommand.ExecuteAsync(null);
        search.ResetCommand.Execute(null);
        await TypeAsync(search, "456 Portage");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Single(server.Asked);
        Assert.Contains("one search a second", search.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_search_twice_is_answered_from_the_cache()
    {
        // What anyone does when the first result was not the one they meant — and it works with
        // the signal gone, which is the point.
        var (search, server, clock) = Build();
        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);

        clock.UtcNow = Noon.AddMinutes(1);
        search.ResetCommand.Execute(null);
        await TypeAsync(search, "123 main");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Single(server.Asked);
        Assert.Single(search.Results);
    }

    [Fact]
    public async Task A_different_search_after_a_second_is_asked_for_properly()
    {
        var (search, server, clock) = Build();
        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);

        clock.UtcNow = Noon.AddSeconds(2);
        search.ResetCommand.Execute(null);
        await TypeAsync(search, "456 Portage");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, server.Asked.Count);
    }

    // --- What it refuses, and what it says ---

    [Fact]
    public async Task A_query_too_short_is_not_sent_and_says_why()
    {
        var (search, server, _) = Build();
        await TypeAsync(search, "ab");

        await search.SearchCommand.ExecuteAsync(null);

        Assert.Empty(server.Asked);
        Assert.Contains("3 at least", search.Message, StringComparison.Ordinal);
        Assert.False(search.CanSearch);
    }

    [Fact]
    public async Task Nothing_found_is_said_as_advice_rather_than_as_a_failure()
    {
        var (search, server, _) = Build();
        server.Reply = "[]";

        await TypeAsync(search, "nowhere at all");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.False(search.HasResults);
        Assert.Contains("street and a town", search.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_that_failed_blames_the_connection()
    {
        // The tiles come over the same link, so the map is probably already saying so — naming
        // the likely cause beats naming the exception.
        var (search, server, _) = Build();
        server.Throws = new HttpRequestException("no such host");

        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Contains("needs a connection", search.Message, StringComparison.Ordinal);
        Assert.False(search.IsSearching);
        Assert.False(search.HasResults);
    }

    // --- The speed gate (ADR-0058) ---

    [Fact]
    public async Task Moving_means_nothing_can_be_typed_or_searched()
    {
        var (search, server, _) = Build(speed: 60);

        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, search.Query);
        Assert.Empty(server.Asked);
        Assert.False(search.MayType);
        Assert.False(search.CanSearch);
        Assert.Contains("Stop first", search.Barred, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_unlocks_it_without_anything_else_happening()
    {
        // Read on the beat rather than latched, so this needs no event — just a Refresh.
        var speed = 60.0;
        var server = new Server();
        var search = new MapSearchViewModel(server.FetchAsync, new Clock(Noon), () => null, () => speed);

        Assert.False(search.MayType);

        speed = 0;
        search.Refresh();

        Assert.True(search.MayType);

        await TypeAsync(search, "123 Main");
        Assert.Equal("123 MAIN", search.Query);
    }

    // --- Where the search is biased, and what happens when one is chosen ---

    [Fact]
    public async Task The_search_is_biased_to_where_the_truck_is()
    {
        var (search, server, _) = Build(near: new MapCamera(-97.1384, 49.8951, 15, 0));

        await TypeAsync(search, "main street");
        await search.SearchCommand.ExecuteAsync(null);

        Assert.Contains("viewbox=", server.Asked[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_an_address_hands_it_on_and_remembers_it()
    {
        var (search, _, _) = Build();
        GeocodeResult? picked = null;
        search.Picked += found => picked = found;

        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);
        search.ChooseCommand.Execute(search.Results[0]);

        Assert.NotNull(picked);
        Assert.Equal("123 Main Street", picked!.Value.Label);
        Assert.Single(search.Recent);
    }

    [Fact]
    public async Task The_same_address_chosen_twice_is_one_recent_entry_at_the_top()
    {
        var (search, _, clock) = Build();

        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);
        search.ChooseCommand.Execute(search.Results[0]);

        clock.UtcNow = Noon.AddSeconds(5);
        search.ChooseCommand.Execute(search.Results[0]);

        Assert.Single(search.Recent);
    }

    [Fact]
    public async Task Recents_stop_growing_so_they_stay_tappable()
    {
        var (search, server, clock) = Build();

        for (var i = 0; i < 9; i++)
        {
            server.Reply = $$"""
                [ { "lat": "49.{{i}}", "lon": "-97.1", "display_name": "{{i}} Some Street, Winnipeg" } ]
                """;

            clock.UtcNow = Noon.AddSeconds(i * 2);
            search.ResetCommand.Execute(null);
            await TypeAsync(search, $"{i} some street");
            await search.SearchCommand.ExecuteAsync(null);
            search.ChooseCommand.Execute(search.Results[0]);
        }

        Assert.Equal(6, search.Recent.Count);
        Assert.StartsWith("8", search.Recent[0].Label, StringComparison.Ordinal);
    }

    // --- How the map answers it ---

    [Fact]
    public void Opening_the_search_takes_the_map_full_screen()
    {
        // Five rows of keys, a field and a list do not fit the bottom third — and the placement
        // already existed (ADR-0062), so this is the first thing to use one for a reason.
        var (search, _, _) = Build();
        var map = new MapViewModel(search: search);

        Assert.True(map.IsPanel);

        map.OpenSearchCommand.Execute(null);

        Assert.True(map.IsFull);
        Assert.True(map.IsSearchOpen);
        Assert.False(map.IsMapShowing);
    }

    [Fact]
    public void Opening_the_search_brings_the_map_off_the_stage_first()
    {
        // From the stage the nav's region shows only the "map is on the stage" notice, so a
        // search opened there would have nowhere to draw.
        var (search, _, _) = Build();
        var map = new MapViewModel(search: search);
        map.SendToStageCommand.Execute(null);

        map.OpenSearchCommand.Execute(null);

        Assert.False(map.IsOnStage);
        Assert.True(map.IsFull);
        Assert.True(map.IsSearchOpen);
    }

    [Fact]
    public async Task A_chosen_address_moves_the_map_pins_it_and_stops_following()
    {
        // A map that slid straight back to the truck would undo what you just asked for.
        var (search, _, _) = Build();
        var map = new MapViewModel(search: search);
        map.OpenSearchCommand.Execute(null);

        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);
        search.ChooseCommand.Execute(search.Results[0]);

        Assert.False(map.IsSearchOpen);
        Assert.False(map.IsFollowing);
        Assert.True(map.IsRecentreOffered);
        Assert.Equal(49.8951, map.Camera!.Value.Latitude, 4);
    }

    [Fact]
    public void A_map_built_without_a_search_does_not_offer_one()
    {
        var map = new MapViewModel();

        Assert.False(map.IsSearchOffered);
        Assert.Null(map.Search);

        map.OpenSearchCommand.Execute(null);

        Assert.False(map.IsSearchOpen);
    }

    [Fact]
    public async Task What_the_search_is_doing_is_reportable_for_a_screenshot()
    {
        var (search, _, _) = Build();
        await TypeAsync(search, "123 Main");
        await search.SearchCommand.ExecuteAsync(null);

        var described = search.Describe();

        Assert.Contains("123 MAIN", described, StringComparison.Ordinal);
        Assert.Contains("results=1", described, StringComparison.Ordinal);
    }
}
