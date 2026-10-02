using System.IO;
using DashDeck.Abstractions;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Tests;

/// <summary>
/// The compass as stage layout elements (ADR-0039): sensor sources, the compass and G meter
/// elements, the built-in compass layout, and the arithmetic moved from the old view model.
/// </summary>
public sealed class CompassLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-compass-{Guid.NewGuid():N}");

    public CompassLayoutTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static StageLayout Parse(string elements) =>
        StageLayout.Parse($$"""{ "name": "T", "elements": [ {{elements}} ] }""");

    // ── The built-in compass layout ───────────────────────────────────────────

    /// <summary>Everything the old screen showed is in it, and none of it is left out.</summary>
    [Fact]
    public void The_built_in_compass_layout_has_what_the_old_screen_had()
    {
        var layout = StageLayout.BuiltInCompass;

        Assert.Empty(layout.Problems);
        Assert.Equal("builtin/compass", layout.Id);
        Assert.Equal(
            ["heading", "latitude", "longitude", "g", "pitch", "roll", "speed", "outside"],
            layout.Elements.Select(e => e.Id));
        Assert.Equal("attitude.heading", layout.Elements[0].Source.Sensor);
        Assert.True(layout.HasGMeter);
        Assert.True(layout.UsesSensors);
    }

    [Fact]
    public void The_built_in_compass_fits_the_stage()
    {
        foreach (var e in StageLayout.BuiltInCompass.Elements)
        {
            Assert.True(e.X >= 0 && e.Y >= 0 && e.X + e.Width <= StageLayout.StageWidth && e.Y + e.Height <= StageLayout.StageHeight, e.Id);
        }
    }

    [Fact]
    public void The_cluster_is_still_the_default_and_uses_no_sensors()
    {
        Assert.Equal("builtin/default", StageLayout.BuiltIn.Id);
        Assert.False(StageLayout.BuiltIn.UsesSensors);
        Assert.False(StageLayout.BuiltIn.HasGMeter);
    }

    [Fact]
    public void The_library_offers_both_built_ins_and_yours_wins_the_compass_name()
    {
        var library = new StageLayoutLibrary(null, _folder);
        Assert.Same(StageLayout.BuiltInCompass, library.FindForTheme("compass"));

        File.WriteAllText(Path.Combine(_folder, "compass.json"), """
            { "name": "My compass", "elements": [ { "type": "compass", "x": 0, "y": 0, "width": 400, "height": 420 } ] }
            """);
        library.Reload();

        Assert.Contains(library.Layouts, l => l.Id == "builtin/compass");
        Assert.Equal("yours/compass", library.FindForTheme("compass")!.Id);
    }

    [Fact]
    public void A_pinned_compass_service_shows_the_compass_layout()
    {
        var service = StageLayoutService.Pinned(new StageLayoutLibrary(null, _folder), StageLayout.CompassSlug);

        Assert.Same(StageLayout.BuiltInCompass, service.Current);
        Assert.Equal("Pinned by the launcher.", service.Reason);
    }

    [Fact]
    public void The_examples_include_the_compass_and_it_reads_back_clean()
    {
        var library = new StageLayoutLibrary(null, _folder);

        Assert.Null(library.WriteExamples());

        var example = StageLayout.Parse(File.ReadAllText(Path.Combine(library.ExamplesFolder, "compass.json")));
        Assert.Empty(example.Problems);
        Assert.Equal(StageLayout.BuiltInCompass.Elements.Count, example.Elements.Count);
        Assert.Equal(StageElementType.GMeter, example.Elements.Single(e => e.Id == "g").Type);
    }

    // ── The elements ──────────────────────────────────────────────────────────

    [Fact]
    public void A_compass_reads_the_heading_unless_it_names_another_sensor()
    {
        var layout = Parse("""
            { "type": "compass", "x": 0, "y": 0, "width": 300, "height": 320 },
            { "type": "compass", "x": 0, "y": 0, "width": 300, "height": 320, "source": { "sensor": "location.groundSpeed" } }
            """);

        Assert.Empty(layout.Problems);
        Assert.Equal("attitude.heading", layout.Elements[0].Source.Sensor);
        Assert.Equal("location.groundSpeed", layout.Elements[1].Source.Sensor);
    }

    [Fact]
    public void A_compass_cannot_read_a_signal()
    {
        var layout = Parse("""{ "id": "c", "type": "compass", "x": 0, "y": 0, "width": 300, "height": 320, "source": { "signal": "vehicle.speed" } }""");

        Assert.Empty(layout.Elements);
        Assert.Contains(layout.Problems, p => p.StartsWith("c:") && p.Contains("sensor"));
    }

    [Fact]
    public void A_g_meter_with_a_silly_range_is_left_out()
    {
        var layout = Parse("""
            { "id": "a", "type": "gMeter", "x": 0, "y": 0, "width": 240, "height": 300, "parts": { "range": 0 } },
            { "id": "b", "type": "gMeter", "x": 0, "y": 0, "width": 240, "height": 300, "parts": { "range": 0.5 } }
            """);

        Assert.Equal(["b"], layout.Elements.Select(e => e.Id));
        Assert.Contains(layout.Problems, p => p.StartsWith("a:") && p.Contains("range"));
    }

    [Fact]
    public void Unknown_compass_and_g_meter_parts_are_named_and_bad_colours_warned()
    {
        var layout = Parse("""
            { "id": "c", "type": "compass", "x": 0, "y": 0, "width": 300, "height": 320, "parts": { "mode": "needle", "sparkle": true, "ringColour": "teal" } },
            { "id": "g", "type": "gMeter", "x": 0, "y": 0, "width": 240, "height": 300, "parts": { "ballSize": 20, "needleWidth": 4 } }
            """);

        Assert.Equal(2, layout.Elements.Count);
        Assert.Contains(layout.Problems, p => p.Contains("'sparkle' is not a part of a compass"));
        Assert.Contains(layout.Problems, p => p.Contains("'ringColour': 'teal' is not a colour"));
        Assert.Contains(layout.Problems, p => p.Contains("'needleWidth' is not a part of a gMeter"));
        Assert.DoesNotContain(layout.Problems, p => p.Contains("'mode'") || p.Contains("'ballSize'"));
    }

    // ── Sensor sources on gauges ──────────────────────────────────────────────

    [Fact]
    public void Any_gauge_style_can_read_a_sensor()
    {
        var layout = Parse("""
            { "id": "pitch", "style": "arc", "x": 0, "y": 0, "width": 200, "height": 200, "min": -30, "max": 30, "source": { "sensor": "attitude.pitch" } },
            { "id": "roll", "style": "lcarsBar", "x": 0, "y": 0, "width": 400, "height": 60, "min": -30, "max": 30, "source": { "sensor": "attitude.roll" }, "parts": { "showSource": false } }
            """);

        Assert.Empty(layout.Problems);
        Assert.All(layout.Elements, e => Assert.True(e.Source.IsSensor));
        Assert.True(layout.UsesSensors);
        Assert.False(layout.HasGMeter);
    }

    [Fact]
    public void A_source_is_a_signal_or_a_sensor_and_a_sensor_cannot_subtract()
    {
        var layout = Parse("""
            { "id": "both", "style": "digital", "x": 0, "y": 0, "width": 100, "height": 100, "source": { "signal": "vehicle.speed", "sensor": "attitude.pitch" } },
            { "id": "minus", "style": "digital", "x": 0, "y": 0, "width": 100, "height": 100, "source": { "sensor": "attitude.pitch", "minus": "attitude.roll" } },
            { "id": "none", "style": "digital", "x": 0, "y": 0, "width": 100, "height": 100 }
            """);

        Assert.Empty(layout.Elements);
        Assert.Contains(layout.Problems, p => p.StartsWith("both:") && p.Contains("not both"));
        Assert.Contains(layout.Problems, p => p.StartsWith("minus:") && p.Contains("minus works with signals only"));
        Assert.Contains(layout.Problems, p => p.StartsWith("none:") && p.Contains("source.sensor"));
    }

    [Fact]
    public void A_sensor_reading_is_scaled_like_a_signal_and_nothing_usable_is_no_reading()
    {
        var source = new GaugeSource { Sensor = "motion.lateralG", Scale = 9.80665 };

        var live = GaugeReading.FromSensor(source, 0.5, SignalQuality.Live);
        Assert.True(live.HasValue);
        Assert.Equal(4.903325, live.Value, 6);

        Assert.False(GaugeReading.FromSensor(source, double.NaN, SignalQuality.Live).HasValue);
        Assert.False(GaugeReading.FromSensor(source, 0.5, SignalQuality.Unavailable).HasValue);
        Assert.False(GaugeReading.FromSensor(source, 0.5, SignalQuality.Stale).HasValue);
        Assert.Equal(SignalQuality.Simulated, GaugeReading.FromSensor(source, 0.1, SignalQuality.Simulated).Quality);
    }

    // ── The arithmetic ────────────────────────────────────────────────────────

    /// <summary>Averaging degrees as numbers puts 350° and 10° at 180°. As vectors, it stays at north.</summary>
    [Fact]
    public void Smoothing_a_heading_across_north_does_not_swing_through_south()
    {
        var eased = SensorMath.SmoothAngle(350, 10, factor: 0.5);

        Assert.True(eased is > 355 or < 5, $"eased to {eased}");
    }

    [Fact]
    public void The_first_heading_is_taken_as_it_is() =>
        Assert.Equal(123, SensorMath.SmoothAngle(double.NaN, 123));

    /// <summary>Braking throws the ball forward (up); a right-hand bend throws it left.</summary>
    [Fact]
    public void The_ball_moves_the_way_the_driver_is_pushed()
    {
        var (bx, by) = SensorMath.Ball(lateral: 0, longitudinal: -0.5, range: 1, radius: 100);
        Assert.Equal(0, bx, 6);
        Assert.Equal(-50, by, 6);

        var (rx, ry) = SensorMath.Ball(lateral: 0.25, longitudinal: 0, range: 1, radius: 100);
        Assert.Equal(-25, rx, 6);
        Assert.Equal(0, ry, 6);
    }

    [Fact]
    public void A_reading_past_the_range_parks_on_the_outer_ring()
    {
        var (x, y) = SensorMath.Ball(lateral: 3, longitudinal: 4, range: 1, radius: 100);

        Assert.Equal(100, Math.Sqrt((x * x) + (y * y)), 6);
    }

    [Fact]
    public void Total_g_is_the_length_of_the_two() =>
        Assert.Equal(0.5, SensorMath.Total(0.3, 0.4), 6);
}
