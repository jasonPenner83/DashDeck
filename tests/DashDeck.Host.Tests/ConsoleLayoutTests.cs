using System.IO;
using DashDeck.Core.Catalog;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>
/// The console that DASH shows (ADR-0041): its canvas, the built-in Modern layout, the LCARS one,
/// and the warning-light element.
/// </summary>
public sealed class ConsoleLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-console-{Guid.NewGuid():N}");

    public ConsoleLayoutTests() => Directory.CreateDirectory(_folder);

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

    private static string Shipped(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", name);

    /// <summary>The catalog the console reads from: the standard set.</summary>
    private static SignalCatalog Catalog() =>
        SignalCatalog.FromFile(Shipped("signals.obd2-standard.json"));

    private static StageLayout Console(string elements) =>
        StageLayout.Parse($$"""{ "name": "T", "elements": [ {{elements}} ] }""", null, LayoutOrigin.Yours, LayoutCanvas.Console);

    [Fact]
    public void Modern_loads_clean_and_fits_the_console()
    {
        var modern = StageLayout.BuiltInConsole;

        Assert.Empty(modern.Problems);
        Assert.Equal("builtin/modern", modern.Id);
        Assert.Same(LayoutCanvas.Console, modern.Canvas);
        Assert.All(modern.Elements, e => Assert.True(
            e.X >= 0 && e.Y >= 0 && e.X + e.Width <= 912 && e.Y + e.Height <= 390, e.Id));
    }

    [Fact]
    public void Every_signal_the_shipped_consoles_read_is_in_the_catalog()
    {
        var catalog = Catalog();
        var library = new StageLayoutLibrary(Shipped("console"), Path.Combine(_folder, "yours"), LayoutCanvas.Console, StageLayout.ConsoleBuiltIns);

        Assert.Empty(library.Problems);
        foreach (var layout in library.Layouts)
        {
            Assert.Empty(layout.Problems);
            foreach (var element in layout.Elements.Where(e => e.Source.Signal.Length > 0))
            {
                Assert.True(catalog.TryGet(element.Source.Signal, out _), $"{layout.Name}: {element.Id} reads {element.Source.Signal}");
            }
        }
    }

    [Fact]
    public void Modern_shows_speed_economy_range_odometer_and_the_warning_lights()
    {
        var signals = StageLayout.BuiltInConsole.Elements.Select(e => e.Source.Signal).ToHashSet();

        Assert.Contains("vehicle.speed", signals);
        Assert.Contains("vehicle.odometer", signals);
        Assert.Contains("fuel.economy", signals);
        Assert.Contains("fuel.range", signals);
        Assert.Contains("diagnostics.checkEngine", signals);
        Assert.Equal(9, StageLayout.BuiltInConsole.Elements.Count(e => e.Type is StageElementType.Warning));
    }

    [Fact]
    public void The_lcars_theme_brings_its_console()
    {
        var library = new StageLayoutLibrary(Shipped("console"), Path.Combine(_folder, "yours"), LayoutCanvas.Console, StageLayout.ConsoleBuiltIns);
        var themes = new ThemeLibrary(Shipped("themes"), Path.Combine(_folder, "themes"));

        Assert.Equal("lcars", themes.Find("shipped/lcars-inspired")!.ConsoleLayout);
        Assert.NotNull(library.FindForTheme("lcars"));
        Assert.Equal(StageLayout.BuiltInConsole, library.Default);
    }

    [Fact]
    public void A_warning_needs_an_icon_it_knows_or_path_data()
    {
        var layout = Console("""
            { "id": "known", "type": "warning", "x": 0, "y": 0, "width": 30, "height": 30, "source": { "signal": "diagnostics.checkEngine" }, "parts": { "icon": "OIL" } },
            { "id": "drawn", "type": "warning", "x": 0, "y": 0, "width": 30, "height": 30, "source": { "signal": "diagnostics.checkEngine" }, "parts": { "icon": "M4 4h16v16H4z" } },
            { "id": "nope", "type": "warning", "x": 0, "y": 0, "width": 30, "height": 30, "source": { "signal": "diagnostics.checkEngine" }, "parts": { "icon": "bananas" } },
            { "id": "blank", "type": "warning", "x": 0, "y": 0, "width": 30, "height": 30, "parts": { "icon": "oil" } }
            """);

        Assert.Equal(["known", "drawn"], layout.Elements.Select(e => e.Id));
        Assert.Contains(layout.Problems, p => p.StartsWith("nope:") && p.Contains("checkEngine, oil"));
        Assert.Contains(layout.Problems, p => p.StartsWith("blank:") && p.Contains("no source"));
    }

    [Fact]
    public void Every_built_in_icon_is_path_data()
    {
        foreach (var name in WarningIcons.Names)
        {
            var icon = WarningIcons.Find(name);
            Assert.NotNull(icon);
            Assert.StartsWith("M", icon!.Path);
        }
    }

    [Theory]
    [InlineData(8.0, true)]     // low fuel: below 12 %
    [InlineData(12.0, false)]
    [InlineData(double.NaN, false)]
    public void Below_lights_a_warning_under_a_value(double level, bool lit)
    {
        var layout = Console("""
            { "id": "f", "type": "warning", "x": 0, "y": 0, "width": 30, "height": 30, "source": { "signal": "fuel.levelPercent" }, "parts": { "icon": "fuel", "below": 12 } }
            """);

        Assert.Equal(lit, ClimateReadings.IsOn(layout.Elements[0], level));
    }

    [Fact]
    public void The_console_library_writes_modern_as_an_example()
    {
        var library = new StageLayoutLibrary(null, Path.Combine(_folder, "console"), LayoutCanvas.Console, StageLayout.ConsoleBuiltIns);

        Assert.Null(library.WriteExamples());
        var example = File.ReadAllText(Path.Combine(library.ExamplesFolder, "modern.json"));
        Assert.Empty(StageLayout.Parse(example, null, LayoutOrigin.Yours, LayoutCanvas.Console).Problems);
    }
}
