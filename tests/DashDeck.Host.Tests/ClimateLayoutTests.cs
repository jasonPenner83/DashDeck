using System.IO;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>
/// The climate panel as a layout (ADR-0040): its own canvas, the new elements, the built-in
/// Glass panel, and the theme naming its own.
/// </summary>
public sealed class ClimateLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-climate-{Guid.NewGuid():N}");

    public ClimateLayoutTests() => Directory.CreateDirectory(_folder);

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

    private static StageLayout Climate(string elements) =>
        StageLayout.Parse($$"""{ "name": "T", "elements": [ {{elements}} ] }""", null, LayoutOrigin.Yours, LayoutCanvas.Climate);

    [Fact]
    public void The_glass_panel_loads_clean_and_fits_its_canvas()
    {
        var glass = StageLayout.BuiltInClimate;

        Assert.Empty(glass.Problems);
        Assert.Equal("builtin/glass", glass.Id);
        Assert.Same(LayoutCanvas.Climate, glass.Canvas);
        Assert.All(glass.Elements, e => Assert.True(
            e.X >= 0 && e.Y >= 0 && e.X + e.Width <= 912 && e.Y + e.Height <= 390, e.Id));
    }

    /// <summary>Every climate value is a read of a signal: nothing in the panel can send anything.</summary>
    [Fact]
    public void The_glass_panel_reads_the_climate_signals_and_outside_air()
    {
        var signals = StageLayout.BuiltInClimate.Elements
            .Where(e => e.Source.Signal.Length > 0)
            .Select(e => e.Source.Signal)
            .ToHashSet();

        Assert.Contains("hvac.driverSetTemp", signals);
        Assert.Contains("hvac.passengerSetTemp", signals);
        Assert.Contains("hvac.fanSpeed", signals);
        Assert.Contains("hvac.airflow", signals);
        Assert.Contains("seat.driver.climate", signals);
        Assert.Contains("ambient.airTemp", signals);
    }

    [Fact]
    public void Positions_are_checked_against_the_climate_canvas_not_the_stage()
    {
        var layout = Climate("""{ "id": "tall", "type": "glass", "x": 0, "y": 0, "width": 200, "height": 500 }""");

        Assert.Contains(layout.Problems, p => p.Contains("912 × 390 climate panel"));
        Assert.Single(layout.Elements);
    }

    [Fact]
    public void Setpoint_levels_and_indicator_need_a_source_and_levels_a_sane_step_count()
    {
        var layout = Climate("""
            { "id": "s", "type": "setpoint", "x": 0, "y": 0, "width": 200, "height": 180 },
            { "id": "l", "type": "levels", "x": 0, "y": 0, "width": 200, "height": 40, "source": { "signal": "hvac.fanSpeed" }, "parts": { "steps": 40 } },
            { "id": "i", "type": "indicator", "x": 0, "y": 0, "width": 100, "height": 40, "label": "A/C" },
            { "id": "ok", "type": "indicator", "x": 0, "y": 0, "width": 100, "height": 40, "label": "A/C", "source": { "signal": "hvac.airConditioning" }, "parts": { "bit": 0 } }
            """);

        Assert.Equal(["ok"], layout.Elements.Select(e => e.Id));
        Assert.Contains(layout.Problems, p => p.StartsWith("s:") && p.Contains("no source"));
        Assert.Contains(layout.Problems, p => p.StartsWith("l:") && p.Contains("steps must be 1 to 20"));
        Assert.Contains(layout.Problems, p => p.StartsWith("i:") && p.Contains("no source"));
    }

    [Fact]
    public void Glass_takes_its_parts_and_names_a_bad_one()
    {
        var layout = Climate("""
            { "id": "g", "type": "glass", "x": 0, "y": 0, "width": 200, "height": 200, "radius": "28",
              "parts": { "tint": "#DDEEFF", "opacity": 0.1, "sheen": 0.2, "edge": "none", "shadow": 0, "sparkle": 1 } }
            """);

        Assert.Single(layout.Elements);
        Assert.Contains(layout.Problems, p => p.Contains("'sparkle' is not a part of a glass"));
        Assert.DoesNotContain(layout.Problems, p => p.Contains("'tint'") || p.Contains("'edge'"));
    }

    [Fact]
    public void A_climate_library_has_its_own_built_in_and_its_own_examples()
    {
        var library = new StageLayoutLibrary(null, _folder, LayoutCanvas.Climate, StageLayout.ClimateBuiltIns);

        Assert.Same(StageLayout.BuiltInClimate, library.Default);
        Assert.Equal(["builtin/glass"], library.Layouts.Select(l => l.Id));
        Assert.Null(library.WriteExamples());

        var example = StageLayout.Parse(File.ReadAllText(Path.Combine(library.ExamplesFolder, "glass.json")), null, LayoutOrigin.Yours, LayoutCanvas.Climate);
        Assert.Empty(example.Problems);
        Assert.False(File.Exists(Path.Combine(library.ExamplesFolder, "f150-cluster.json")));
    }

    [Fact]
    public void Your_file_in_the_climate_folder_is_read_on_the_climate_canvas()
    {
        File.WriteAllText(Path.Combine(_folder, "mine.json"), """
            { "name": "Mine", "elements": [ { "type": "glass", "x": 0, "y": 0, "width": 912, "height": 390 } ] }
            """);

        var mine = new StageLayoutLibrary(null, _folder, LayoutCanvas.Climate, StageLayout.ClimateBuiltIns).FindForTheme("mine")!;

        Assert.Same(LayoutCanvas.Climate, mine.Canvas);
        Assert.Empty(mine.Problems);
    }

    [Fact]
    public void A_theme_names_its_climate_layout_and_a_missing_one_falls_back_to_glass()
    {
        var theme = ThemeDefinition.Parse("""{ "name": "T", "climateLayout": "nope" }""", null, ThemeOrigin.Yours);
        Assert.Equal("nope", theme.ClimateLayout);
        Assert.Contains("\"climateLayout\": \"nope\"", theme.ToJson(), StringComparison.Ordinal);

        var service = new StageLayoutService(
            new StageLayoutLibrary(null, _folder, LayoutCanvas.Climate, StageLayout.ClimateBuiltIns),
            () => theme.ClimateLayout,
            null);

        Assert.Same(StageLayout.BuiltInClimate, service.Current);
        Assert.Contains("climate panel folders", service.Reason, StringComparison.Ordinal);
        Assert.Contains("Glass", service.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 0, 7, 7, 4, false)]      // fan at 4 of 7
    [InlineData(0, 0, 7, 7, 0, false)]      // fan off
    [InlineData(9, 0, 7, 7, 7, false)]      // past the top is the top, never more
    [InlineData(2, -3, 3, 3, 2, false)]     // seat heat 2
    [InlineData(-1, -3, 3, 3, 1, true)]     // seat cooling 1: the negative colour
    [InlineData(-3, -3, 3, 3, 3, true)]
    [InlineData(double.NaN, 0, 7, 7, 0, false)] // no reading lights nothing
    public void Levels_light_steps_up_to_the_value_and_below_zero_on_the_other_side(
        double value, double min, double max, int steps, int lit, bool negative)
    {
        Assert.Equal((lit, negative), ClimateReadings.Lit(value, min, max, steps));
    }

    [Fact]
    public void An_indicator_is_on_by_bit_by_equals_or_at_onAt()
    {
        var layout = Climate("""
            { "id": "plain", "type": "indicator", "x": 0, "y": 0, "width": 80, "height": 40, "source": { "signal": "hvac.auto" } },
            { "id": "feet", "type": "indicator", "x": 0, "y": 0, "width": 80, "height": 40, "source": { "signal": "hvac.airflow" }, "parts": { "bit": 1 } },
            { "id": "mode", "type": "indicator", "x": 0, "y": 0, "width": 80, "height": 40, "source": { "signal": "hvac.airflow" }, "parts": { "equals": 4 } }
            """);
        var plain = layout.Elements[0];
        var feet = layout.Elements[1];
        var mode = layout.Elements[2];

        Assert.True(ClimateReadings.IsOn(plain, 1));
        Assert.False(ClimateReadings.IsOn(plain, 0));
        Assert.False(ClimateReadings.IsOn(plain, double.NaN));

        Assert.True(ClimateReadings.IsOn(feet, 3));   // face + feet
        Assert.False(ClimateReadings.IsOn(feet, 5));  // face + glass

        Assert.True(ClimateReadings.IsOn(mode, 4));
        Assert.False(ClimateReadings.IsOn(mode, 5));
    }

    private static string ShippedFolder(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", name);

    [Fact]
    public void The_shipped_lcars_climate_panel_reads_cleanly_and_the_lcars_theme_names_it()
    {
        var library = new StageLayoutLibrary(ShippedFolder("climate"), Path.Combine(_folder, "yours"), LayoutCanvas.Climate, StageLayout.ClimateBuiltIns);

        Assert.Empty(library.Problems);
        var lcars = library.FindForTheme("lcars");
        Assert.NotNull(lcars);
        Assert.Empty(lcars!.Problems);
        Assert.Same(LayoutCanvas.Climate, lcars.Canvas);
        Assert.Contains(lcars.Elements, e => e.Type is StageElementType.Setpoint);

        var themes = new ThemeLibrary(ShippedFolder("themes"), Path.Combine(_folder, "themes"));
        Assert.Equal("lcars", themes.Find("shipped/lcars-inspired")!.ClimateLayout);
    }
}
