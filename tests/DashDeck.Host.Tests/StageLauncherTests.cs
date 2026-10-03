using System.IO;
using DashDeck.Host.Stage.Launcher;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// The launcher file (ADR-0038): every stage option, in order, and the bar below the stage.
/// </summary>
public sealed class StageLauncherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-launcher-{Guid.NewGuid():N}");

    public StageLauncherTests() => Directory.CreateDirectory(_folder);

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

    private string UserPath => Path.Combine(_folder, "launcher.json");

    // ── The built-in list ─────────────────────────────────────────────────────

    /// <summary>
    /// The list that was in code before, in the same order — nothing moved by becoming a file —
    /// with CARDS second since the cards moved onto the stage (ADR-0041).
    /// </summary>
    [Fact]
    public void The_built_in_launcher_offers_what_the_code_did_in_the_same_order()
    {
        var launcher = StageLauncher.BuiltIn;

        Assert.Empty(launcher.Problems);
        Assert.Equal(
            ["GAUGES", "CARDS", "CLOCK", "COMPASS", "PHONE", "VIDEO", "MAPS", "SPOTIFY", "MUSIC", "NUVIO", "STREMIO", "PROBE"],
            launcher.Offered.Where(e => e.Type != LauncherTypes.UserApps).Select(e => e.Name));
        Assert.Equal(LauncherTypes.UserApps, launcher.Entries[^1].Type);
        Assert.Equal("GAUGES", launcher.StartOn);
    }

    /// <summary>The quick bar it shows is the one the shell showed: the first five.</summary>
    [Fact]
    public void The_built_in_quick_bar_is_the_first_five()
    {
        var names = StageLauncher.BuiltIn.Offered.Where(e => e.Name.Length > 0).Select(e => e.Name).ToList();

        Assert.Equal(names.Take(5), StageLauncher.BuiltIn.QuickBar);
    }

    /// <summary>The sources that kept playing before still do (ADR-0026); MAPS never did.</summary>
    [Fact]
    public void The_built_in_sources_keep_playing_and_maps_does_not()
    {
        var plays = StageLauncher.BuiltIn.Entries.Where(e => e.PlaysAudio).Select(e => e.Name).ToHashSet();

        Assert.Equal(["MUSIC", "NUVIO", "PHONE", "SPOTIFY", "STREMIO", "VIDEO"], plays.Order());
    }

    [Fact]
    public void Headings_come_from_the_type_unless_the_file_names_one()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [
              { "name": "maps", "type": "web", "url": "https://www.openstreetmap.org" },
              { "name": "radio", "type": "web", "url": "https://example.org/radio", "group": "media" },
              { "name": "clock", "type": "clock" }
            ] }
            """);

        Assert.Equal(["WEB", "MEDIA", "SCREENS"], launcher.Entries.Select(e => e.GroupLabel));
        Assert.Equal(["MAPS", "RADIO", "CLOCK"], launcher.Entries.Select(e => e.Name));
    }

    // ── Reading a file ────────────────────────────────────────────────────────

    [Fact]
    public void An_entry_that_cannot_work_is_left_out_and_named_and_the_rest_load()
    {
        var launcher = StageLauncher.Parse("""
            {
              // comments and trailing commas are fine
              "entries": [
                { "name": "CLOCK", "type": "clock" },
                { "name": "RADAR", "type": "hologram" },
                { "name": "NOWEB", "type": "web" },
                { "name": "FTP", "type": "web", "url": "ftp://example.org" },
                { "name": "NOAPP", "type": "app", "paths": [ "" ] },
                { "name": "CLOCK", "type": "compass" },
                { "type": "clock" },
              ],
            }
            """);

        Assert.Equal(["CLOCK"], launcher.Entries.Select(e => e.Name));
        Assert.Equal(6, launcher.Problems.Count);
        Assert.Contains(launcher.Problems, p => p.Contains("RADAR") && p.Contains("hologram"));
        Assert.Contains(launcher.Problems, p => p.Contains("NOWEB") && p.Contains("url"));
        Assert.Contains(launcher.Problems, p => p.Contains("FTP") && p.Contains("http"));
        Assert.Contains(launcher.Problems, p => p.Contains("NOAPP") && p.Contains("paths"));
        Assert.Contains(launcher.Problems, p => p.Contains("CLOCK appears twice"));
        Assert.Contains(launcher.Problems, p => p.Contains("no name"));
    }

    [Fact]
    public void A_file_that_offers_nothing_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => StageLauncher.Parse("""{ "entries": [] }"""));
        Assert.Throws<InvalidDataException>(() => StageLauncher.Parse("""{ "entries": [ { "name": "A", "type": "clock", "hidden": true } ] }"""));
        Assert.Throws<InvalidDataException>(() => StageLauncher.Parse("""{ "entries": [ { "type": "userApps" } ] }"""));
        Assert.Throws<InvalidDataException>(() => StageLauncher.Parse("not json"));
    }

    [Fact]
    public void Hidden_entries_are_kept_but_not_offered()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [
              { "name": "CLOCK", "type": "clock" },
              { "name": "MUSIC", "type": "web", "url": "https://music.apple.com", "hidden": true }
            ] }
            """);

        Assert.Equal(2, launcher.Entries.Count);
        Assert.Equal(["CLOCK"], launcher.Offered.Select(e => e.Name));
    }

    [Fact]
    public void A_zoom_out_of_range_falls_back_to_the_display_setting_and_says_so()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [
              { "name": "A", "type": "web", "url": "https://a.example", "zoom": 9 },
              { "name": "B", "type": "web", "url": "https://b.example", "zoom": 0.8 }
            ] }
            """);

        Assert.Null(launcher.Entries[0].Zoom);
        Assert.Equal(0.8, launcher.Entries[1].Zoom);
        Assert.Single(launcher.Problems, p => p.Contains("zoom"));
    }

    // ── The quick bar and the opening stage ───────────────────────────────────

    [Fact]
    public void The_quick_bar_keeps_known_names_in_order_and_drops_the_rest()
    {
        var launcher = StageLauncher.Parse("""
            {
              "quickBar": [ "maps", "CLOCK", "NOPE", "MAPS", "HIDDEN", "A", "B", "C", "D" ],
              "entries": [
                { "name": "CLOCK", "type": "clock" },
                { "name": "MAPS", "type": "web", "url": "https://www.openstreetmap.org" },
                { "name": "HIDDEN", "type": "clock", "hidden": true },
                { "name": "A", "type": "compass" }, { "name": "B", "type": "phone" },
                { "name": "C", "type": "video" }, { "name": "D", "type": "gauges" }
              ]
            }
            """);

        Assert.Equal(["MAPS", "CLOCK", "A", "B", "C"], launcher.QuickBar);
        Assert.Contains(launcher.Problems, p => p.Contains("NOPE"));
        Assert.Contains(launcher.Problems, p => p.Contains("MAPS appears twice"));
        Assert.Contains(launcher.Problems, p => p.Contains("HIDDEN"));
        Assert.Contains(launcher.Problems, p => p.Contains("only 5 fit") && p.Contains("D"));
    }

    [Fact]
    public void The_quick_bar_may_name_an_app_added_in_settings()
    {
        var launcher = StageLauncher.Parse(
            """{ "quickBar": [ "CHROME" ], "entries": [ { "name": "CLOCK", "type": "clock" } ] }""",
            extraNames: ["Chrome"]);

        Assert.Equal(["CHROME"], launcher.QuickBar);
        Assert.Empty(launcher.Problems);
    }

    [Fact]
    public void Without_a_quick_bar_the_first_five_available_get_buttons()
    {
        var launcher = StageLauncher.Parse("""{ "entries": [ { "name": "CLOCK", "type": "clock" } ] }""");

        Assert.Null(launcher.QuickBar);
        Assert.Equal(["A", "B", "C", "D", "E"], launcher.QuickBarFrom(["A", "B", "C", "D", "E", "F"]));
    }

    /// <summary>A not-installed app is not available, so a bar that names it leaves the gap closed.</summary>
    [Fact]
    public void A_quick_bar_name_that_is_not_available_is_skipped()
    {
        var launcher = StageLauncher.BuiltIn with { QuickBar = ["GAUGES", "NUVIO", "CLOCK"] };

        Assert.Equal(["GAUGES", "CLOCK"], launcher.QuickBarFrom(["GAUGES", "CLOCK", "MAPS"]));
    }

    [Fact]
    public void An_unknown_start_is_dropped_and_named()
    {
        var launcher = StageLauncher.Parse("""{ "startOn": "warp", "entries": [ { "name": "CLOCK", "type": "clock" } ] }""");

        Assert.Null(launcher.StartOn);
        Assert.Contains(launcher.Problems, p => p.Contains("startOn"));
    }

    // ── The store ─────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_file_the_store_uses_the_built_in_launcher()
    {
        var store = new StageLauncherStore(UserPath);

        Assert.Equal(LauncherOrigin.BuiltIn, store.Current.Origin);
        Assert.Empty(store.Problems);
    }

    [Fact]
    public void Your_file_replaces_the_built_in_list_outright()
    {
        File.WriteAllText(UserPath, """
            { "entries": [ { "name": "MAPS", "type": "web", "url": "https://www.openstreetmap.org" }, { "name": "GAUGES", "type": "gauges" } ] }
            """);

        var store = new StageLauncherStore(UserPath);

        Assert.Equal(LauncherOrigin.Yours, store.Current.Origin);
        Assert.Equal(["MAPS", "GAUGES"], store.Current.Offered.Select(e => e.Name));
    }

    [Fact]
    public void A_broken_file_costs_a_warning_never_the_launcher()
    {
        File.WriteAllText(UserPath, "{ this is not json");

        var store = new StageLauncherStore(UserPath);

        Assert.Equal(LauncherOrigin.BuiltIn, store.Current.Origin);
        Assert.Contains(store.Problems, p => p.Contains("launcher.json was not used"));
    }

    [Fact]
    public void Reload_picks_up_a_hand_edit_and_says_so()
    {
        var store = new StageLauncherStore(UserPath);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        File.WriteAllText(UserPath, """{ "entries": [ { "name": "TIME", "type": "clock" } ] }""");
        store.Reload();

        Assert.Equal(1, raised);
        Assert.Equal(["TIME"], store.Current.Offered.Select(e => e.Name));
    }

    [Fact]
    public void The_example_is_the_built_in_list_and_reads_back_as_it()
    {
        var store = new StageLauncherStore(UserPath);

        Assert.Null(store.WriteExample());

        var example = StageLauncher.Parse(File.ReadAllText(store.ExamplePath));
        Assert.Equal(
            StageLauncher.BuiltIn.Entries.Select(e => (e.Name, e.Type)),
            example.Entries.Select(e => (e.Name, e.Type)));
        Assert.False(File.Exists(UserPath));
    }

    // ── Settings ▸ Apps ───────────────────────────────────────────────────────

    [Fact]
    public void Make_it_mine_writes_a_file_that_loads_as_yours()
    {
        var store = new StageLauncherStore(UserPath);
        var settings = new LauncherSettingsViewModel(store, new NoDialogs());

        Assert.Equal("BUILT IN", settings.Source);
        Assert.True(settings.CanMakeYours);

        settings.MakeYoursCommand.Execute(null);

        Assert.True(File.Exists(UserPath));
        Assert.Equal(LauncherOrigin.Yours, store.Current.Origin);
        Assert.Empty(store.Problems);
        Assert.StartsWith("YOUR FILE", settings.Source);
        Assert.False(settings.CanMakeYours);
        Assert.False(settings.MakeYoursCommand.CanExecute(null));
    }

    [Fact]
    public void The_settings_list_shows_every_entry_and_where_it_lives()
    {
        File.WriteAllText(UserPath, """
            {
              "quickBar": [ "MAPS" ],
              "entries": [
                { "name": "MAPS", "type": "web", "url": "https://www.openstreetmap.org" },
                { "name": "TOWING", "type": "gauges", "layout": "towing" },
                { "name": "MUSIC", "type": "web", "url": "https://music.apple.com", "hidden": true },
                { "type": "userApps" }
              ]
            }
            """);

        var settings = new LauncherSettingsViewModel(new StageLauncherStore(UserPath), new NoDialogs());

        Assert.Equal(["MAPS", "TOWING", "MUSIC", "YOUR APPS"], settings.Entries.Select(e => e.Name));
        Assert.Equal(["BAR", "GRID", "HIDDEN", "GRID"], settings.Entries.Select(e => e.Place));
        Assert.Contains("layout towing", settings.Entries[1].Detail);
    }

    private sealed class NoDialogs : IThemeDialogs
    {
        public string? PickThemeFile() => null;

        public string? PickExportFolder() => null;

        public void OpenFolder(string folder)
        {
        }
    }

    // ── The cards on the stage (ADR-0041) ─────────────────────────────────────

    [Fact]
    public void The_built_in_quick_bar_has_the_cards_beside_the_gauges()
    {
        Assert.Equal(["GAUGES", "CARDS", "CLOCK", "COMPASS", "PHONE"], StageLauncher.BuiltIn.QuickBar);
        Assert.Equal(LauncherTypes.Cards, StageLauncher.BuiltIn.Entries.Single(e => e.Name == "CARDS").Type);
    }

    [Fact]
    public void A_cards_entry_is_read_like_any_other()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [ { "name": "Tiles", "type": "cards", "detail": "mine" }, { "name": "CLOCK", "type": "clock" } ] }
            """);

        Assert.Empty(launcher.Problems);
        Assert.Equal(("TILES", LauncherTypes.Cards), (launcher.Entries[0].Name, launcher.Entries[0].Type));
    }
}
