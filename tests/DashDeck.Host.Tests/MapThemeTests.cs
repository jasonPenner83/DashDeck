using DashDeck.Host.Maps;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>
/// The map's colours as theme tokens (ADR-0064).
/// </summary>
/// <remarks>
/// The claim this design rests on is that a theme which says nothing about maps still gets a map
/// that matches it, because every map token's default follows one that already exists. That is
/// what is pinned here — along with night, which comes free through the same dimming rules and
/// would otherwise be ten more values to maintain by hand in every theme.
/// </remarks>
public sealed class MapThemeTests
{
    private static readonly string[] MapKeys =
    [
        "mapLand", "mapWater", "mapGreen", "mapBuilding", "mapRoad",
        "mapRoadMajor", "mapRoadCasing", "mapLabel", "mapLabelHalo", "mapMarker",
    ];

    private static ThemeDefinition Theme(params (string Key, string Value)[] tokens) => new()
    {
        Name = "Test",
        Tokens = tokens.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal),
    };

    [Fact]
    public void The_map_tokens_are_part_of_the_vocabulary()
    {
        foreach (var key in MapKeys)
        {
            Assert.True(ThemeTokens.TryGet(key, out var token), key);
            Assert.Equal(TokenKind.Colour, token.Kind);
        }
    }

    [Fact]
    public void They_paint_no_wpf_so_they_carry_no_resource_key()
    {
        // The guard in ThemeService depends on this: a colour token with no key is skipped rather
        // than written to the empty-string resource, where all ten would overwrite one another.
        foreach (var key in MapKeys)
        {
            ThemeTokens.TryGet(key, out var token);
            Assert.Equal("", token.ResourceKey);
        }
    }

    [Fact]
    public void A_theme_that_says_nothing_about_maps_still_gets_a_map_that_matches_it()
    {
        // The whole argument for tokens over style files. Change the dash's canvas and the map's
        // ground follows, with no map-specific lines in the theme at all.
        var resolved = ThemeResolver.Resolve(Theme(("canvas", "#101020")), night: false);
        var palette = MapTheme.From(resolved);

        Assert.Equal("#101020", palette.Land);

        // And the halo follows the land, which follows the canvas — two links of indirection.
        Assert.Equal("#101020", palette.LabelHalo);
    }

    [Fact]
    public void The_text_ramp_carries_the_roads_and_the_names_with_it()
    {
        var resolved = ThemeResolver.Resolve(
            Theme(("textLow", "#704040"), ("textMid", "#A06060"), ("textHigh", "#FFE0C0")),
            night: false);

        var palette = MapTheme.From(resolved);

        Assert.Equal("#704040", palette.Road);
        Assert.Equal("#A06060", palette.RoadMajor);
        Assert.Equal("#FFE0C0", palette.Label);
    }

    [Fact]
    public void The_accent_is_the_vehicle()
    {
        var resolved = ThemeResolver.Resolve(Theme(("accent", "#FF9900")), night: false);

        Assert.Equal("#FF9900", MapTheme.From(resolved).Marker);
    }

    [Fact]
    public void A_theme_can_still_say_exactly_what_it_wants()
    {
        // A Star Trek map is this, and nothing more: a handful of colours in the theme file.
        var resolved = ThemeResolver.Resolve(
            Theme(
                ("mapLand", "#000000"),
                ("mapWater", "#1A1A2E"),
                ("mapRoad", "#CC6666"),
                ("mapRoadMajor", "#FF9966"),
                ("mapLabel", "#FFCC66")),
            night: false);

        var palette = MapTheme.From(resolved);

        Assert.Equal("#000000", palette.Land);
        Assert.Equal("#1A1A2E", palette.Water);
        Assert.Equal("#CC6666", palette.Road);
        Assert.Equal("#FF9966", palette.RoadMajor);
        Assert.Equal("#FFCC66", palette.Label);

        // Named nothing, so it follows the land it was given rather than the default canvas.
        Assert.Equal("#000000", palette.LabelHalo);
    }

    [Fact]
    public void Night_dims_the_map_the_way_it_dims_everything_else()
    {
        // Free, and the reason this is a token rather than a file: ten night values that would
        // otherwise be hand-maintained in every theme.
        var day = MapTheme.From(ThemeResolver.Resolve(Theme(("mapRoad", "#808080")), night: false));
        var night = MapTheme.From(ThemeResolver.Resolve(Theme(("mapRoad", "#808080")), night: true));

        Assert.Equal("#808080", day.Road);
        Assert.NotEqual(day.Road, night.Road);

        // Ink, not surface: roads dim less than the ground under them, so the contrast holds.
        Assert.Equal("#5F5F5F", night.Road);
    }

    [Fact]
    public void The_ground_dims_further_than_the_names_on_it()
    {
        var night = MapTheme.From(ThemeResolver.Resolve(Theme(), night: true));
        var day = MapTheme.From(ThemeResolver.Resolve(Theme(), night: false));

        static int Brightness(string hex) =>
            Convert.ToInt32(hex[1..3], 16) + Convert.ToInt32(hex[3..5], 16) + Convert.ToInt32(hex[5..7], 16);

        // Surface dims to 0.55 and ink to 0.74, so a night map keeps its labels legible over a
        // darker ground rather than fading evenly into illegibility.
        Assert.True(Brightness(night.Water) < Brightness(day.Water));
        Assert.True(Brightness(night.Label) < Brightness(day.Label));

        var groundLoss = (double)Brightness(night.Water) / Math.Max(1, Brightness(day.Water));
        var inkLoss = (double)Brightness(night.Label) / Math.Max(1, Brightness(day.Label));

        Assert.True(groundLoss < inkLoss, "the ground should dim further than the ink on it");
    }

    [Fact]
    public void Every_colour_reaches_the_map_opaque()
    {
        // A theme may write alpha and MapLibre would honour it, but translucent roads over
        // translucent landcover is how a map turns to mud.
        var resolved = ThemeResolver.Resolve(Theme(("mapWater", "#80102030")), night: false);
        var palette = MapTheme.From(resolved);

        Assert.All(palette.Colours, colour => Assert.Equal(7, colour.Length));
        Assert.All(palette.Colours, colour => Assert.StartsWith("#", colour, StringComparison.Ordinal));
    }

    [Fact]
    public void A_themes_map_colours_build_a_style_that_parses()
    {
        // The two halves meeting: tokens through the resolver, into the style the renderer gets.
        var resolved = ThemeResolver.Resolve(Theme(("mapLand", "#000000"), ("mapWater", "#1A1A2E")), night: false);
        var style = DashDeck.Core.Maps.MapStyle.Build(MapTheme.From(resolved));

        Assert.True(DashDeck.Core.Maps.MapStyle.IsWellFormed(style));
        Assert.Contains("#1A1A2E", style, StringComparison.Ordinal);
    }
}
