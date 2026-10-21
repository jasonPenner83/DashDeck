using System.Text.Json;
using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.Maps;
using DashDeck.Host.ViewModels;
using static DashDeck.Host.Tests.MapRenderTests;

namespace DashDeck.Host.Tests;

/// <summary>
/// The buttons over the map and the range lines, as the map's view-model decides them (ADR-0071).
/// </summary>
/// <remarks>
/// The page draws the buttons and the lines and decides nothing; a press crosses the bridge and
/// everything about what it does is here — which is why it is here, where it is tested.
/// </remarks>
public sealed class MapOverlayViewModelTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private const double Lat = 49.8951;
    private const double Lon = -97.1384;

    private static LocationFix Fix(double lat = Lat, double lon = Lon) =>
        new(lat, lon, 62, 84, SignalQuality.Live, Noon);

    /// <summary>The truck's signals, holding a distance to empty.</summary>
    private sealed class Bus : IVehicleSignals
    {
        private readonly Dictionary<string, SignalValue> _values = new(StringComparer.Ordinal);

        public List<string> Required { get; } = [];

        public int Released { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => _values.Keys;

        public void Range(double km, SignalQuality quality = SignalQuality.Live, string unit = "km") =>
            _values[MapViewModel.RangeSignal] = new SignalValue(MapViewModel.RangeSignal, km, unit, Noon, quality);

        public SignalValue Current(string signalId) =>
            _values.TryGetValue(signalId, out var v) ? v : SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) => throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            Required.Add(signalId);
            return new Demand(this, signalId, rateHz);
        }

        private sealed class Demand(Bus bus, string id, double rate) : ISignalSubscription
        {
            public string SignalId => id;

            public double RequestedRateHz => rate;

            public double EffectiveRateHz => rate;

            public event Action<double>? EffectiveRateChanged
            {
                add { }
                remove { }
            }

            public void Dispose() => bus.Released++;
        }
    }

    private static (MapViewModel Map, FakeSurface Surface, Source Source, Bus Bus, List<MapChoices> Saved) Drawn(
        MapChoices? choices = null,
        double? rangeKm = 400,
        MapSearchViewModel? search = null)
    {
        var bus = new Bus();

        if (rangeKm is { } km)
        {
            bus.Range(km);
        }

        var saved = new List<MapChoices>();
        var source = new Source(Fix());
        var map = new MapViewModel(source, new Clock(Noon), search: search, choices: choices, remember: saved.Add, signals: bus);
        var surface = new FakeSurface();

        map.Attach(surface);
        map.OnMessage(new MapMessage(MapSignal.Ready));

        return (map, surface, source, bus, saved);
    }

    private static void Press(MapViewModel map, MapControl control) =>
        map.OnMessage(new MapMessage(MapSignal.Pressed, Control: control));

    private static string[] Levels(string geoJson)
    {
        using var json = JsonDocument.Parse(geoJson);
        return [.. json.RootElement.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("properties").GetProperty("level").GetString()!)];
    }

    // ---- The buttons ----

    [Fact]
    public void A_drawn_map_is_given_its_buttons()
    {
        var (_, surface, _, _, _) = Drawn();

        var buttons = surface.Buttoned[^1];

        Assert.Equal(MapChoices.Default, buttons.Choices);
        Assert.False(buttons.Recentre);
        Assert.Equal("400 KM", buttons.Range);
    }

    [Fact]
    public void The_map_opens_the_way_it_was_left()
    {
        var left = new MapChoices(MapBase.Terrain, MapOrientation.TrackUp, ShowRange: false);
        var (map, surface, _, _, _) = Drawn(left);

        Assert.Equal(left, map.Choices);
        Assert.Equal(left, surface.Buttoned[^1].Choices);

        // The shell hands the theme over at startup, and the style it builds is the saved base's.
        map.Wear(MapPalette.Default);
        Assert.Contains("hillshade", map.StyleJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_satellite_restyles_the_map_and_is_remembered()
    {
        var (map, surface, _, _, saved) = Drawn();
        var styles = surface.Styles.Count;

        Press(map, MapControl.Satellite);

        Assert.Equal(MapBase.Satellite, map.Choices.Base);
        Assert.Equal(styles + 1, surface.Styles.Count);
        Assert.Contains(MapSource.ImageryTemplate, surface.Styles[^1]!, StringComparison.Ordinal);
        Assert.Equal(MapBase.Satellite, saved.Single().Base);
        Assert.Equal(MapBase.Satellite, surface.Buttoned[^1].Choices.Base);
    }

    [Fact]
    public void Choosing_the_base_already_shown_changes_nothing_but_still_answers()
    {
        var (map, surface, _, _, saved) = Drawn();
        var styles = surface.Styles.Count;
        var buttons = surface.Buttoned.Count;

        Press(map, MapControl.Road);

        Assert.Equal(styles, surface.Styles.Count);
        Assert.Empty(saved);
        Assert.Equal(buttons + 1, surface.Buttoned.Count);
    }

    [Fact]
    public void The_orientation_button_turns_track_up_on_without_restyling()
    {
        var (map, surface, _, _, saved) = Drawn();
        var styles = surface.Styles.Count;

        Press(map, MapControl.Orientation);

        Assert.Equal(MapOrientation.TrackUp, map.Choices.Orientation);
        Assert.Equal(MapOrientation.TrackUp, surface.Buttoned[^1].Choices.Orientation);
        Assert.Equal(MapOrientation.TrackUp, saved.Single().Orientation);
        Assert.Equal(styles, surface.Styles.Count);
    }

    [Fact]
    public void A_style_override_is_not_replaced_by_a_base()
    {
        // --map-style is the comparison for a generated style that looks wrong; a base button
        // must not quietly take it away.
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon), "https://example.org/style.json");

        Press(map, MapControl.Satellite);

        Assert.Null(map.StyleJson);
        Assert.Equal(MapBase.Satellite, map.Choices.Base);
    }

    [Fact]
    public void The_search_button_opens_the_search()
    {
        var search = new MapSearchViewModel((_, _) => Task.FromResult("[]"), new Clock(Noon), () => null, () => null);
        var (map, _, _, _, _) = Drawn(search: search);

        Press(map, MapControl.Search);

        Assert.True(map.IsSearchOpen);
        Assert.True(map.IsFull);
    }

    [Fact]
    public void Recentre_appears_after_a_pan_and_puts_the_map_back_on_the_truck()
    {
        var (map, surface, _, _, _) = Drawn();

        map.OnMessage(new MapMessage(MapSignal.Freed));
        Assert.True(surface.Buttoned[^1].Recentre);

        Press(map, MapControl.Recentre);

        Assert.True(map.IsFollowing);
        Assert.False(surface.Buttoned[^1].Recentre);
        Assert.True(surface.Follows[^1]);
    }

    [Fact]
    public void A_rebuilt_map_is_given_its_buttons_and_lines_once_its_page_is_ready()
    {
        // A surface passes scripts on only once its page exists, so what the old map showed is
        // given again when the new one says it is ready — not only when it is built, when it
        // would be dropped on the floor.
        var (map, surface, _, _, _) = Drawn();
        map.Detach(surface);

        var fresh = new FakeSurface();
        map.Attach(fresh);
        var attached = fresh.Buttoned.Count;

        map.OnMessage(new MapMessage(MapSignal.Ready));

        Assert.True(fresh.Buttoned.Count > attached);
        Assert.Equal(["low", "empty"], Levels(fresh.Ranges[^1].Rings));
    }

    // ---- The range lines ----

    [Fact]
    public void The_range_lines_are_drawn_around_the_truck_from_its_distance_to_empty()
    {
        var (_, surface, _, _, _) = Drawn(rangeKm: 400);

        var drawn = surface.Ranges[^1];

        Assert.True(drawn.Live);
        Assert.Equal(["low", "empty"], Levels(drawn.Rings));
        Assert.Empty(Levels(drawn.Route));
    }

    [Fact]
    public void The_range_is_asked_for_only_while_a_map_is_drawing_the_lines()
    {
        var (map, surface, _, bus, _) = Drawn();

        Assert.Equal([MapViewModel.RangeSignal], bus.Required);

        Press(map, MapControl.Range);
        Assert.Equal(1, bus.Released);

        Press(map, MapControl.Range);
        Assert.Equal(2, bus.Required.Count);

        map.Detach(surface);
        Assert.Equal(2, bus.Released);
    }

    [Fact]
    public void Turning_the_lines_off_rubs_them_out_and_says_so_on_the_button()
    {
        var (map, surface, _, _, saved) = Drawn();

        Press(map, MapControl.Range);

        Assert.Empty(Levels(surface.Ranges[^1].Rings));
        Assert.Equal("OFF", surface.Buttoned[^1].Range);
        Assert.False(saved.Single().ShowRange);
    }

    [Fact]
    public void With_no_range_from_the_truck_nothing_is_drawn_and_the_button_says_why()
    {
        var (map, surface, _, _, _) = Drawn(rangeKm: null);

        Assert.All(surface.Ranges, r => Assert.Empty(Levels(r.Rings)));
        Assert.Equal("NO RANGE", surface.Buttoned[^1].Range);
        Assert.Equal("NO RANGE", map.RangeCaption);
    }

    [Fact]
    public void Without_a_truck_there_are_no_range_lines_rather_than_invented_ones()
    {
        var map = new MapViewModel(new Source(Fix()), new Clock(Noon));
        var surface = new FakeSurface();
        map.Attach(surface);
        map.OnMessage(new MapMessage(MapSignal.Ready));

        Assert.All(surface.Ranges, r => Assert.Empty(Levels(r.Rings)));
        Assert.Equal("NO RANGE", map.RangeCaption);
    }

    [Fact]
    public void A_stale_range_is_drawn_drained()
    {
        var (map, surface, _, bus, _) = Drawn();

        bus.Range(400, SignalQuality.Stale);
        map.Refresh();

        Assert.False(surface.Ranges[^1].Live);
    }

    [Fact]
    public void A_range_in_miles_is_drawn_in_kilometres()
    {
        var (map, surface, _, bus, _) = Drawn();

        bus.Range(250, unit: "mi");
        map.Refresh();

        Assert.Equal("402 KM", surface.Buttoned[^1].Range);
    }

    [Fact]
    public void The_lines_are_not_redrawn_for_every_metre_the_truck_moves()
    {
        var (map, surface, source, _, _) = Drawn();
        var drawn = surface.Ranges.Count;

        // About 30 m north: nothing anyone could see changes on a ring hundreds of km across.
        source.Fix = Fix(Lat + 0.0003);
        map.Refresh();
        Assert.Equal(drawn, surface.Ranges.Count);

        // About a kilometre: drawn again around where the truck is now.
        source.Fix = Fix(Lat + 0.01);
        map.Refresh();
        Assert.Equal(drawn + 1, surface.Ranges.Count);
    }

    [Fact]
    public void A_falling_range_redraws_the_lines_and_the_number()
    {
        var (map, surface, _, bus, _) = Drawn();

        bus.Range(399.6);
        map.Refresh();
        var drawn = surface.Ranges.Count;

        bus.Range(395);
        map.Refresh();

        Assert.Equal(drawn + 1, surface.Ranges.Count);
        Assert.Equal("395 KM", surface.Buttoned[^1].Range);
    }

    [Fact]
    public async Task A_route_longer_than_the_range_is_cut_where_the_fuel_runs_low_and_out()
    {
        // Due north for 200 km, a vertex every 10 km, as the router would answer it.
        var line = string.Join(",", Enumerable.Range(0, 21)
            .Select(i => Geo.Destination(Lat, Lon, 0, i * 10_000))
            .Select(p => FormattableString.Invariant($"[{p.Longitude:0.######},{p.Latitude:0.######}]")));

        var reply = $$"""
            { "routes": [ { "distance": 200000, "duration": 7200,
                "geometry": { "coordinates": [{{line}}] },
                "legs": [ { "steps": [
                  { "distance": 200000, "name": "Highway 6", "maneuver": { "type": "depart" } },
                  { "distance": 0, "name": "", "maneuver": { "type": "arrive" } } ] } ] } ] }
            """;

        var here = new MapCamera(Lon, Lat, 15, 0);
        var directions = new MapRouteViewModel((_, _) => Task.FromResult(reply), new Clock(Noon), () => here);
        var bus = new Bus();
        bus.Range(100);

        var map = new MapViewModel(new Source(Fix()), new Clock(Noon), directions: directions, signals: bus);
        var surface = new FakeSurface();
        map.Attach(surface);
        map.OnMessage(new MapMessage(MapSignal.Ready));

        Assert.Empty(Levels(surface.Ranges[^1].Route));

        // Cut at once when the route arrives, rather than at the next beat that happens to move
        // the truck far enough: orange from 20 km, red from 100 km.
        await directions.FindAsync(new MapCamera(Lon, 51.7, 15, 0), "North");

        Assert.Single(surface.Routes);
        Assert.Equal(["low", "empty"], Levels(surface.Ranges[^1].Route));

        // And put back the way it was when the route is cleared.
        directions.ClearCommand.Execute(null);
        Assert.Empty(Levels(surface.Ranges[^1].Route));
    }

    [Fact]
    public void What_the_buttons_are_set_to_is_reportable_for_a_screenshot()
    {
        var (map, _, _, _, _) = Drawn(new MapChoices(MapBase.Satellite, MapOrientation.TrackUp, true));

        var described = map.Describe();

        Assert.Contains("satellite · track up · range on", described, StringComparison.Ordinal);
        Assert.Contains("range: 400 km", described, StringComparison.Ordinal);
    }
}
