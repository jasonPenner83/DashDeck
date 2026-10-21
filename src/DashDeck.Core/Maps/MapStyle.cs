using System.Text.Json;
using System.Text.Json.Nodes;

namespace DashDeck.Core.Maps;

/// <summary>
/// The map's own style, built from the theme's colours (ADR-0064).
/// </summary>
/// <remarks>
/// <b>Deliberately far simpler than a browsing map.</b> OpenFreeMap's own styles are two hundred
/// layers tuned for a desktop, a mouse and time to look; this is eleven, tuned for a glance
/// through a windscreen. Fewer colours, bolder lines, bigger labels. That is not a shortcut —
/// a cartographic style read at 100 km/h is a different problem from one read at a desk.
/// <para>
/// It also makes theming possible at all. A style that a theme recolours has to be one we can
/// account for, and eleven layers from ten colours is reviewable where somebody else's two
/// hundred is not — their structure is not a contract and changes weekly.
/// </para>
/// <para>
/// Pure, and in Core, so the whole thing is tested without Windows, a browser or a signal: the
/// JSON parses, every layer names a real OpenMapTiles source layer, every colour is the theme's,
/// and nothing fixes a camera — which would undo the camera the dash is keeping (Q43).
/// </para>
/// </remarks>
public static class MapStyle
{
    /// <summary>
    /// The vector tiles, by template rather than through a TileJSON document.
    /// </summary>
    /// <remarks>
    /// One fewer network round-trip before the first tile, and one fewer thing to be wrong: the
    /// template, the zoom range and the attribution are all stated here instead of fetched. The
    /// tiles stop at 14 and MapLibre overzooms past it, which stays sharp because they are vector.
    /// </remarks>
    public const string TileTemplate = "https://tiles.openfreemap.org/planet/latest/{z}/{x}/{y}.pbf";

    /// <summary>Where the label fonts come from, read from OpenFreeMap's own published style.</summary>
    /// <remarks>
    /// The one thing on the map that is neither tiles nor our own: signed-distance-field glyphs.
    /// It means <b>labels are the first thing to fail when signal drops</b> — the roads and water
    /// keep drawing from cached tiles and the names go. Vendoring them beside MapLibre fixes that
    /// and is deliberately left for later (Q44), where it belongs with the rest of offline.
    /// </remarks>
    public const string GlyphsTemplate = "https://tiles.openfreemap.org/fonts/{fontstack}/{range}.pbf";

    /// <summary>
    /// The font stack asked of the glyph server.
    /// </summary>
    /// <remarks>
    /// Not the theme's font, and it cannot be: a map label needs glyphs built into PBF ranges,
    /// which the server has for this stack and not for a TTF sitting in a theme folder. So an
    /// LCARS map is Star Trek colours in Noto Sans until there is a tool that builds glyphs from
    /// a theme's own font.
    /// </remarks>
    public const string FontStack = "Noto Sans Regular";

    /// <summary>The highest zoom the tiles themselves carry.</summary>
    public const int SourceMaxZoom = 14;

    /// <summary>Build a style in these colours, over the chosen base (ADR-0071).</summary>
    /// <remarks>
    /// The road map is the template as it stands. The other two are the same style changed, rather
    /// than styles of their own, so the roads and names a theme paints are the same on all three:
    /// <list type="bullet">
    /// <item><b>Satellite</b> puts imagery straight over the ground and drops the fills it makes
    /// redundant — water, greenery and buildings are in the picture. The roads and the names stay,
    /// in the theme's colours, because imagery from ten metres up does not say which road is which.</item>
    /// <item><b>Terrain</b> shades the ground from elevation tiles, under the water and the roads,
    /// in the theme's own colours so it darkens and lightens with the rest at dusk.</item>
    /// </list>
    /// </remarks>
    public static string Build(MapPalette palette, MapBase mapBase) => mapBase switch
    {
        MapBase.Satellite => Satellite(Build(palette)),
        MapBase.Terrain => Terrain(Build(palette), palette),
        _ => Build(palette),
    };

    /// <summary>The fills that imagery replaces.</summary>
    private static readonly string[] ImageryCovers = ["green", "water", "waterway", "building"];

