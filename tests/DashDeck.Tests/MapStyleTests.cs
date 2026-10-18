using System.Text.Json;
using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// The map style the theme's colours build (ADR-0064).
/// </summary>
/// <remarks>
/// The style is the one part of the map that can be checked properly without Windows, a browser
/// or a signal — it is data. So this is where the cartography is held to account: that it parses,
/// that every layer names a source layer the tiles actually carry, that the theme's colours reach
/// the properties they are meant to, and that nothing in it fixes a camera.
/// </remarks>
public sealed class MapStyleTests
{
    /// <summary>The source layers OpenFreeMap's own dark style uses, which is where these came from.</summary>
    private static readonly string[] RealSourceLayers =
    [
        "water", "waterway", "water_name", "landcover", "landuse", "building",
        "aeroway", "transportation", "transportation_name", "boundary", "place",
    ];

    private static JsonDocument Built(MapPalette? palette = null) =>
        JsonDocument.Parse(MapStyle.Build(palette ?? MapPalette.Default));

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

    [Fact]
    public void It_is_a_style_a_renderer_will_accept()
    {
        using var style = Built();
        var root = style.RootElement;

        Assert.Equal(8, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("sources", out _));
        Assert.NotEmpty(root.GetProperty("layers").EnumerateArray());
        Assert.True(MapStyle.IsWellFormed(MapStyle.Build(MapPalette.Default)));
    }

    [Fact]
    public void Nothing_in_it_fixes_a_camera()
    {
        // A style with its own centre and zoom would undo the camera the dash keeps across a
        // rebuild (Q43) — so every placement change would throw you back to where it was authored.
        using var style = Built();

        Assert.False(style.RootElement.TryGetProperty("center", out _));
        Assert.False(style.RootElement.TryGetProperty("zoom", out _));
    }

    [Fact]
    public void Every_placeholder_is_filled_in()
    {
        // A leftover $NAME would reach the renderer as a colour it cannot parse, and a style that
        // fails to parse is a dark rectangle with no error.
        Assert.DoesNotContain("$", MapStyle.Build(Trek), StringComparison.Ordinal);
    }

    [Fact]
    public void The_road_colours_do_not_overwrite_each_other()
    {
        // $ROAD is a prefix of $ROADMAJOR and $ROADCASING, and $LABEL of $LABELHALO. Substituting
        // in the wrong order leaves "#CC6666MAJOR" behind, which is the whole reason this is a test.
        using var style = Built(Trek);
        var layers = Layers(style);

        Assert.Equal(Trek.Road, Paint(layers["road-minor"], "line-color"));
        Assert.Equal(Trek.Road, Paint(layers["road-mid"], "line-color"));
        Assert.Equal(Trek.RoadMajor, Paint(layers["road-major"], "line-color"));
        Assert.Equal(Trek.RoadCasing, Paint(layers["road-casing"], "line-color"));
        Assert.Equal(Trek.Label, Paint(layers["place-name"], "text-color"));
        Assert.Equal(Trek.LabelHalo, Paint(layers["place-name"], "text-halo-color"));
    }

    [Fact]
    public void The_themes_colours_reach_the_things_they_name()
    {
        using var style = Built(Trek);
        var layers = Layers(style);

        Assert.Equal(Trek.Land, Paint(layers["land"], "background-color"));
        Assert.Equal(Trek.Water, Paint(layers["water"], "fill-color"));
        Assert.Equal(Trek.Green, Paint(layers["green"], "fill-color"));
        Assert.Equal(Trek.Building, Paint(layers["building"], "fill-color"));
    }

    [Fact]
    public void Every_layer_names_a_source_layer_the_tiles_actually_carry()
    {
        // A misspelled source layer draws nothing and reports nothing — the map simply comes up
        // missing its roads, which looks like a tile problem and is not.
        using var style = Built();

        foreach (var layer in style.RootElement.GetProperty("layers").EnumerateArray())
        {
            if (layer.GetProperty("type").GetString() == "background")
            {
                continue;
            }

            var named = layer.GetProperty("source-layer").GetString();

            Assert.Contains(named, RealSourceLayers);
            Assert.Equal("omt", layer.GetProperty("source").GetString());
        }
    }

