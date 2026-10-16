using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// The camera the map page is driven by, the bridge it talks over, and the tile source (ADR-0063).
/// </summary>
/// <remarks>
/// All three are pure on purpose, because none of the rest can be tested without Windows, a
/// WebView2 and a signal. What is left here is what actually breaks: a locale that writes
/// <c>49,8951</c>, a <c>NaN</c> arriving from JavaScript, and a string protocol between two
/// languages that stops being understood without anything saying so.
/// </remarks>
public sealed class MapSourceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // --- The camera ---

    [Fact]
    public void A_camera_over_a_fix_sits_on_it_at_a_driving_zoom()
    {
        var fix = new LocationFix(49.8951, -97.1384, 62, 84, SignalQuality.Live, Noon);
        var camera = MapCamera.Over(fix);

        Assert.Equal(49.8951, camera.Latitude, 4);
        Assert.Equal(-97.1384, camera.Longitude, 4);
        Assert.Equal(MapCamera.DrivingZoom, camera.Zoom);

        // North up, not course up: the heading is only trustworthy while moving (ADR-0027), so
        // the marker carries it and the map stays still.
        Assert.Equal(0, camera.Bearing);
    }

    [Fact]
    public void The_wire_format_is_invariant_whatever_the_dash_is_set_to()
    {
        // A locale that writes a decimal comma turns "49,8951" into two numbers inside a
        // comma-separated list. This is the bug that would only ever appear on his machine:
        // the engine runs under InvariantGlobalization, but the Host switches it off (it has
        // to — WPF cannot render without ICU), so the shell can genuinely be in such a culture.
        //
        // Built by hand rather than looked up by name, because a name lookup is exactly what
        // invariant mode refuses — which is why this test failed before it passed.
        var comma = (System.Globalization.CultureInfo)
            System.Globalization.CultureInfo.InvariantCulture.Clone();

        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";

        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = comma;

            // Proof the culture is actually in force, so this cannot quietly become a no-op.
            Assert.Equal("49,8951", 49.8951.ToString("0.####"));

            var wire = new MapCamera(-97.1384, 49.8951, 15, 0).ToWire();

            Assert.Equal("-97.1384,49.8951,15,0", wire);
            Assert.True(MapCamera.TryParse(wire, out var back));
            Assert.Equal(49.8951, back.Latitude, 4);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_camera_survives_the_round_trip_the_page_puts_it_through()
    {
        var sent = new MapCamera(-97.1384, 49.8951, 14.5, 270);

        Assert.True(MapCamera.TryParse(sent.ToWire(), out var back));

        Assert.Equal(sent.Longitude, back.Longitude, 5);
        Assert.Equal(sent.Latitude, back.Latitude, 5);
        Assert.Equal(sent.Zoom, back.Zoom, 2);
        Assert.Equal(sent.Bearing, back.Bearing, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("north,2,3,4")]
    [InlineData("NaN,49,15,0")]
    [InlineData("-97,Infinity,15,0")]
    public void Nothing_malformed_becomes_a_camera(string wire)
    {
        // The failure that matters: a NaN handed to MapLibre is a blank canvas with no error,
        // which on a dash is indistinguishable from a map still loading.
        Assert.False(MapCamera.TryParse(wire, out _));
    }

    [Fact]
    public void A_camera_with_a_nan_in_it_is_never_usable()
    {
        Assert.False(new MapCamera(double.NaN, 49, 15, 0).IsUsable);
        Assert.False(new MapCamera(-97, double.PositiveInfinity, 15, 0).IsUsable);
        Assert.True(new MapCamera(-97, 49, 15, 0).IsUsable);
    }

    [Fact]
    public void Zoom_and_latitude_are_clamped_to_what_the_projection_can_draw()
    {
        var far = new MapCamera(-97, 89, 40, 0).Clamped();

        Assert.Equal(MapCamera.MaxLatitude, far.Latitude, 4);
        Assert.Equal(MapCamera.MaxZoom, far.Zoom);

        var near = new MapCamera(-97, -89, -3, 0).Clamped();

        Assert.Equal(-MapCamera.MaxLatitude, near.Latitude, 4);
        Assert.Equal(MapCamera.MinZoom, near.Zoom);
    }

    [Fact]
    public void Longitude_wraps_rather_than_sticking_at_the_date_line()
    {
        // Dragged east past 180 the map should carry on, not stop against an invisible wall.
        Assert.Equal(-170, new MapCamera(190, 49, 15, 0).Clamped().Longitude, 4);
        Assert.Equal(170, new MapCamera(-190, 49, 15, 0).Clamped().Longitude, 4);
        Assert.Equal(-97.1384, new MapCamera(-97.1384, 49, 15, 0).Clamped().Longitude, 4);
    }

    [Fact]
    public void Bearing_wraps_the_way_a_compass_does()
    {
        // Into 0..360 and never negative — the other direction from longitude, which is why
        // one shared helper for both got bearing wrong.
        Assert.Equal(10, new MapCamera(-97, 49, 15, 370).Clamped().Bearing, 1);
        Assert.Equal(350, new MapCamera(-97, 49, 15, -10).Clamped().Bearing, 1);
        Assert.Equal(0, new MapCamera(-97, 49, 15, 360).Clamped().Bearing, 1);
    }

    // --- The bridge ---

    [Fact]
    public void The_page_can_say_it_is_ready()
    {
        Assert.True(MapMessage.TryParse("map:ready", out var message));
        Assert.Equal(MapSignal.Ready, message.Signal);
    }

    [Fact]
    public void The_page_can_say_it_was_panned_by_hand()
    {
        Assert.True(MapMessage.TryParse("map:free", out var message));
        Assert.Equal(MapSignal.Freed, message.Signal);
    }

    [Fact]
    public void The_page_can_report_where_it_is_looking()
    {
        Assert.True(MapMessage.TryParse("map:camera:-97.1384,49.8951,14.5,0", out var message));

        Assert.Equal(MapSignal.Camera, message.Signal);
        Assert.Equal(49.8951, message.Camera!.Value.Latitude, 4);
        Assert.Equal(14.5, message.Camera!.Value.Zoom, 2);
    }

    [Fact]
    public void A_failure_keeps_its_reason_and_never_arrives_blank()
    {
        Assert.True(MapMessage.TryParse("map:error:Failed to fetch", out var message));
        Assert.Equal(MapSignal.Failed, message.Signal);
        Assert.Equal("Failed to fetch", message.Detail);

        // Something has to be said even when the page had nothing to say.
        Assert.True(MapMessage.TryParse("map:error:", out var bare));
        Assert.False(string.IsNullOrWhiteSpace(bare.Detail));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("drm:widevine")]
    [InlineData("map:")]
    [InlineData("map:something-else")]
    [InlineData("map:camera:rubbish")]
    public void Nothing_else_on_the_channel_is_read_as_a_map_message(string? text)
    {
        // The DRM probe shares this channel, and a camera that will not parse is dropped rather
        // than reported as a zeroed one — which would move the map to the Gulf of Guinea.
        Assert.False(MapMessage.TryParse(text, out _));
    }

    // --- The source ---

    [Fact]
    public void The_tiles_do_not_come_from_openstreetmaps_own_servers()
    {
        // Their usage policy prohibits fetching tiles ahead of being viewed and names offline
        // use and archives, which is the exact shape of this feature. OpenFreeMap serves the
        // same OSM data under terms that allow it.
        Assert.DoesNotContain("openstreetmap.org", MapSource.StyleUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openstreetmap.org", MapSource.TileJsonUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openfreemap", MapSource.StyleUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attribution_names_all_three_the_licences_ask_for()
    {
        Assert.Contains("OpenStreetMap", MapSource.Attribution, StringComparison.Ordinal);
        Assert.Contains("OpenMapTiles", MapSource.Attribution, StringComparison.Ordinal);
        Assert.Contains("OpenFreeMap", MapSource.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public void A_style_override_has_to_be_https_and_carry_no_credentials()
    {
        // It is a URL off the command line going straight into a browser control.
        Assert.True(MapSource.IsAcceptableStyle("https://tiles.openfreemap.org/styles/liberty"));

        Assert.False(MapSource.IsAcceptableStyle("http://tiles.openfreemap.org/styles/liberty"));
        Assert.False(MapSource.IsAcceptableStyle("file:///C:/style.json"));
        Assert.False(MapSource.IsAcceptableStyle("https://user:pass@example.org/style"));
        Assert.False(MapSource.IsAcceptableStyle("not a url"));
        Assert.False(MapSource.IsAcceptableStyle(null));
    }

    [Fact]
    public void An_unusable_override_falls_back_to_the_dark_style_rather_than_failing()
    {
        Assert.Equal(MapSource.StyleUrl, MapSource.StyleOr(null));
        Assert.Equal(MapSource.StyleUrl, MapSource.StyleOr("javascript:alert(1)"));
        Assert.Equal(
            "https://tiles.openfreemap.org/styles/fiord",
            MapSource.StyleOr("  https://tiles.openfreemap.org/styles/fiord  "));
    }

    [Fact]
    public void The_page_is_served_from_a_host_that_never_resolves_on_the_network()
    {
        // A virtual host mapped to a folder in the deploy, not a real domain and not file://,
        // which an ES module and a web worker both refuse.
        Assert.Equal("https://dashdeck.map/index.html", MapSource.PageUrl);
        Assert.DoesNotContain(".", MapSource.PageFolder, StringComparison.Ordinal);
    }
}