    private static string Satellite(string road)
    {
        var style = JsonNode.Parse(road)!.AsObject();

        style["sources"]!.AsObject()["imagery"] = new JsonObject
        {
            ["type"] = "raster",
            ["tiles"] = new JsonArray(MapSource.ImageryTemplate),
            ["tileSize"] = 256,
            ["minzoom"] = 0,
            ["maxzoom"] = MapSource.ImageryMaxZoom,
            ["attribution"] = MapSource.ImageryAttribution,
        };

        var layers = style["layers"]!.AsArray();

        foreach (var covered in layers.Where(l => ImageryCovers.Contains((string?)l!["id"])).ToList())
        {
            layers.Remove(covered);
        }

        // Straight after the background, which stays: it is what shows while the imagery arrives,
        // and the dash's own canvas colour is better than a flash of white.
        layers.Insert(1, new JsonObject
        {
            ["id"] = "imagery",
            ["type"] = "raster",
            ["source"] = "imagery",
            ["paint"] = new JsonObject { ["raster-fade-duration"] = 0 },
        });

        return style.ToJsonString();
    }

    private static string Terrain(string road, MapPalette palette)
    {
        var style = JsonNode.Parse(road)!.AsObject();

        style["sources"]!.AsObject()["elevation"] = new JsonObject
        {
            ["type"] = "raster-dem",
            ["tiles"] = new JsonArray(MapSource.TerrainTemplate),
            ["encoding"] = "terrarium",
            ["tileSize"] = 256,
            ["minzoom"] = 0,
            ["maxzoom"] = MapSource.TerrainMaxZoom,
            ["attribution"] = MapSource.TerrainAttribution,
        };

        var layers = style["layers"]!.AsArray();
        var water = layers.Select((l, i) => (Id: (string?)l!["id"], i)).First(l => l.Id == "water").i;

        // Over the ground and the greenery, under the water and every road, so the shading reads
        // as the shape of the land rather than as something laid over the map. Exaggerated,
        // because most of the province is flat enough that true relief would draw nothing.
        layers.Insert(water, new JsonObject
        {
            ["id"] = "hillshade",
            ["type"] = "hillshade",
            ["source"] = "elevation",
            ["paint"] = new JsonObject
            {
                ["hillshade-shadow-color"] = "#000000",
                ["hillshade-highlight-color"] = palette.RoadMajor,
                ["hillshade-accent-color"] = palette.Road,
                ["hillshade-exaggeration"] = 0.6,
            },
        });

        return style.ToJsonString();
    }

