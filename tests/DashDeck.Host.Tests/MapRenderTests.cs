using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.Maps;
using DashDeck.Host.Stage;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// The map itself (ADR-0063): what the region shows at each stage of coming up, and the state
/// that has to outlive the browser control.
/// </summary>
/// <remarks>
/// No WebView2 is built — the surface arrives as an <see cref="IMapSurface"/> precisely so this
/// is testable without Windows, a browser and a signal. What is pinned here is the part that
/// decides whether the dash tells the truth while a map is loading, failing, or waiting for a
/// position, because all three look identical on screen: a dark rectangle.
/// </remarks>
public sealed class MapRenderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Source(LocationFix? fix, string name = "COM4") : ILocationSource
    {
        public LocationFix? Fix { get; set; } = fix;

        public string Name => name;

        public LocationFix? Latest => Fix;

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSurface : IMapSurface
    {
        public List<LocationFix> Positions { get; } = [];

        public List<bool> Live { get; } = [];

        public List<MapCamera> Cameras { get; } = [];

        public List<bool> Follows { get; } = [];

        public List<string?> Styles { get; } = [];

        public List<string> Markers { get; } = [];

        public List<(MapCamera Camera, string Label)> Destinations { get; } = [];

        public void Restyle(string? styleJson, string marker)
        {
            Styles.Add(styleJson);
            Markers.Add(marker);
        }

        public void Destination(MapCamera camera, string label) => Destinations.Add((camera, label));

        public void Position(LocationFix fix, bool live)
        {
            Positions.Add(fix);
            Live.Add(live);
        }

        public void Camera(MapCamera camera) => Cameras.Add(camera);

        public void Follow(bool on) => Follows.Add(on);
    }

    private static readonly MapPalette Trek = new(
        Land: "#000000",
        Water: "#1A1A2E",
        Green: "#101810",
        Building: "#202020",
        Road: "#CC6666",
        RoadMajor: "#FF9966",
        RoadCasing: "#000000",
        Label: "#FFCC66",
        LabelHalo: "#000000",
        Marker: "#FF9900");

    private static LocationFix Fix(double lat = 49.8951, double lon = -97.1384, double speed = 62, double course = 84)
        => new(lat, lon, speed, course, SignalQuality.Live, Noon);

    // --- What the region shows ---

    [Fact]
    public void With_no_gps_there_is_no_map_and_the_dash_says_which_problem_it_is()
    {
        // Not "no map" — a map cannot be centred on nothing, and saying the tiles failed when
        // the position is what is missing sends you looking in the wrong place.
        var map = new MapViewModel();

        Assert.Equal(MapPicture.NoGps, map.Picture);
        Assert.False(map.WantsSurface);
        Assert.Equal("No GPS", map.PlaceholderTitle);
        Assert.Contains("Settings", map.PlaceholderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_with_no_fix_yet_waits_rather_than_opening_a_map_somewhere_invented()
    {
        // The alternative was a hard-coded centre, and both honest versions of that are bad: the
        // middle of the ocean, or somebody's house committed to a public repository.
        var map = new MapViewModel(new Source(null), new Clock(Noon));

        Assert.Equal(MapPicture.WaitingForFix, map.Picture);
        Assert.False(map.WantsSurface);
        Assert.Equal("Waiting for a position", map.PlaceholderTitle);
        Assert.Contains("COM4", map.PlaceholderDetail, StringComparison.Ordinal);
        Assert.Null(map.Camera);
    }

    [Fact]
    public void The_first_fix_is_what_lets_the_map_be_built_and_decides_where_it_opens()
    {
        var source = new Source(null);
        var map = new MapViewModel(source, new Clock(Noon));

        Assert.False(map.WantsSurface);

        source.Fix = Fix();
        map.Refresh();

        Assert.True(map.WantsSurface);
        Assert.Equal(MapPicture.Loading, map.Picture);
        Assert.Equal(49.8951, map.Camera!.Value.Latitude, 4);
        Assert.Equal(MapCamera.DrivingZoom, map.Camera!.Value.Zoom);
    }

    [Fact]
    public void A_loading_map_says_so_and_credits_the_data_while_it_does()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        Assert.Equal(MapPicture.Loading, map.Picture);
        Assert.False(map.IsMapDrawn);
        Assert.Contains("OpenStreetMap", map.PlaceholderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drawn_map_gets_the_placeholder_out_of_the_way()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        map.OnMessage(new MapMessage(MapSignal.Ready));

        Assert.Equal(MapPicture.Drawn, map.Picture);
        Assert.True(map.IsMapDrawn);
        Assert.Equal(string.Empty, map.PlaceholderTitle);
    }

    [Fact]
    public void A_failure_keeps_its_reason_and_blames_the_right_thing()
    {
        // The tiles come over the truck's own connection, so this is nearly always signal — and
        // a dash that says "no map" without saying why sends him into the settings for nothing.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        map.OnMessage(new MapMessage(MapSignal.Failed, Detail: "Failed to fetch"));

        Assert.Equal(MapPicture.Failed, map.Picture);
        Assert.True(map.HasFailed);
        Assert.False(map.WantsSurface);
        Assert.Contains("Failed to fetch", map.PlaceholderDetail, StringComparison.Ordinal);
        Assert.Contains("signal", map.PlaceholderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Trying_again_clears_the_failure_and_asks_for_a_whole_new_surface()
    {
        // A rebuild rather than a reload: the failure may have been the browser control itself,
        // and retrying only the navigation would leave that case looking recoverable when it is not.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var reloads = 0;
        map.ReloadRequested += () => reloads++;

        map.OnMessage(new MapMessage(MapSignal.Failed, Detail: "Failed to fetch"));
        map.RetryCommand.Execute(null);

        Assert.Equal(1, reloads);
        Assert.False(map.HasFailed);
        Assert.True(map.WantsSurface);
        Assert.Equal(MapPicture.Loading, map.Picture);
    }

    [Fact]
    public void The_position_readout_stays_on_the_no_map_states()
    {
        // It is what separates "no position" from "no tiles", which otherwise look the same.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        map.OnMessage(new MapMessage(MapSignal.Failed, Detail: "Failed to fetch"));

        Assert.StartsWith("FIX", map.PositionText, StringComparison.Ordinal);
        Assert.Contains("COM4", map.PositionDetail, StringComparison.Ordinal);
    }

    // --- The state that outlives the browser control (Q43) ---

    [Fact]
    public void The_camera_survives_the_map_being_rebuilt()
    {
        // The claim Q43 rests on. Moving between the three placements rebuilds the browser
        // control, which reloads the page — so without this, every resize would throw the map
        // back to wherever it first opened.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var first = new FakeSurface();

        map.Attach(first);
        map.OnMessage(new MapMessage(MapSignal.Ready));
        map.OnMessage(new MapMessage(MapSignal.Camera, new MapCamera(-97.5, 50.1, 11.5, 0)));

        map.Detach(first);

        Assert.Equal(50.1, map.Camera!.Value.Latitude, 4);
        Assert.Equal(11.5, map.Camera!.Value.Zoom, 2);

        // And the rebuilt one opens there rather than back at the driving zoom over the vehicle.
        map.Attach(new FakeSurface());
        Assert.Equal(11.5, map.Camera!.Value.Zoom, 2);
    }

    [Fact]
    public void A_camera_the_page_could_not_describe_is_dropped_rather_than_believed()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var opened = map.Camera;

        // MapSignal.Camera with nothing in it — the shape a malformed bridge message takes
        // after MapMessage has refused to parse its numbers.
        map.OnMessage(new MapMessage(MapSignal.Camera));

        Assert.Equal(opened, map.Camera);
    }

    [Fact]
    public void A_detached_surface_stops_being_driven_but_a_newer_one_is_not_dropped_with_it()
    {
        // Both panels are briefly alive during a placement change, so the old one's teardown
        // arrives after the new one has registered. Clearing blindly there would leave a live
        // map that nothing feeds.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var old = new FakeSurface();
        var fresh = new FakeSurface();

        map.Attach(old);
        map.Attach(fresh);
        map.Detach(old);

        map.OnMessage(new MapMessage(MapSignal.Ready));
        map.Refresh();

        Assert.NotEmpty(fresh.Positions);
        Assert.Single(old.Positions);   // the one Attach gave it, and nothing since
    }

    // --- Following the vehicle ---

    [Fact]
    public void A_new_surface_is_told_the_position_and_whether_to_follow()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var surface = new FakeSurface();

        map.Attach(surface);

        Assert.Equal([true], surface.Follows);
        Assert.Single(surface.Positions);
        Assert.Equal(49.8951, surface.Positions[0].Latitude, 4);
    }

    [Fact]
    public void Panning_the_map_stops_it_following_and_offers_the_way_back()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var surface = new FakeSurface();
        map.Attach(surface);

        Assert.True(map.IsFollowing);
        Assert.False(map.IsRecentreOffered);

        map.OnMessage(new MapMessage(MapSignal.Freed));

        Assert.False(map.IsFollowing);
        Assert.True(map.IsRecentreOffered);

        map.RecentreCommand.Execute(null);

        Assert.True(map.IsFollowing);
        Assert.False(map.IsRecentreOffered);
        Assert.Equal([true, true], surface.Follows);
    }

    [Fact]
    public void A_stale_fix_reaches_the_marker_as_stale()
    {
        // The dash never dresses an old reading up as a live one, and the map is not an exception:
        // the marker drains of colour rather than sitting there looking current.
        var source = new Source(new LocationFix(49.8951, -97.1384, double.NaN, double.NaN, SignalQuality.Stale, Noon));
        var map = new MapViewModel(source, new Clock(Noon));

        // A stale fix carries no position worth opening on, so the map waits rather than
        // centring on coordinates it does not trust.
        Assert.Equal(MapPicture.WaitingForFix, map.Picture);

        source.Fix = Fix();
        map.Refresh();

        var surface = new FakeSurface();
        map.Attach(surface);
        Assert.Equal([true], surface.Live);

        // A stale fix still reaches the map — the last known position is the most useful thing
        // on screen when the link drops — but marked not live, so the marker greys where it is.
        source.Fix = new LocationFix(49.9, -97.1, double.NaN, double.NaN, SignalQuality.Stale, Noon);
        map.OnMessage(new MapMessage(MapSignal.Ready));
        map.Refresh();

        Assert.Contains(false, surface.Live);
        Assert.Equal(49.9, surface.Positions[^1].Latitude, 4);
    }

    [Fact]
    public void A_fix_whose_hold_has_ended_is_not_drawn_at_all()
    {
        // Unavailable means the coordinates are no longer claimed to mean anything (ADR-0027's
        // addendum), so nothing is sent and the marker stays where it was — already grey.
        var source = new Source(Fix());
        var map = new MapViewModel(source, new Clock(Noon));
        var surface = new FakeSurface();
        map.Attach(surface);
        map.OnMessage(new MapMessage(MapSignal.Ready));

        var sent = surface.Positions.Count;

        source.Fix = new LocationFix(49.9, -97.1, double.NaN, double.NaN, SignalQuality.Unavailable, Noon);
        map.Refresh();

        Assert.Equal(sent, surface.Positions.Count);
    }

    [Fact]
    public void The_beat_is_harmless_while_there_is_no_map_to_push_at()
    {
        // Refresh runs on the dash's one-second beat whenever MAPS is the destination, which
        // includes every second the map is still loading, has failed, or was never built.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        map.Refresh();
        map.OnMessage(new MapMessage(MapSignal.Failed, Detail: "Failed to fetch"));
        map.Refresh();

        // The readout keeps working, which is the whole reason the beat still runs.
        Assert.StartsWith("FIX", map.PositionText, StringComparison.Ordinal);
        Assert.Equal(MapPicture.Failed, map.Picture);
    }

    // --- The style, and what the stage occupant offers ---

    // --- The theme's colours (ADR-0064) ---

    [Fact]
    public void Without_an_override_the_map_is_drawn_in_the_themes_colours()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        Assert.Null(map.StyleOverride);

        map.Wear(Trek);

        Assert.NotNull(map.StyleJson);
        Assert.Contains(Trek.Water, map.StyleJson!, StringComparison.Ordinal);
        Assert.Equal(Trek.Marker, map.MarkerColour);
        Assert.Null(map.StyleNote);
    }

    [Fact]
    public void A_map_already_on_screen_is_restyled_rather_than_left_behind()
    {
        // A theme worn, an accent picked or dusk falling, with the map up. The whole point: the
        // map turns with the rest of the dash instead of staying grey under an LCARS console.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var surface = new FakeSurface();
        map.Attach(surface);

        map.Wear(Trek);

        Assert.Single(surface.Styles);
        Assert.Contains(Trek.Road, surface.Styles[0]!, StringComparison.Ordinal);
        Assert.Equal([Trek.Marker], surface.Markers);
    }

    [Fact]
    public void An_override_keeps_the_hosted_style_but_still_takes_the_themes_marker()
    {
        // The cartography is somebody else's under --map-style; the vehicle is always ours.
        var map = new MapViewModel(
            new Source(Fix()), new Clock(Noon), "https://tiles.openfreemap.org/styles/fiord");

        Assert.Equal("https://tiles.openfreemap.org/styles/fiord", map.StyleOverride);

        map.Wear(Trek);

        Assert.Null(map.StyleJson);
        Assert.Equal(Trek.Marker, map.MarkerColour);
    }

    [Fact]
    public void An_unusable_override_is_ignored_so_the_theme_still_gets_its_map()
    {
        Assert.Null(new MapViewModel(styleUrl: "http://insecure/style").StyleOverride);
        Assert.Null(new MapViewModel(styleUrl: "javascript:alert(1)").StyleOverride);
        Assert.Null(new MapViewModel().StyleOverride);
    }

    [Fact]
    public void A_style_that_will_not_build_falls_back_and_says_why()
    {
        // Tested, so this is a bug rather than a state — but a map that silently stopped
        // following the theme would be a puzzle, and the note is what solves it.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));

        map.Wear(Trek with { Water = "not a colour\" }" });

        Assert.Null(map.StyleJson);
        Assert.NotNull(map.StyleNote);
        Assert.Contains("hosted dark", map.StyleNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void On_the_stage_the_menu_offers_the_two_controls_that_still_mean_something()
    {
        // The panel's chips are hidden there, so a map that has lost you would otherwise have no
        // way back to the vehicle.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        map.SendToStageCommand.Execute(null);

        var occupant = new MapStageOccupant(map, () => map.BringBackCommand.Execute(null));
        var captions = occupant.Actions.Select(a => a.Caption).ToList();

        Assert.Equal(["BRING THE MAP BACK", "RECENTRE"], captions);

        map.OnMessage(new MapMessage(MapSignal.Freed));
        occupant.Actions[1].Invoke();

        Assert.True(map.IsFollowing);
    }

    [Fact]
    public void What_the_map_is_doing_is_reportable_for_a_screenshot()
    {
        // A picture cannot show a placement, a load state or a centre, and all three are the
        // question while this is being built.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        map.OnMessage(new MapMessage(MapSignal.Ready));

        var described = map.Describe();

        Assert.Contains("bottom third", described, StringComparison.Ordinal);
        Assert.Contains("drawn", described, StringComparison.Ordinal);
        Assert.Contains("49.8951", described, StringComparison.Ordinal);
    }
}
