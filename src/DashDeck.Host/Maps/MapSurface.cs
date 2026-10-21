using System.Globalization;
using System.Text.Json;
using System.Windows;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace DashDeck.Host.Maps;

/// <summary>What the dash can tell a loaded map to do.</summary>
/// <remarks>
/// An interface so <see cref="MapViewModel"/> can hold the live surface without holding a browser
/// control — the view-model is the one part of this feature that is testable without Windows, and
/// it stays that way.
/// </remarks>
public interface IMapSurface
{
    /// <summary>Move the vehicle marker, and the map with it while it is following.</summary>
    /// <param name="fix">Where the vehicle is.</param>
    /// <param name="live">False for a stale or absent fix, which drains the marker's colour.</param>
    void Position(LocationFix fix, bool live);

    /// <summary>Put the map back where it was looking.</summary>
    void Camera(MapCamera camera);

    /// <summary>Follow the vehicle, or leave the map where it has been panned to.</summary>
    void Follow(bool on);

    /// <summary>
    /// Draw the map in these colours from now on.
    /// </summary>
    /// <param name="styleJson">
    /// A style built from the theme, or null to keep whatever the page was started with — which is
    /// the hosted style, under a <c>--map-style</c> override or after a style failed to build.
    /// </param>
    /// <param name="palette">
    /// The theme's map colours. The style paints the cartography; these also reach the things it
    /// does not — the vehicle, the pin and the popup's chrome (ADR-0064).
    /// </param>
    void Restyle(string? styleJson, MapPalette palette);

    /// <summary>Fly to a chosen place, pin it, and open what is known about it.</summary>
    /// <param name="camera">Where it is.</param>
    /// <param name="place">What was found there.</param>
    void Destination(MapCamera camera, GeocodeResult place);

    /// <summary>Draw a route, and fit the map around it.</summary>
    void Route(Route route);

    /// <summary>Rub out the route, the pin and the popup.</summary>
    void ClearRoute();

    /// <summary>Draw the buttons over the map in this state (ADR-0071).</summary>
    void Buttons(MapButtons buttons);

    /// <summary>
    /// Draw the range lines (ADR-0071), replacing any already drawn.
    /// </summary>
    /// <param name="ringsJson">The rings around the truck, as <see cref="RangeLines.RingsJson"/> gives them.</param>
    /// <param name="routeJson">The route's low and beyond stretches, as <see cref="RangeLines.RouteJson"/> gives them.</param>
    /// <param name="live">False for a stale range, which drains the lines' colour.</param>
    void Range(string ringsJson, string routeJson, bool live);
}

/// <summary>What the buttons over the map show (ADR-0071).</summary>
/// <param name="Choices">The base, the orientation and whether the range lines are on.</param>
/// <param name="Search">Whether there is a search to offer.</param>
/// <param name="Recentre">Whether the map has been panned off the truck.</param>
/// <param name="Range">What the range button says: <c>412 KM</c>, <c>NO RANGE</c> or <c>OFF</c>.</param>
public readonly record struct MapButtons(MapChoices Choices, bool Search, bool Recentre, string Range);

/// <summary>
/// The map itself: MapLibre GL JS in a WebView2, on tiles fetched as you drive (ADR-0063).
/// </summary>
/// <remarks>
/// The second WebView2 in the dash, and deliberately not the first one reused: a
/// <see cref="Stage.WebStageOccupant"/> points a browser at somebody else's URL, where this serves
/// our own page from the deploy folder and talks to it. What they share is the hard-won setup —
/// a writable profile folder outside the install, and a <c>postMessage</c> channel rather than
/// script return values.
/// <para>
/// <b>Served from a virtual host, not <c>file://</c>.</b> The page is an ES module and MapLibre
/// starts a web worker; a file origin refuses both. <c>SetVirtualHostNameToFolderMapping</c> gives
/// the deploy's <c>map</c> folder a real HTTPS origin that never touches the network.
/// </para>
/// <para>
/// <b>One surface per placement, and it reloads when the map moves between them</b> — that is
/// Q43, out in the open rather than designed around. A browser control cannot be in two places in
/// the visual tree, so the camera lives outside the page (<see cref="MapCamera"/>) and is handed
/// back on load. What is lost is the tiles already decoded, which is a second of redraw.
/// </para>
/// </remarks>
public sealed class MapSurface : IMapSurface, IDisposable
{
    private readonly MapViewModel _map;
    private WebView2? _view;
    private bool _ready;
    private bool _disposed;

    /// <param name="map">
    /// The view-model this surface reports to, and takes its opening camera and style from.
    /// </param>
    public MapSurface(MapViewModel map) => _map = map;

    /// <summary>The control to put on screen. Built once.</summary>
    public FrameworkElement View => _view ??= Build();

