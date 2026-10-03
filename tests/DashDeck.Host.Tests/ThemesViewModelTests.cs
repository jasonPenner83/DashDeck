using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Host.Theme;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>Settings ▸ Themes (ADR-0036): wearing, importing, exporting, saving your own.</summary>
public sealed partial class ThemesViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-themes-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Wears themes without WPF: remembers what it was asked to wear.</summary>
    private sealed partial class FakeHost(ThemeLibrary library) : ObservableObject, IThemeHost
    {
        public ThemeLibrary Library { get; } = library;

        [ObservableProperty]
        private ThemeDefinition _current = ThemeDefinition.BuiltIn;

        [ObservableProperty]
        private IReadOnlyList<string> _problems = [];

        public int Reloads { get; private set; }

        public void Wear(ThemeDefinition theme) => Current = theme;

        public void Reload()
        {
            Reloads++;
            Library.Reload();
            Current = Library.Find(Current.Id) ?? ThemeDefinition.BuiltIn;
        }
    }

    private sealed class FakeDialogs : IThemeDialogs
    {
        public string? ThemeFile { get; set; }

        public string? ExportFolder { get; set; }

        public string? Opened { get; private set; }

        public string? PickThemeFile() => ThemeFile;

        public string? PickExportFolder() => ExportFolder;

        public void OpenFolder(string folder) => Opened = folder;
    }

    private (ThemesViewModel Themes, FakeHost Host, FakeDialogs Dialogs) Make()
    {
        var shipped = Path.Combine(_dir, "shipped");
        Directory.CreateDirectory(shipped);
        File.WriteAllText(Path.Combine(shipped, "black.json"),
            """{ "name": "Black", "description": "All black.", "tokens": { "canvas": "#000000", "buttonRadius": 28 } }""");

        var host = new FakeHost(new ThemeLibrary(shipped, Path.Combine(_dir, "yours")));
        var dialogs = new FakeDialogs();
        return (new ThemesViewModel(host, dialogs), host, dialogs);
    }

    [Fact]
    public void Lists_the_built_in_look_then_shipped_themes_and_marks_the_one_worn()
    {
        var (themes, _, _) = Make();

        Assert.Equal(["MODERN", "BLACK"], themes.Themes.Select(t => t.Caption));
        Assert.True(themes.Themes[0].IsCurrent);
        Assert.Equal("BUILT IN", themes.Themes[0].OriginLabel);
        Assert.Equal("#000000", themes.Themes[1].Swatches[0]);
        Assert.False(themes.Themes[1].CanDelete);
    }

    [Fact]
    public void Tapping_a_theme_wears_it_and_the_token_reference_follows()
    {
        var (themes, host, _) = Make();

        themes.WearCommand.Execute(themes.Themes[1]);

        Assert.Equal("Black", host.Current.Name);
        Assert.True(themes.Themes[1].IsCurrent);
        Assert.False(themes.Themes[0].IsCurrent);
        Assert.Equal("WEARING", themes.Status[..7].ToUpperInvariant());

        var radius = themes.Tokens.Single(t => t.Key == "buttonRadius");
        Assert.Equal("28", radius.Value);
        Assert.True(radius.IsSet);
        Assert.False(themes.Tokens.Single(t => t.Key == "accent").IsSet);
        Assert.Equal(ThemeTokens.All.Count, themes.Tokens.Count);
    }

    [Fact]
    public void Save_as_makes_your_own_copy_wears_it_and_it_can_be_deleted()
    {
        var (themes, host, _) = Make();
        themes.WearCommand.Execute(themes.Themes[1]);

        Assert.False(themes.SaveAsCommand.CanExecute(null));
        themes.SaveAsName = "My Black";
        themes.SaveAsCommand.Execute(null);

        Assert.Equal("yours/my-black", host.Current.Id);
        var mine = themes.Themes.Single(t => t.Theme.Id == "yours/my-black");
        Assert.True(mine.CanDelete);
        Assert.True(mine.IsCurrent);

        themes.DeleteCommand.Execute(mine);

        Assert.DoesNotContain(themes.Themes, t => t.Theme.Id == "yours/my-black");
        Assert.Equal("builtin/modern", host.Current.Id);
    }

    [Fact]
    public void Import_and_export_go_through_the_pickers_and_a_bad_file_says_why()
    {
        var (themes, host, dialogs) = Make();
        var downloads = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(downloads);

        File.WriteAllText(Path.Combine(downloads, "bad.json"), "{ not a theme");
        dialogs.ThemeFile = Path.Combine(downloads, "bad.json");
        themes.ImportCommand.Execute(null);
        Assert.StartsWith("Could not import bad.json", themes.Status, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(downloads, "good.json"), """{ "name": "Good" }""");
        dialogs.ThemeFile = Path.Combine(downloads, "good.json");
        themes.ImportCommand.Execute(null);
        Assert.Equal("yours/good", host.Current.Id);

        dialogs.ExportFolder = Path.Combine(_dir, "usb");
        themes.ExportCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(_dir, "usb", "good.json")));

        // Cancelling a picker does nothing.
        dialogs.ThemeFile = null;
        themes.ImportCommand.Execute(null);
        Assert.Equal("yours/good", host.Current.Id);
    }

    [Fact]
    public void Reload_picks_up_a_hand_edit_and_open_folder_shows_yours()
    {
        var (themes, host, dialogs) = Make();
        Directory.CreateDirectory(host.Library.UserFolder);
        File.WriteAllText(Path.Combine(host.Library.UserFolder, "later.json"), """{ "name": "Later" }""");

        Assert.DoesNotContain(themes.Themes, t => t.Theme.Name == "Later");
        themes.ReloadCommand.Execute(null);
        Assert.Contains(themes.Themes, t => t.Theme.Name == "Later");
        Assert.Equal(1, host.Reloads);

        themes.OpenFolderCommand.Execute(null);
        Assert.Equal(host.Library.UserFolder, dialogs.Opened);
    }

    [Fact]
    public void A_file_that_would_not_load_is_a_warning_not_a_secret()
    {
        var (themes, host, _) = Make();
        Directory.CreateDirectory(host.Library.UserFolder);
        File.WriteAllText(Path.Combine(host.Library.UserFolder, "broken.json"), "{");

        themes.ReloadCommand.Execute(null);

        Assert.True(themes.HasWarnings);
        Assert.Contains(themes.Warnings, w => w.StartsWith("broken.json was not loaded", StringComparison.Ordinal));
    }
}