    [Fact]
    public void Every_layer_has_its_own_id()
    {
        using var style = Built();

        var ids = style.RootElement.GetProperty("layers").EnumerateArray()
            .Select(l => l.GetProperty("id").GetString()!)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_land_is_drawn_first_and_the_names_last()
    {
        // Layer order is paint order. Labels under a road fill are labels nobody can read.
        using var style = Built();

        var ids = style.RootElement.GetProperty("layers").EnumerateArray()
            .Select(l => l.GetProperty("id").GetString()!)
            .ToList();

        Assert.Equal("land", ids[0]);
        Assert.True(ids.IndexOf("water") < ids.IndexOf("road-major"), "roads draw over water");
        Assert.True(ids.IndexOf("road-casing") < ids.IndexOf("road-minor"), "a casing goes under its road");
        Assert.True(ids.IndexOf("road-major") < ids.IndexOf("road-name"), "names draw over roads");
        Assert.EndsWith("-name", ids[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_for_tiles_and_fonts_by_template()
    {
        // No TileJSON fetch: one fewer round-trip before the first tile, and the zoom range and
        // the attribution are stated rather than discovered.
        using var style = Built();
        var source = style.RootElement.GetProperty("sources").GetProperty("omt");

        Assert.Equal("vector", source.GetProperty("type").GetString());
        Assert.Equal(MapStyle.SourceMaxZoom, source.GetProperty("maxzoom").GetInt32());

        var tiles = source.GetProperty("tiles").EnumerateArray().Single().GetString()!;
        Assert.Contains("{z}/{x}/{y}", tiles, StringComparison.Ordinal);
        Assert.DoesNotContain("openstreetmap.org", tiles, StringComparison.OrdinalIgnoreCase);

        var glyphs = style.RootElement.GetProperty("glyphs").GetString()!;
        Assert.Contains("{fontstack}", glyphs, StringComparison.Ordinal);
        Assert.Contains("{range}", glyphs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_credit_travels_with_the_source()
    {
        // ODbL is a licence condition, and the renderer draws it from the source's own attribution.
        using var style = Built();

        Assert.Equal(
            MapSource.Attribution,
            style.RootElement.GetProperty("sources").GetProperty("omt").GetProperty("attribution").GetString());
    }

    [Fact]
    public void Labels_ask_for_a_stack_the_glyph_server_has()
    {
        // Not the theme's font, and it cannot be: a TTF in a theme folder has no glyph ranges.
        using var style = Built();

        foreach (var layer in style.RootElement.GetProperty("layers").EnumerateArray())
        {
            if (layer.GetProperty("type").GetString() != "symbol")
            {
                continue;
            }

            var fonts = layer.GetProperty("layout").GetProperty("text-font")
                .EnumerateArray().Select(f => f.GetString()).ToList();

            Assert.Equal([MapStyle.FontStack], fonts);
        }
    }

    [Fact]
    public void A_style_kept_simple_stays_simple()
    {
        // The point of authoring our own: a glanceable map, not a browsing one. If this ever needs
        // raising, that is a decision rather than a drift.
        using var style = Built();

        Assert.InRange(style.RootElement.GetProperty("layers").GetArrayLength(), 8, 16);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "version": 7, "sources": {}, "layers": [ { "id": "a" } ] }""")]
    [InlineData("""{ "version": 8, "sources": {}, "layers": [] }""")]
    [InlineData("""{ "version": 8, "sources": {}, "layers": [ { "id": "a" } ], "center": [0, 0] }""")]
    public void Anything_malformed_is_refused_rather_than_handed_to_the_renderer(string? style)
    {
        Assert.False(MapStyle.IsWellFormed(style));
    }

    private static Dictionary<string, JsonElement> Layers(JsonDocument style) =>
        style.RootElement.GetProperty("layers").EnumerateArray()
            .ToDictionary(l => l.GetProperty("id").GetString()!, l => l, StringComparer.Ordinal);

    private static string? Paint(JsonElement layer, string property) =>
        layer.GetProperty("paint").GetProperty(property).GetString();
}
