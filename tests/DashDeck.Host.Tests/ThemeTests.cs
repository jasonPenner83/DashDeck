using System.IO;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>
/// Themes as token files (ADR-0036): reading them, resolving every token day and night, and the
/// library that holds the shipped ones and the user's own.
/// </summary>
public sealed class ThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-themes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string CatalogThemes([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", "themes");

    private static ThemeDefinition Theme(string tokens, string night = "{}") =>
        ThemeDefinition.Parse($$"""{ "name": "Test", "tokens": {{tokens}}, "night": {{night}} }""");

    /// <summary>
    /// A themes folder holding LCARS (with its fonts) — the LCARS extra's (ADR-0043), which these tests
    /// use as a shipped folder because it exercises fonts. Found from this source file, so it works
    /// whatever folder the tests are run from.
    /// </summary>
    private static string ShippedFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "catalog", "extras", "lcars", "themes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "lcars-inspired.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException("catalog/extras/lcars/themes not found from the test binary");
    }

    // ── The built-in look is exactly what shipped before themes ───────────────

    [Fact]
    public void The_built_in_theme_is_modern()
    {
        var day = ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: false);

        Assert.Equal("Modern", ThemeDefinition.BuiltIn.Name);
        Assert.Equal("builtin/modern", ThemeDefinition.BuiltIn.Id);
        Assert.Equal("modern", ThemeDefinition.BuiltIn.StageLayout);
        Assert.Empty(day.Problems);
        Assert.Empty(ThemeResolver.Legibility(day));
        Assert.Equal("#0A0B0D", day.Colour("canvas").ToString());
        Assert.Equal("#F2F4F6", day.Colour("textHigh").ToString());
        Assert.Equal("#5AC8FA", day.Colour("accent").ToString());
        Assert.Equal(day.Colour("textLow"), day.Colour("caption"));
        Assert.Equal(day.Colour("canvas"), day.Colour("navBackground"));
        Assert.Equal(ThemeColour.Transparent, day.Colour("buttonBackground"));
        Assert.Equal(10, day.Number("buttonRadius"));
        Assert.Equal("Segoe UI Variable Text, Segoe UI", day.Fonts["monoFont"]);
    }

    [Fact]
    public void The_built_in_night_is_hand_tuned()
    {
        var night = ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: true);

        Assert.Equal("#050607", night.Colour("canvas").ToString());
        Assert.Equal("#B8BCC1", night.Colour("textHigh").ToString());
        Assert.Equal("#60676E", night.Colour("caption").ToString());
        Assert.Empty(ThemeResolver.Legibility(night));

        // The accent dims by 0.82 at night, as it always has.
        Assert.Equal(new ThemeColour(0x5A, 0xC8, 0xFA).Dim(0.82), night.Colour("accent"));
    }

    /// <summary>The second look (ADR-0043): Glass ships as a file and brings its own layouts.</summary>
    [Fact]
    public void Glass_ships_beside_modern_and_brings_its_layouts()
    {
        var themes = CatalogThemes();
        var library = new ThemeLibrary(themes, Path.Combine(_dir, "yours"));

        Assert.Empty(library.Problems);
        Assert.Equal(["Modern", "Glass"], library.Themes.Select(t => t.Name));

        var glass = library.Find("shipped/glass")!;
        Assert.Equal("glass", glass.ClimateLayout);
        Assert.Equal("glass", glass.ConsoleLayout);
        Assert.Equal("", glass.StageLayout);   // the chrome F-150 cluster

        var day = ThemeResolver.Resolve(glass, night: false);
        Assert.Empty(day.Problems);
        Assert.Empty(ThemeResolver.Legibility(day));
        Assert.Empty(ThemeResolver.Legibility(ThemeResolver.Resolve(glass, night: true)));
    }

    // ── Resolving a theme ─────────────────────────────────────────────────────

    [Fact]
    public void A_token_left_out_falls_through_and_one_that_follows_another_follows_the_theme()
    {
        var day = ThemeResolver.Resolve(Theme("""{ "textLow": "#CC99CC" }"""), night: false);

        Assert.Equal("#CC99CC", day.Colour("caption").ToString());
        Assert.Equal("#0D0C0B", day.Colour("canvas").ToString());
    }

    [Fact]
    public void Night_is_derived_by_dimming_unless_the_theme_says()
    {
        var theme = Theme("""{ "canvas": "#202020", "textHigh": "#C8C8C8" }""", """{ "textHigh": "#909090" }""");
        var night = ThemeResolver.Resolve(theme, night: true);

        Assert.Equal(new ThemeColour(0x20, 0x20, 0x20).Dim(ThemeTokens.Factor(NightRule.Surface)), night.Colour("canvas"));
        Assert.Equal("#909090", night.Colour("textHigh").ToString());
    }

    [Fact]
    public void A_bad_value_costs_that_token_and_says_so_never_the_theme()
    {
        var day = ThemeResolver.Resolve(Theme("""{ "canvas": "black", "buttonRadius": 900, "glow": "#FFFFFF", "textHigh": "#FFFFFF" }"""), night: false);

        Assert.Equal("#0D0C0B", day.Colour("canvas").ToString());
        Assert.Equal(14, day.Number("buttonRadius"));
        Assert.Equal("#FFFFFF", day.Colour("textHigh").ToString());
        Assert.Equal(3, day.Problems.Count);
        Assert.Contains(day.Problems, p => p.Contains("'glow'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#FF7722", 0xFF, 0x77, 0x22, 0xFF)]
    [InlineData("#f72", 0xFF, 0x77, 0x22, 0xFF)]
    [InlineData("#80FF7722", 0xFF, 0x77, 0x22, 0x80)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void Reads_colours_the_ways_people_write_them(string text, int r, int g, int b, int a)
    {
        Assert.True(ThemeColour.TryParse(text, out var colour));
        Assert.Equal(new ThemeColour((byte)r, (byte)g, (byte)b, (byte)a), colour);
    }

    [Fact]
    public void A_theme_survives_being_written_and_read_back()
    {
        var theme = Theme("""{ "canvas": "#000000", "buttonRadius": 28, "uiFont": "Antonio" }""", """{ "canvas": "#010101" }""")
            with { Description = "Black — with a dash", FontFiles = ["Antonio-Regular.ttf"] };

        var json = theme.ToJson();
        var again = ThemeDefinition.Parse(json);

        Assert.Contains("\"buttonRadius\": 28", json, StringComparison.Ordinal);
        Assert.Contains("—", json, StringComparison.Ordinal);
        Assert.Equal(theme.Tokens, again.Tokens);
        Assert.Equal(theme.Night, again.Night);
        Assert.Equal(theme.FontFiles, again.FontFiles);
        Assert.Equal(theme.Description, again.Description);
    }

    [Fact]
    public void A_file_without_a_name_is_not_a_theme()
    {
        Assert.Throws<InvalidDataException>(() => ThemeDefinition.Parse("""{ "tokens": {} }"""));
        Assert.Throws<InvalidDataException>(() => ThemeDefinition.Parse("not json"));
    }

    [Fact]
    public void Legibility_warns_about_text_that_disappears()
    {
        var murky = ThemeResolver.Resolve(Theme("""{ "buttonBackground": "#333333", "buttonText": "#3A3A3A" }"""), night: false);
        Assert.Contains(ThemeResolver.Legibility(murky), w => w.StartsWith("Button text", StringComparison.Ordinal));

        Assert.Empty(ThemeResolver.Legibility(ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: false)));
    }

    // ── The shipped LCARS theme ───────────────────────────────────────────────

    [Fact]
    public void The_shipped_lcars_theme_reads_cleanly_and_is_legible()
    {
        var library = new ThemeLibrary(ShippedFolder(), Path.Combine(_dir, "yours"));
        var lcars = library.Themes.Single(t => t.Name == "LCARS (inspired)");

        Assert.Empty(library.Problems);
        Assert.Equal("shipped/lcars-inspired", lcars.Id);

        var day = ThemeResolver.Resolve(lcars, night: false);
        Assert.Empty(day.Problems);
        Assert.Empty(ThemeResolver.Legibility(day));
        Assert.Equal(1, day.Number("accentWash"));
        Assert.All(lcars.FontFiles, f => Assert.True(File.Exists(Path.Combine(lcars.Folder!, f)), f));
    }

    // ── The library ───────────────────────────────────────────────────────────

    [Fact]
    public void The_built_in_look_is_always_first_even_with_no_folders()
    {
        var library = new ThemeLibrary(null, Path.Combine(_dir, "nowhere"));

        Assert.Equal("builtin/modern", Assert.Single(library.Themes).Id);
    }

    [Fact]
    public void A_bad_file_is_left_out_and_reported()
    {
        var yours = Path.Combine(_dir, "yours");
        Directory.CreateDirectory(yours);
        File.WriteAllText(Path.Combine(yours, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(yours, "fine.json"), """{ "name": "Fine" }""");

        var library = new ThemeLibrary(null, yours);

        Assert.Equal(["Modern", "Fine"], library.Themes.Select(t => t.Name));
        Assert.Equal("broken.json", Assert.Single(library.Problems).File);
    }

    [Fact]
    public void Import_brings_the_fonts_it_names_and_export_takes_them_away_again()
    {
        var source = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "Face.ttf"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(source, "shared.json"),
            """{ "name": "Shared Look", "fontFiles": ["Face.ttf"], "tokens": { "uiFont": "Face" } }""");

        var library = new ThemeLibrary(null, Path.Combine(_dir, "yours"));
        var imported = library.Import(Path.Combine(source, "shared.json"));

        Assert.Equal(ThemeOrigin.Yours, imported.Origin);
        Assert.Equal("yours/shared-look", imported.Id);
        Assert.True(File.Exists(Path.Combine(library.UserFolder, "Face.ttf")));

        var exported = library.Export(imported, Path.Combine(_dir, "usb"));
        Assert.True(File.Exists(exported));
        Assert.True(File.Exists(Path.Combine(_dir, "usb", "Face.ttf")));
    }

    [Theory]
    [InlineData("..\\..\\secrets.ttf")]
    [InlineData("../escape.ttf")]
    [InlineData("notes.txt")]
    [InlineData("Missing.ttf")]
    public void Import_refuses_a_font_that_is_not_a_font_beside_the_theme(string fontFile)
    {
        var source = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "hi");
        File.WriteAllText(Path.Combine(source, "sly.json"),
            $$"""{ "name": "Sly", "fontFiles": ["{{fontFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}"] }""");

        var library = new ThemeLibrary(null, Path.Combine(_dir, "yours"));

        Assert.Throws<InvalidDataException>(() => library.Import(Path.Combine(source, "sly.json")));
        Assert.False(Directory.Exists(library.UserFolder) && Directory.EnumerateFiles(library.UserFolder).Any());
    }

    [Fact]
    public void Save_as_makes_an_editable_copy_and_only_your_own_can_be_deleted()
    {
        var library = new ThemeLibrary(ShippedFolder(), Path.Combine(_dir, "yours"));
        var lcars = library.Themes.Single(t => t.Origin is ThemeOrigin.Shipped && t.Name.StartsWith("LCARS", StringComparison.Ordinal));

        var mine = library.SaveAs(lcars, "My LCARS");
        var again = library.SaveAs(lcars, "My LCARS");

        Assert.Equal("yours/my-lcars", mine.Id);
        Assert.Equal("yours/my-lcars-2", again.Id);
        Assert.Equal(lcars.Tokens, mine.Tokens);
        Assert.True(File.Exists(Path.Combine(library.UserFolder, "Antonio-Regular.ttf")));

        Assert.False(library.Delete(lcars));
        Assert.True(library.Delete(mine));
        Assert.Null(library.Find("yours/my-lcars"));
    }

    [Theory]
    [InlineData("LCARS (inspired)", "lcars-inspired")]
    [InlineData("  Night Owl  ", "night-owl")]
    [InlineData("???", "theme")]
    public void A_theme_name_becomes_a_file_name(string name, string slug) =>
        Assert.Equal(slug, ThemeLibrary.Slug(name));

    [Fact]
    public void The_shipped_themes_are_written_out_as_examples_that_are_not_loaded()
    {
        var library = new ThemeLibrary(ShippedFolder(), Path.Combine(_dir, "yours"));

        Assert.Null(library.WriteExamples());

        var lcars = Path.Combine(library.ExamplesFolder, "lcars-inspired.json");
        Assert.Equal(File.ReadAllText(Path.Combine(ShippedFolder(), "lcars-inspired.json")), File.ReadAllText(lcars));
        Assert.True(File.Exists(Path.Combine(library.ExamplesFolder, "Antonio-Regular.ttf")));
        Assert.True(File.Exists(Path.Combine(library.ExamplesFolder, "OFL-Antonio.txt")));
        Assert.True(File.Exists(Path.Combine(library.ExamplesFolder, "README.txt")));

        // The written-out Modern theme sets every token, and copied up a folder it is the same look.
        var every = ThemeDefinition.Parse(File.ReadAllText(Path.Combine(library.ExamplesFolder, "modern.json")));
        Assert.Equal(ThemeTokens.All.Count, every.Tokens.Count);
        Assert.Equal(ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: false).Colours, ThemeResolver.Resolve(every, night: false).Colours);
        Assert.Equal(ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: true).Colours, ThemeResolver.Resolve(every, night: true).Colours);

        // The LCARS example, copied up a folder with its fonts, imports cleanly.
        Assert.Equal("yours/lcars-inspired", library.Import(lcars).Id);

        // Examples themselves are references, not themes.
        library.Reload();
        Assert.Single(library.Themes, t => t.Origin is ThemeOrigin.Yours);

        File.WriteAllText(lcars, "{ edited }");
        library.WriteExamples();
        Assert.NotEqual("{ edited }", File.ReadAllText(lcars));
    }
}