    /// <summary>Build the road map in these colours.</summary>
    public static string Build(MapPalette palette) => Template
        .Replace("$LAND", palette.Land, StringComparison.Ordinal)
        .Replace("$WATER", palette.Water, StringComparison.Ordinal)
        .Replace("$GREEN", palette.Green, StringComparison.Ordinal)
        .Replace("$BUILDING", palette.Building, StringComparison.Ordinal)
        .Replace("$ROADMAJOR", palette.RoadMajor, StringComparison.Ordinal)
        .Replace("$ROADCASING", palette.RoadCasing, StringComparison.Ordinal)
        .Replace("$ROAD", palette.Road, StringComparison.Ordinal)
        .Replace("$LABELHALO", palette.LabelHalo, StringComparison.Ordinal)
        .Replace("$LABEL", palette.Label, StringComparison.Ordinal)
        .Replace("$TILES", TileTemplate, StringComparison.Ordinal)
        .Replace("$GLYPHS", GlyphsTemplate, StringComparison.Ordinal)
        .Replace("$FONT", FontStack, StringComparison.Ordinal)
        .Replace("$ATTRIBUTION", MapSource.Attribution, StringComparison.Ordinal)
        .Replace("$MAXZOOM", SourceMaxZoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>
    /// Whether a built style is one worth handing to the renderer.
    /// </summary>
    /// <remarks>
    /// Checked rather than trusted, because a malformed style is a dark rectangle with no error —
    /// the one failure this dash will not have. The caller falls back to the hosted style and says
    /// so, instead of showing nothing.
    /// </remarks>
    public static bool IsWellFormed(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(style);
            var root = parsed.RootElement;

            return root.TryGetProperty("version", out var version)
                && version.GetInt32() == 8
                && root.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("layers", out var layers)
                && layers.ValueKind == JsonValueKind.Array
                && layers.GetArrayLength() > 0

                // A style that fixes a camera would undo the one the dash keeps across a rebuild.
                && !root.TryGetProperty("center", out _)
                && !root.TryGetProperty("zoom", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The style, with <c>$NAME</c> standing in for every colour.
    /// </summary>
    /// <remarks>
    /// A template with substitutions rather than a serialiser, because this is read far more often
    /// than it is written: the layer order, the filters and the zoom ramps are the design, and they
    /// are legible here in a way that two hundred lines of writer calls would not be.
    /// <para>
    /// Source layers are OpenMapTiles' (<c>water</c>, <c>transportation</c>, <c>place</c> and the
    /// rest), read from OpenFreeMap's own dark style rather than assumed.
    /// </para>
    /// </remarks>
    private const string Template = """
        {
          "version": 8,
          "name": "DashDeck",
          "glyphs": "$GLYPHS",
          "sources": {
            "omt": {
              "type": "vector",
              "tiles": ["$TILES"],
              "minzoom": 0,
              "maxzoom": $MAXZOOM,
              "attribution": "$ATTRIBUTION"
            }
          },
          "layers": [
            { "id": "land", "type": "background",
              "paint": { "background-color": "$LAND" } },

            { "id": "green", "type": "fill", "source": "omt", "source-layer": "landcover",
              "filter": ["match", ["get", "class"], ["wood", "grass", "farmland", "wetland"], true, false],
              "paint": { "fill-color": "$GREEN" } },

            { "id": "water", "type": "fill", "source": "omt", "source-layer": "water",
              "filter": ["!=", ["get", "intermittent"], 1],
              "paint": { "fill-color": "$WATER" } },

            { "id": "waterway", "type": "line", "source": "omt", "source-layer": "waterway",
              "paint": {
                "line-color": "$WATER",
                "line-width": ["interpolate", ["linear"], ["zoom"], 8, 0.6, 14, 2.5]
              } },

            { "id": "building", "type": "fill", "source": "omt", "source-layer": "building",
              "minzoom": 14,
              "paint": { "fill-color": "$BUILDING" } },

            { "id": "road-casing", "type": "line", "source": "omt", "source-layer": "transportation",
              "minzoom": 6,
              "filter": ["match", ["get", "class"],
                ["motorway", "trunk", "primary", "secondary", "tertiary", "minor", "service"], true, false],
              "layout": { "line-cap": "round", "line-join": "round" },
              "paint": {
                "line-color": "$ROADCASING",
                "line-width": ["interpolate", ["linear"], ["zoom"], 6, 2.5, 12, 7, 16, 18]
              } },

            { "id": "road-minor", "type": "line", "source": "omt", "source-layer": "transportation",
              "minzoom": 12,
              "filter": ["match", ["get", "class"], ["minor", "service"], true, false],
              "layout": { "line-cap": "round", "line-join": "round" },
              "paint": {
                "line-color": "$ROAD",
                "line-width": ["interpolate", ["linear"], ["zoom"], 12, 1, 16, 7]
              } },

            { "id": "road-mid", "type": "line", "source": "omt", "source-layer": "transportation",
              "minzoom": 8,
              "filter": ["match", ["get", "class"], ["secondary", "tertiary"], true, false],
              "layout": { "line-cap": "round", "line-join": "round" },
              "paint": {
                "line-color": "$ROAD",
                "line-width": ["interpolate", ["linear"], ["zoom"], 8, 1.2, 12, 3, 16, 10]
              } },

            { "id": "road-major", "type": "line", "source": "omt", "source-layer": "transportation",
              "filter": ["match", ["get", "class"], ["motorway", "trunk", "primary"], true, false],
              "layout": { "line-cap": "round", "line-join": "round" },
              "paint": {
                "line-color": "$ROADMAJOR",
                "line-width": ["interpolate", ["linear"], ["zoom"], 4, 0.8, 8, 2, 12, 5, 16, 14]
              } },

            { "id": "road-name", "type": "symbol", "source": "omt", "source-layer": "transportation_name",
              "minzoom": 13,
              "layout": {
                "text-field": ["get", "name"],
                "text-font": ["$FONT"],
                "text-size": 13,
                "symbol-placement": "line",
                "text-rotation-alignment": "map"
              },
              "paint": {
                "text-color": "$LABEL",
                "text-halo-color": "$LABELHALO",
                "text-halo-width": 1.5
              } },

            { "id": "place-name", "type": "symbol", "source": "omt", "source-layer": "place",
              "filter": ["match", ["get", "class"], ["city", "town", "village", "suburb"], true, false],
              "layout": {
                "text-field": ["get", "name"],
                "text-font": ["$FONT"],
                "text-size": ["interpolate", ["linear"], ["zoom"], 4, 12, 10, 18, 14, 22],
                "text-max-width": 8
              },
              "paint": {
                "text-color": "$LABEL",
                "text-halo-color": "$LABELHALO",
                "text-halo-width": 1.8
              } }
          ]
        }
        """;
}
