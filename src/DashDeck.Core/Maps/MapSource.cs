namespace DashDeck.Core.Maps;

/// <summary>
/// Where the map's tiles and its look come from (ADR-0063).
/// </summary>
/// <remarks>
/// <b>OpenFreeMap</b>, fetched as you drive. No API key, no registration, no stated request
/// limit, commercial use allowed — and it publishes the same planet as a PMTiles archive, which
/// is what the offline half will be seeded from, so online and offline end up the same data and
/// the same look rather than two maps that disagree.
/// <para>
/// <b>Not OpenStreetMap's own tile servers.</b> Their usage policy prohibits fetching tiles ahead
/// of being viewed, and names archives and offline use specifically — which is exactly the shape
/// of a dash that keeps what it downloads. The OSM <em>data</em> is what both serve; the capacity
/// to serve it is what the policy protects.
/// </para>
/// <para>
/// Attribution is a licence obligation, not a courtesy: OSM is ODbL, and OpenFreeMap asks for
/// itself and OpenMapTiles to be named alongside. MapLibre renders the style's own attribution
/// control; <see cref="Attribution"/> is here so nothing else has to guess at the wording.
/// </para>
/// </remarks>
public static class MapSource
{
    /// <summary>
    /// The style the dash opens with: OpenFreeMap's <c>dark</c>.
    /// </summary>
    /// <remarks>
    /// Dark because this screen is mostly looked at through a windscreen, half of it after
    /// sunset, and because the rest of the dash is dark — a white map in a dark cab is a torch.
    /// Their <c>bright</c>, <c>liberty</c>, <c>positron</c> and <c>fiord</c> are the same tiles
    /// with another look, which is why <c>--map-style</c> exists rather than a rebuild.
    /// </remarks>
    public const string StyleUrl = "https://tiles.openfreemap.org/styles/dark";

    /// <summary>The tiles the styles point at, named here because the offline seed comes from the
    /// same deployment. Not fetched directly — the style does that.</summary>
    public const string TileJsonUrl = "https://tiles.openfreemap.org/planet/latest";

    /// <summary>Where a PMTiles archive of the same planet is published, for the offline half.</summary>
    /// <remarks>
    /// Weekly runs, kept for the last four plus the first of each of the last six months, with a
    /// version index at <c>files.txt</c>. A province is cut out of it rather than built from an
    /// OSM extract, which is the whole reason the two halves can look identical.
    /// </remarks>
    public const string PlanetArchiveIndex = "https://btrfs.openfreemap.com/files.txt";

    /// <summary>Who to credit, in the words the licences ask for.</summary>
    public const string Attribution = "© OpenStreetMap contributors · OpenMapTiles · OpenFreeMap";

    /// <summary>
    /// Imagery for the satellite map: EOX's Sentinel-2 cloudless mosaic, 2020 (ADR-0071).
    /// </summary>
    /// <remarks>
    /// No key and no account, and the template is the one MapLibre's own satellite example uses.
    /// The 2018–2021 mosaics are licensed for <b>non-commercial use with attribution</b>, which a
    /// personal dash is. Ten metres a pixel: fields, rivers, yards and towns read; a house does not.
    /// Served to zoom 14 and stretched past it, so close in it is soft rather than missing.
    /// <para>
    /// Not Esri's World Imagery, which is sharper and needs an ArcGIS Location Platform key to use
    /// outside Esri's own software. That is a setting for later, if this is not enough.
    /// </para>
    /// </remarks>
    public const string ImageryTemplate =
        "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2020_3857/default/g/{z}/{y}/{x}.jpg";

    /// <summary>The highest zoom the imagery is served at.</summary>
    public const int ImageryMaxZoom = 14;

    /// <summary>Who to credit for the imagery, in the words EOX asks for.</summary>
    public const string ImageryAttribution =
        "Sentinel-2 cloudless by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2020)";

    /// <summary>
    /// Elevation for the terrain map: Terrain Tiles on AWS, in the Terrarium encoding (ADR-0071).
    /// </summary>
    /// <remarks>
    /// An AWS open dataset — no key, no account, free to fetch — built by Mapzen from SRTM, GMTED,
    /// USGS 3DEP, Natural Resources Canada's CDEM and others. Each tile is a PNG whose colours are
    /// a height, which MapLibre shades into hills itself. Probed from the session that wrote this.
    /// </remarks>
    public const string TerrainTemplate = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";

    /// <summary>The highest zoom the elevation is served at.</summary>
    public const int TerrainMaxZoom = 15;

    /// <summary>Who to credit for the elevation.</summary>
    public const string TerrainAttribution = "Terrain Tiles on AWS · Mapzen, SRTM, GMTED, 3DEP, CDEM and others";

    /// <summary>
    /// The host name the map page is served under inside WebView2.
    /// </summary>
    /// <remarks>
    /// A virtual host mapped to a folder in the deploy, rather than a <c>file://</c> URL, because
    /// the page is an ES module and loads a web worker — both of which a file origin refuses. It
    /// is not a real domain and never resolves on the network.
    /// </remarks>
    public const string PageHost = "dashdeck.map";

    /// <summary>The page itself.</summary>
    public static string PageUrl => $"https://{PageHost}/index.html";

    /// <summary>The folder, inside the deploy, that <see cref="PageHost"/> serves.</summary>
    public const string PageFolder = "map";

    /// <summary>
    /// Whether a style address is one we are willing to load.
    /// </summary>
    /// <remarks>
    /// Guards the <c>--map-style</c> override, which is a URL off the command line going straight
    /// into a browser control. HTTPS only, and no credentials in the address — a style is a
    /// public document and anything that needs a password to fetch is not one.
    /// </remarks>
    public static bool IsAcceptableStyle(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(parsed.UserInfo);

    /// <summary>The style to load: the override when it is usable, otherwise <see cref="StyleUrl"/>.</summary>
    public static string StyleOr(string? overrideUrl) =>
        IsAcceptableStyle(overrideUrl) ? overrideUrl!.Trim() : StyleUrl;
}
