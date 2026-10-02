using System.IO;
using DashDeck.Abstractions;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Tests;

/// <summary>The stage as a file (ADR-0037): gauges, text, clock and panels, read, checked and computed.</summary>
public sealed class StageLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-stage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static StageLayout Layout(string elements) =>
        StageLayout.Parse($$"""{ "name": "Test", "elements": [ {{elements}} ] }""");

    private static readonly DateTimeOffset At = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    private static SignalValue Live(double v) => new("x", v, "", At, SignalQuality.Live);

    // ── The built-in cluster ──────────────────────────────────────────────────

    [Fact]
    public void The_built_in_layout_is_the_old_cluster_and_reads_cleanly()
    {
        var layout = StageLayout.BuiltIn;

        Assert.Empty(layout.Problems);
        Assert.Equal("builtin/default", layout.Id);
        Assert.Equal(["boost", "oil", "volts", "intake", "throttle", "load"], layout.Gauges.Select(g => g.Id));
        Assert.All(layout.Elements, e => Assert.True(e.X + e.Width <= StageLayout.StageWidth && e.Y + e.Height <= StageLayout.StageHeight, e.Id));

        var boost = layout.Gauges.First();
        Assert.Equal("engine.barometricPressure", boost.Source.Minus);
        Assert.Single(boost.Zones);
        Assert.False(layout.Gauges.Last().Flag("numerals", true));
    }

    [Fact]
    public void A_layout_survives_being_written_and_read_back()
    {
        var again = StageLayout.Parse(StageLayout.BuiltIn.ToJson());

        Assert.Empty(again.Problems);
        Assert.Equal(StageLayout.BuiltIn.Elements.Count, again.Elements.Count);
        Assert.Equal(StageLayout.BuiltIn.Gauges.First().Source, again.Gauges.First().Source);
        Assert.Contains("\"style\": \"dial\"", StageLayout.BuiltIn.ToJson(), StringComparison.Ordinal);
    }

    // ── Elements other than gauges ────────────────────────────────────────────

    [Fact]
    public void Text_clock_and_panels_are_elements_too()
    {
        var layout = Layout("""
            { "type": "panel", "x": 0, "y": 0, "width": 200, "height": 120, "colour": "@accent", "radius": "60,0,0,0" },
            { "type": "text", "x": 20, "y": 10, "width": 300, "height": 40, "content": "ENGINE", "colour": "#FF9966" },
            { "type": "clock", "x": 600, "y": 10, "width": 200, "height": 60, "content": "HH:mm:ss" }
            """);

        Assert.Empty(layout.Problems);
        Assert.Equal([StageElementType.Panel, StageElementType.Text, StageElementType.Clock], layout.Elements.Select(e => e.Type));
        Assert.Empty(layout.Gauges);
    }

    [Theory]
    [InlineData("12", new[] { 12.0, 12, 12, 12 })]
    [InlineData("40, 0, 0, 40", new[] { 40.0, 0, 0, 40 })]
    public void Radius_is_one_number_or_four(string text, double[] expected) =>
        Assert.Equal(expected, StageLayout.ParseRadius(text));

    [Theory]
    [InlineData("1,2")]
    [InlineData("-3")]
    [InlineData("big")]
    public void A_bad_radius_is_refused(string text) => Assert.Null(StageLayout.ParseRadius(text));

    // ── What is left out, and what is only warned about ───────────────────────

    [Fact]
    public void An_element_that_cannot_be_drawn_honestly_is_left_out_and_named()
    {
        var layout = Layout("""
            { "id": "nosource", "x": 0, "y": 0, "width": 100, "height": 100, "min": 0, "max": 10 },
            { "id": "backwards", "x": 0, "y": 0, "width": 100, "height": 100, "source": { "signal": "vehicle.speed" }, "min": 10, "max": 0 },
            { "id": "greedy", "x": 0, "y": 0, "width": 100, "height": 100, "source": { "signal": "vehicle.speed", "rateHz": 50 } },
            { "id": "empty", "type": "text", "x": 0, "y": 0, "width": 100, "height": 40 },
            { "id": "fine", "x": 0, "y": 0, "width": 100, "height": 100, "source": { "signal": "vehicle.speed" } }
            """);

        Assert.Equal(["fine"], layout.Elements.Select(e => e.Id));
        Assert.Equal(4, layout.Problems.Count);
        Assert.All(layout.Problems, p => Assert.EndsWith("left out", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Off_the_stage_unknown_parts_and_bad_colours_are_warnings_not_refusals()
    {
        var layout = Layout("""
            { "id": "wide", "style": "bar", "x": 800, "y": 0, "width": 300, "height": 60,
              "source": { "signal": "vehicle.speed" },
              "zones": [ { "from": 100, "to": 120, "colour": "redish" } ],
              "parts": { "needleWidth": 4, "fillColour": "orange" } }
            """);

        Assert.Single(layout.Elements);
        Assert.Contains(layout.Problems, p => p.Contains("outside", StringComparison.Ordinal));
        Assert.Contains(layout.Problems, p => p.Contains("'needleWidth' is not a part of a bar", StringComparison.Ordinal));
        Assert.Contains(layout.Problems, p => p.Contains("'fillColour'", StringComparison.Ordinal));
        Assert.Contains(layout.Problems, p => p.Contains("zone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_with_no_name_is_not_a_layout()
    {
        Assert.Throws<InvalidDataException>(() => StageLayout.Parse("""{ "elements": [] }"""));
        Assert.Throws<InvalidDataException>(() => StageLayout.Parse("nope"));
    }

    // ── Readings: never a confident zero ──────────────────────────────────────

    private static readonly GaugeSource Boost = new()
    {
        Signal = "engine.intakeManifoldPressure",
        Minus = "engine.barometricPressure",
        MinusFallback = 101.325,
        Scale = 0.1450377,
    };

    [Fact]
    public void Boost_is_manifold_minus_barometric_in_psi()
    {
        var reading = GaugeReading.Compute(Boost, Live(201.325), Live(101.325));

        Assert.Equal(SignalQuality.Live, reading.Quality);
        Assert.Equal(14.50377, reading.Value, 4);
    }

    [Fact]
    public void A_missing_barometer_uses_its_fallback_but_a_missing_manifold_draws_nothing()
    {
        Assert.Equal(14.50377, GaugeReading.Compute(Boost, Live(201.325), SignalValue.Missing("b")).Value, 4);

        var none = GaugeReading.Compute(Boost, SignalValue.Missing("m"), Live(100));
        Assert.False(none.HasValue);
        Assert.Equal(SignalQuality.Unavailable, none.Quality);
    }

    [Fact]
    public void A_signal_the_truck_does_not_answer_draws_no_needle_not_zero()
    {
        var oil = GaugeReading.Compute(new GaugeSource { Signal = "engine.oilTemp" }, SignalValue.Missing("engine.oilTemp", "°C"), null);

        Assert.False(oil.HasValue);
    }

    [Fact]
    public void Stale_still_draws_and_the_least_trusted_input_sets_the_quality()
    {
        var stale = GaugeReading.Compute(Boost, Live(150).AsStale(), Live(100));
        Assert.True(stale.HasValue);
        Assert.Equal(SignalQuality.Stale, stale.Quality);

        var simulated = GaugeReading.Compute(Boost, Live(150), Live(100) with { Quality = SignalQuality.Simulated });
        Assert.Equal(SignalQuality.Simulated, simulated.Quality);
    }

    // ── The library ───────────────────────────────────────────────────────────

    [Fact]
    public void A_theme_names_a_layout_and_your_copy_beats_the_shipped_one()
    {
        var shipped = Path.Combine(_dir, "shipped");
        Directory.CreateDirectory(shipped);
        File.WriteAllText(Path.Combine(shipped, "lcars.json"), """{ "name": "LCARS stage" }""");

        var library = new StageLayoutLibrary(shipped, Path.Combine(_dir, "yours"));
        Assert.Equal("shipped/lcars", library.FindForTheme("lcars")!.Id);

        var mine = library.SaveAs(library.Find("shipped/lcars")!, "lcars");
        Assert.Equal("yours/lcars", mine.Id);
        Assert.Equal("yours/lcars", library.FindForTheme("lcars")!.Id);
        Assert.Equal("shipped/lcars", library.FindForTheme("shipped/lcars")!.Id);

        Assert.False(library.Delete(library.Find("shipped/lcars")!));
        Assert.True(library.Delete(mine));
        Assert.Equal("shipped/lcars", library.FindForTheme("lcars")!.Id);
        Assert.Null(library.FindForTheme("nothing"));
    }

    [Fact]
    public void A_bad_file_is_left_out_and_the_built_in_cluster_is_always_there()
    {
        var yours = Path.Combine(_dir, "yours");
        Directory.CreateDirectory(yours);
        File.WriteAllText(Path.Combine(yours, "broken.json"), "{");

        var library = new StageLayoutLibrary(null, yours);

        Assert.Equal("builtin/default", Assert.Single(library.Layouts).Id);
        Assert.Equal("broken.json", Assert.Single(library.Problems).File);
    }

    // ── The shipped LCARS stage ───────────────────────────────────────────────

    private static string ShippedFolder(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", name);

    [Fact]
    public void The_shipped_lcars_stage_reads_cleanly_and_the_lcars_theme_names_it()
    {
        var library = new StageLayoutLibrary(ShippedFolder("stage"), Path.Combine(_dir, "yours"));

        Assert.Empty(library.Problems);
        var lcars = library.FindForTheme("lcars");
        Assert.NotNull(lcars);
        Assert.Empty(lcars!.Problems);
        Assert.Equal(6, lcars.Gauges.Count());
        Assert.All(lcars.Gauges, g => Assert.Equal(GaugeStyle.LcarsBar, g.Style));

        var themes = new DashDeck.Host.Theme.ThemeLibrary(ShippedFolder("themes"), Path.Combine(_dir, "themes"));
        Assert.Equal("lcars", themes.Find("shipped/lcars-inspired")!.StageLayout);
    }

    [Fact]
    public void The_stage_follows_the_theme_until_one_is_chosen()
    {
        var library = new StageLayoutLibrary(ShippedFolder("stage"), Path.Combine(_dir, "yours"));
        var themeLayout = "lcars";
        string? saved = null;
        var service = new StageLayoutService(library, () => themeLayout, StageLayoutService.FollowTheme, c => saved = c);
        var changes = 0;
        service.LayoutChanged += (_, _) => changes++;

        Assert.Equal("shipped/lcars", service.Current.Id);

        themeLayout = "";
        service.ThemeChanged();
        Assert.Equal("builtin/default", service.Current.Id);

        service.Choose("shipped/lcars");
        Assert.Equal("shipped/lcars", saved);
        themeLayout = "";
        service.ThemeChanged();
        Assert.Equal("shipped/lcars", service.Current.Id);

        themeLayout = "missing";
        service.Choose(StageLayoutService.FollowTheme);
        Assert.Equal("builtin/default", service.Current.Id);
        Assert.Contains("missing", service.Reason, StringComparison.Ordinal);
        Assert.True(changes >= 3);
    }
}
