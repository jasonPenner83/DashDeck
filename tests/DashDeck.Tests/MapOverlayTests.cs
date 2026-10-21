using System.Text.Json;
using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// The map's overlays (ADR-0071): the buttons drawn over it, the three bases, which way is up,
/// and the range lines.
/// </summary>
public sealed class MapOverlayTests
{
    // ---- The choices ----

    [Fact]
    public void It_opens_on_the_road_map_north_up_with_the_range_lines_on()
    {
        Assert.Equal(new MapChoices(MapBase.Road, MapOrientation.NorthUp, ShowRange: true), MapChoices.Default);
    }

    [Theory]
    [InlineData(MapControl.Road, MapBase.Road)]
    [InlineData(MapControl.Satellite, MapBase.Satellite)]
    [InlineData(MapControl.Terrain, MapBase.Terrain)]
    public void A_base_button_chooses_that_base_and_changes_nothing_else(MapControl pressed, MapBase chosen)
    {
        var before = MapChoices.Default with { Orientation = MapOrientation.TrackUp, ShowRange = false };
        var after = before.With(pressed);

        Assert.Equal(chosen, after.Base);
        Assert.Equal(MapOrientation.TrackUp, after.Orientation);
        Assert.False(after.ShowRange);
    }

    [Fact]
    public void The_orientation_button_swaps_north_up_and_track_up()
    {
        var once = MapChoices.Default.With(MapControl.Orientation);

        Assert.Equal(MapOrientation.TrackUp, once.Orientation);
        Assert.Equal(MapOrientation.NorthUp, once.With(MapControl.Orientation).Orientation);
    }

    [Fact]
    public void The_range_button_toggles_the_lines()
    {
        Assert.False(MapChoices.Default.With(MapControl.Range).ShowRange);
        Assert.True(MapChoices.Default.With(MapControl.Range).With(MapControl.Range).ShowRange);
    }

    [Theory]
    [InlineData(MapControl.Search)]
    [InlineData(MapControl.Recentre)]
    public void Buttons_that_do_something_change_no_choice(MapControl pressed)
    {
        Assert.Equal(MapChoices.Default, MapChoices.Default.With(pressed));
    }

    [Fact]
    public void Saved_choices_read_back_and_nonsense_falls_back_to_the_default()
    {
        Assert.Equal(
            new MapChoices(MapBase.Satellite, MapOrientation.TrackUp, false),
            MapChoices.From("satellite", "TrackUp", false));

        Assert.Equal(
            new MapChoices(MapBase.Road, MapOrientation.NorthUp, true),
            MapChoices.From("moon", "sideways", true));

        Assert.Equal(MapBase.Road, MapChoices.From("7", null, true).Base);
        Assert.Equal(MapBase.Road, MapChoices.From(null, null, true).Base);
    }

    [Fact]
    public void Choices_describe_themselves_for_a_shot()
    {
        Assert.Equal("terrain · track up · range off", new MapChoices(MapBase.Terrain, MapOrientation.TrackUp, false).Describe());
    }

    // ---- The bridge ----

    [Theory]
    [InlineData("map:press:search", MapControl.Search)]
    [InlineData("map:press:orientation", MapControl.Orientation)]
    [InlineData("map:press:range", MapControl.Range)]
    [InlineData("map:press:recentre", MapControl.Recentre)]
    [InlineData("map:press:road", MapControl.Road)]
    [InlineData("map:press:satellite", MapControl.Satellite)]
    [InlineData("map:press:terrain", MapControl.Terrain)]
    public void A_pressed_button_crosses_the_bridge_by_name(string text, MapControl control)
    {
        Assert.True(MapMessage.TryParse(text, out var message));
        Assert.Equal(MapSignal.Pressed, message.Signal);
        Assert.Equal(control, message.Control);
    }

    [Theory]
    [InlineData("map:press:")]
    [InlineData("map:press:launch")]
    [InlineData("map:press:2")]
    [InlineData("map:press:-1")]
    [InlineData("map:press:search,range")]
    [InlineData("map:press: search")]
    public void A_button_that_is_not_one_of_ours_is_not_a_message(string text)
    {
        Assert.False(MapMessage.TryParse(text, out _));
    }