    private WebView2 Build()
    {
        var view = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                // Shared with the web occupant on purpose: one browser profile, one cache, and
                // the tiles this map fetches are warm for the next launch. Beside everything
                // else of ours in %LOCALAPPDATA%, because the install folder may be read-only.
                UserDataFolder = UserDataFolder(),
            },

            // Without this the control flashes white before the first frame, which on a dark
            // dash at night is a camera flash.
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0D, 0x0C, 0x0B),
        };

        _view = view;
        _ = StartAsync(view);
        return view;
    }

    private async Task StartAsync(WebView2 view)
    {
        try
        {
            await view.EnsureCoreWebView2Async().ConfigureAwait(true);

            if (_disposed || view.CoreWebView2 is not { } core)
            {
                return;
            }

            // A dash, not a browser. None of this is reachable without a keyboard anyway, but a
            // long-press context menu over the map is reachable with a thumb.
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                MapSource.PageHost,
                PageFolder(),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += (_, message) =>
            {
                // TryGetWebMessageAsString throws rather than returning null when the page sent
                // an object, and the shared profile means another page could.
                string? text = null;

                try
                {
                    text = message.TryGetWebMessageAsString();
                }
                catch (Exception)
                {
                    return;
                }

                if (MapMessage.TryParse(text, out var parsed))
                {
                    _ready |= parsed.Signal == MapSignal.Ready;
                    _map.OnMessage(parsed);
                }
            };

            // Before the module runs, so the map opens where it was rather than jumping there
            // after a frame. This is what makes a placement change read as a redraw and not as
            // the map losing your place.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.dashBoot = {BootJson()};").ConfigureAwait(true);

            if (!_disposed)
            {
                view.Source = new Uri(MapSource.PageUrl);
            }
        }
        catch (Exception error)
        {
            // The Evergreen runtime ships with Windows 11, so this is the unlikely path — but a
            // map that silently never appears is the one outcome this dash will not have.
            _map.OnMessage(new MapMessage(MapSignal.Failed, Detail: Reason(error)));
        }
    }

    /// <summary>The opening camera, style and marker, as the page's own boot object.</summary>
    /// <remarks>
    /// The style goes in as a <b>JSON object</b> when the theme built one, and as a quoted URL
    /// when it did not — MapLibre's <c>style</c> option takes either, which is what lets the
    /// theme's own cartography and somebody else's hosted style share one code path.
    /// </remarks>
    private string BootJson()
    {
        var camera = _map.Camera;

        var place = camera is { } c
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"\"lon\":{c.Longitude:0.######},\"lat\":{c.Latitude:0.######},\"zoom\":{c.Zoom:0.##},\"bearing\":{c.Bearing:0.#},")
            : string.Empty;

        // Either the theme's style verbatim, or a URL — ours or validated as HTTPS with no
        // credentials (MapSource), so the only escaping left that matters is the quote.
        var style = _map.StyleJson
            ?? $"\"{StyleUrl().Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        return $"{{{place}\"style\":{style},\"marker\":\"{_map.MarkerColour}\"}}";
    }

    /// <summary>The hosted style to fall back on: the override, or OpenFreeMap's dark.</summary>
    private string StyleUrl() => MapSource.StyleOr(_map.StyleOverride);

    /// <inheritdoc />
    public void Restyle(string? styleJson, MapPalette palette)
    {
        // The palette first and always: the vehicle, the pin and the popup are ours to paint
        // whoever drew the ground underneath them.
        Run("palette", $"window.dash.palette({PaletteJson(palette)})");

        if (styleJson is not null)
        {
            // The style is JSON, which is a JavaScript expression — so it goes in as one rather
            // than as a string the page would have to parse.
            Run("restyle", $"window.dash.restyle({styleJson})");
        }
    }

    /// <inheritdoc />
    public void Destination(MapCamera camera, GeocodeResult place) => Run(
        "destination",
        string.Create(
            CultureInfo.InvariantCulture,
            $"window.dash.destination({camera.Longitude:0.######},{camera.Latitude:0.######},{camera.Zoom:0.##},{PlaceJson(place)})"));

    /// <inheritdoc />
    public void Route(Route route) => Run("route", $"window.dash.route({ShapeJson(route)})");

    /// <inheritdoc />
    public void ClearRoute() => Run("clearRoute", "window.dash.clearRoute()");

    /// <inheritdoc />
    public void Buttons(MapButtons buttons) => Run("buttons", $"window.dash.buttons({ButtonsJson(buttons)})");

    /// <inheritdoc />
    /// <remarks>
    /// Both are GeoJSON built by <see cref="RangeLines"/> with <c>JsonSerializer</c>, so they go in
    /// as expressions, the way the style does.
    /// </remarks>
    public void Range(string ringsJson, string routeJson, bool live) =>
        Run("range", $"window.dash.range({ringsJson},{routeJson},{(live ? "true" : "false")})");

    /// <summary>The buttons' state, by the names the page uses.</summary>
    private static string ButtonsJson(MapButtons buttons) => JsonSerializer.Serialize(new
    {
        mapBase = buttons.Choices.Base.ToString().ToLowerInvariant(),
        trackUp = buttons.Choices.Orientation == MapOrientation.TrackUp,
        rangeOn = buttons.Choices.ShowRange,
        range = buttons.Range,
        search = buttons.Search,
        recentre = buttons.Recentre,
    });

    /// <summary>
    /// The place, as the page's popup wants it.
    /// </summary>
    /// <remarks>
    /// Serialised rather than hand-quoted, and that is not fussiness: a place name, a website and
    /// opening hours all come from OpenStreetMap, which is to say from anybody. The page builds
    /// its popup with <c>textContent</c> so none of it is ever parsed as markup, and this end
    /// escapes it properly so none of it can end a string and become script.
    /// </remarks>
    private static string PlaceJson(GeocodeResult place) => JsonSerializer.Serialize(new
    {
        label = place.Label,
        detail = place.Detail,
        kind = place.Details.Kind,
        hours = place.Details.Hours,
        phone = place.Details.Phone,
        website = place.Details.Website,
    });

    /// <summary>The theme's colours, under the names the page's CSS variables use.</summary>
    private static string PaletteJson(MapPalette palette) => JsonSerializer.Serialize(new
    {
        marker = palette.Marker,
        ink = palette.Label,
        mid = palette.RoadMajor,
        line = palette.Road,
        land = palette.Land,
        surface = palette.Building,
    });

    /// <summary>The route's line, as longitude/latitude pairs.</summary>
    private static string ShapeJson(Route route) => JsonSerializer.Serialize(
        route.Shape.Select(point => new[] { point.Longitude, point.Latitude }));

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <inheritdoc />
    public void Position(LocationFix fix, bool live) => Run(
        "position",
        string.Create(
            CultureInfo.InvariantCulture,
            $"window.dash.position({fix.Longitude:0.######},{fix.Latitude:0.######},{Course(fix)},{(live ? "true" : "false")})"));

    /// <inheritdoc />
    public void Camera(MapCamera camera) => Run(
        "camera",
        string.Create(
            CultureInfo.InvariantCulture,
            $"window.dash.camera({camera.Longitude:0.######},{camera.Latitude:0.######},{camera.Zoom:0.##},{camera.Bearing:0.#})"));

    /// <inheritdoc />
    public void Follow(bool on) => Run("follow", $"window.dash.follow({(on ? "true" : "false")})");

    /// <inheritdoc />
    public void Destination(MapCamera camera, string label) => Run(
        "destination",
        string.Create(
            CultureInfo.InvariantCulture,
            $"window.dash.destination({camera.Longitude:0.######},{camera.Latitude:0.######},{camera.Zoom:0.##},{Quote(label)})"));

    /// <summary>
    /// The course to draw the marker's cone at, or <c>NaN</c> to leave it where it is.
    /// </summary>
    /// <remarks>
    /// Below about walking pace a GPS course is noise (ADR-0027's rule, the same one the compass
    /// uses), and a cone spinning on the spot at a red light is worse than one pointing at the
    /// last direction actually travelled.
    /// </remarks>
    private static string Course(LocationFix fix) =>
        fix.HasUsableCourse(MinCourseSpeedKmh)
            ? string.Create(CultureInfo.InvariantCulture, $"{fix.CourseDegrees:0.#}")
            : "NaN";

    /// <summary>The speed a GPS course starts being worth drawing, in km/h.</summary>
    private const double MinCourseSpeedKmh = 5;

    /// <summary>Run a script against the page, but only once it has one.</summary>
    private void Run(string what, string script)
    {
        if (_disposed || !_ready || _view?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            // Fire and forget deliberately: this is called on the dash's one-second beat, and
            // awaiting it would queue a second's worth of scripts behind a slow frame.
            _ = _view.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // A browser control that objects to being driven must not take the dash with it.
            // Nothing is reported, because the next beat is a second away and will say more.
            _ = what;
        }
    }

    /// <summary>What to say when the control itself would not start.</summary>
    private static string Reason(Exception error) => error switch
    {
        System.IO.FileNotFoundException or System.DllNotFoundException =>
            "the WebView2 runtime is missing — it ships with Windows 11",
        _ => error.Message,
    };

    /// <summary>The deploy's own map folder, beside the executable in a build and in a publish alike.</summary>
    private static string PageFolder() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, MapSource.PageFolder);

    private static string UserDataFolder()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DashDeck",
            "WebView2");

        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Without this the browser process outlives the surface, and a map left fetching tiles
        // on a hotspot is a bill rather than a leak.
        _view?.Dispose();
        _view = null;
    }
}