    // ---- The three bases ----

    private static JsonElement[] Layers(MapBase mapBase)
    {
        using var style = JsonDocument.Parse(MapStyle.Build(MapPalette.Default, mapBase));
        return [.. style.RootElement.GetProperty("layers").EnumerateArray().Select(l => l.Clone())];
    }

    private static string[] Ids(MapBase mapBase) => [.. Layers(mapBase).Select(l => l.GetProperty("id").GetString()!)];

    [Theory]
    [InlineData(MapBase.Road)]
    [InlineData(MapBase.Satellite)]
    [InlineData(MapBase.Terrain)]
    public void Every_base_is_a_style_the_renderer_will_take(MapBase mapBase)
    {
        Assert.True(MapStyle.IsWellFormed(MapStyle.Build(MapPalette.Default, mapBase)));
    }

    [Fact]
    public void The_road_map_is_the_style_it_always_was()
    {
        Assert.Equal(MapStyle.Build(MapPalette.Default), MapStyle.Build(MapPalette.Default, MapBase.Road));
    }

    [Fact]
    public void Satellite_puts_imagery_over_the_ground_and_keeps_the_roads_and_names()
    {
        var ids = Ids(MapBase.Satellite);

        Assert.Equal(["land", "imagery"], ids[..2]);
        Assert.Contains("road-major", ids);
        Assert.Contains("place-name", ids);

        // Imagery shows water, fields and roofs itself; painting them over it would hide it.
        Assert.DoesNotContain("water", ids);
        Assert.DoesNotContain("green", ids);
        Assert.DoesNotContain("building", ids);

        using var style = JsonDocument.Parse(MapStyle.Build(MapPalette.Default, MapBase.Satellite));
        var imagery = style.RootElement.GetProperty("sources").GetProperty("imagery");

        Assert.Equal("raster", imagery.GetProperty("type").GetString());
        Assert.Equal(MapSource.ImageryTemplate, imagery.GetProperty("tiles")[0].GetString());
        Assert.Contains("EOX", imagery.GetProperty("attribution").GetString());
    }

    [Fact]
    public void Terrain_shades_the_ground_under_the_water_and_the_roads()
    {
        var ids = Ids(MapBase.Terrain).ToList();

        Assert.Contains("hillshade", ids);
        Assert.True(ids.IndexOf("hillshade") > ids.IndexOf("green"));
        Assert.True(ids.IndexOf("hillshade") < ids.IndexOf("water"));
        Assert.True(ids.IndexOf("hillshade") < ids.IndexOf("road-casing"));

        using var style = JsonDocument.Parse(MapStyle.Build(MapPalette.Default, MapBase.Terrain));
        var elevation = style.RootElement.GetProperty("sources").GetProperty("elevation");

        Assert.Equal("raster-dem", elevation.GetProperty("type").GetString());
        Assert.Equal("terrarium", elevation.GetProperty("encoding").GetString());
        Assert.Equal(MapSource.TerrainTemplate, elevation.GetProperty("tiles")[0].GetString());
    }

    [Fact]
    public void Terrain_is_shaded_in_the_themes_colours()
    {
        var trek = MapPalette.Default with { Road = "#CC6666", RoadMajor = "#FF9966" };

        using var style = JsonDocument.Parse(MapStyle.Build(trek, MapBase.Terrain));
        var shade = style.RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("id").GetString() == "hillshade").GetProperty("paint");

        Assert.Equal("#FF9966", shade.GetProperty("hillshade-highlight-color").GetString());
        Assert.Equal("#CC6666", shade.GetProperty("hillshade-accent-color").GetString());
    }

    [Theory]
    [InlineData(MapBase.Satellite)]
    [InlineData(MapBase.Terrain)]
    public void No_base_fixes_a_camera(MapBase mapBase)
    {
        using var style = JsonDocument.Parse(MapStyle.Build(MapPalette.Default, mapBase));

        Assert.False(style.RootElement.TryGetProperty("center", out _));
        Assert.False(style.RootElement.TryGetProperty("zoom", out _));
    }

    // ---- Range rings ----

    private const double Lat = 49.9;
    private const double Lon = -97.1;

    [Fact]
    public void A_point_set_off_north_is_that_far_north()
    {
        var (lat, lon) = Geo.Destination(Lat, Lon, 0, 100_000);

        Assert.Equal(100_000, Geo.DistanceMetres(Lat, Lon, lat, lon), 1.0);
        Assert.Equal(Lon, lon, 6);
        Assert.True(lat > Lat);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(135)]
    [InlineData(270)]
    public void A_point_set_off_on_any_bearing_is_that_far_on_that_bearing(double bearing)
    {
        var (lat, lon) = Geo.Destination(Lat, Lon, bearing, 250_000);

        Assert.Equal(250_000, Geo.DistanceMetres(Lat, Lon, lat, lon), 1.0);
        Assert.Equal(bearing, Geo.BearingDegrees(Lat, Lon, lat, lon), 1);
    }

    [Fact]
    public void A_point_past_the_date_line_wraps_into_range()
    {
        var (_, lon) = Geo.Destination(0, 179.9, 90, 50_000);

        Assert.InRange(lon, -180, -179);
    }

    [Fact]
    public void Two_rings_one_at_eighty_left_and_one_at_empty()
    {
        var rings = RangeLines.Rings(Lat, Lon, 400);

        Assert.Equal([RangeLevel.Low, RangeLevel.Empty], rings.Select(r => r.Level));
        Assert.Equal(320, rings[0].RadiusKm);
        Assert.Equal(400, rings[1].RadiusKm);
        Assert.Equal("80 KM LEFT", RangeLines.Label(rings[0]));
        Assert.Equal("EMPTY", RangeLines.Label(rings[1]));
    }

    [Fact]
    public void Every_point_of_a_ring_is_its_radius_away_and_the_ring_closes()
    {
        var ring = RangeLines.Rings(Lat, Lon, 400)[1];

        Assert.Equal(RangeLines.RingPoints + 1, ring.Line.Count);
        Assert.Equal(ring.Line[0], ring.Line[^1]);

        foreach (var (lon, lat) in ring.Line)
        {
            Assert.Equal(400_000, Geo.DistanceMetres(Lat, Lon, lat, lon), 1.0);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(35)]
    public void Inside_the_reserve_there_is_only_the_empty_ring(double range)
    {
        var rings = RangeLines.Rings(Lat, Lon, range);

        Assert.Equal(RangeLevel.Empty, Assert.Single(rings).Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(60_000)]
    public void A_range_that_is_not_a_distance_draws_nothing(double range)
    {
        Assert.Empty(RangeLines.Rings(Lat, Lon, range));
    }

    [Fact]
    public void No_position_draws_nothing()
    {
        Assert.Empty(RangeLines.Rings(double.NaN, Lon, 400));
    }

    [Fact]
    public void Rings_go_to_the_page_as_labelled_lines()
    {
        using var json = JsonDocument.Parse(RangeLines.RingsJson(RangeLines.Rings(Lat, Lon, 400)));
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToList();

        Assert.Equal("FeatureCollection", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(["low", "empty"], features.Select(f => f.GetProperty("properties").GetProperty("level").GetString()));
        Assert.Equal("80 KM LEFT", features[0].GetProperty("properties").GetProperty("label").GetString());
        Assert.Equal("LineString", features[0].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(RangeLines.RingPoints + 1, features[0].GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
    }

    // ---- Range along a route ----

    /// <summary>A straight road due north from the truck, <paramref name="km"/> long, a vertex every 10 km.</summary>
    private static Route North(double km)
    {
        var shape = new List<(double Longitude, double Latitude)>();

        for (var d = 0.0; d <= km; d += 10)
        {
            var (lat, lon) = Geo.Destination(Lat, Lon, 0, d * 1000);
            shape.Add((lon, lat));
        }

        return new Route(km * 1000, km * 36, shape, []);
    }

    /// <summary>How far north of the truck a point is, in km.</summary>
    private static double KmFrom((double Longitude, double Latitude) point) =>
        Geo.DistanceMetres(Lat, Lon, point.Latitude, point.Longitude) / 1000;

    [Fact]
    public void A_route_inside_the_range_with_fuel_to_spare_is_clear()
    {
        var cut = RangeLines.AlongRoute(North(200), Lat, Lon, 400);

        Assert.NotNull(cut);
        Assert.True(cut.IsClear);
    }

    [Fact]
    public void A_route_past_the_range_is_cut_at_eighty_left_and_at_empty()
    {
        var cut = RangeLines.AlongRoute(North(500), Lat, Lon, 400)!;

        Assert.Equal(320, KmFrom(cut.Low[0]), 0.5);
        Assert.Equal(400, KmFrom(cut.Low[^1]), 0.5);
        Assert.Equal(400, KmFrom(cut.Beyond[0]), 0.5);
        Assert.Equal(500, KmFrom(cut.Beyond[^1]), 0.5);
    }

    [Fact]
    public void A_cut_that_falls_between_vertices_is_made_exactly_there()
    {
        var cut = RangeLines.AlongRoute(North(500), Lat, Lon, 405)!;

        Assert.Equal(325, KmFrom(cut.Low[0]), 0.5);
        Assert.Equal(405, KmFrom(cut.Beyond[0]), 0.5);
    }

    [Fact]
    public void A_route_that_ends_in_the_reserve_is_orange_to_the_end_and_never_red()
    {
        var cut = RangeLines.AlongRoute(North(350), Lat, Lon, 400)!;

        Assert.Equal(320, KmFrom(cut.Low[0]), 0.5);
        Assert.Equal(350, KmFrom(cut.Low[^1]), 0.5);
        Assert.Empty(cut.Beyond);
    }

    [Fact]
    public void Already_in_the_reserve_the_orange_starts_at_the_truck()
    {
        var cut = RangeLines.AlongRoute(North(200), Lat, Lon, 50)!;

        Assert.Equal(0, KmFrom(cut.Low[0]), 0.5);
        Assert.Equal(50, KmFrom(cut.Low[^1]), 0.5);
        Assert.Equal(50, KmFrom(cut.Beyond[0]), 0.5);
    }

    [Fact]
    public void The_cut_is_measured_from_where_the_truck_is_on_the_route_not_where_it_began()
    {
        // A hundred km up the road, the same 400 km of range reaches a hundred km further.
        var (lat, lon) = Geo.Destination(Lat, Lon, 0, 100_000);
        var cut = RangeLines.AlongRoute(North(600), lat, lon, 400)!;

        Assert.Equal(420, KmFrom(cut.Low[0]), 0.5);
        Assert.Equal(500, KmFrom(cut.Beyond[0]), 0.5);
    }

    [Fact]
    public void Off_the_route_there_is_no_cut()
    {
        var (lat, lon) = Geo.Destination(Lat, Lon, 90, 2_000);

        Assert.Null(RangeLines.AlongRoute(North(500), lat, lon, 400));
    }

    [Fact]
    public void No_range_no_cut()
    {
        Assert.Null(RangeLines.AlongRoute(North(500), Lat, Lon, double.NaN));
    }

    [Fact]
    public void A_cut_route_goes_to_the_page_as_its_two_stretches()
    {
        using var json = JsonDocument.Parse(RangeLines.RouteJson(RangeLines.AlongRoute(North(500), Lat, Lon, 400)));

        Assert.Equal(
            ["low", "empty"],
            json.RootElement.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("properties").GetProperty("level").GetString()));
    }

    [Fact]
    public void No_cut_goes_to_the_page_as_nothing_to_draw()
    {
        using var json = JsonDocument.Parse(RangeLines.RouteJson(null));

        Assert.Equal(0, json.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public void The_countdown_still_counts_down_after_sharing_its_arithmetic()
    {
        var (lat, lon) = Geo.Destination(Lat, Lon, 0, 100_000);
        var progress = RouteProgress.Along(North(500), lat, lon)!.Value;

        Assert.Equal(400_000, progress.RemainingMetres, 100.0);
        Assert.Equal(500 * 36 * 0.8, progress.RemainingSeconds, 0);
        Assert.True(progress.IsOnRoute);
    }
}
