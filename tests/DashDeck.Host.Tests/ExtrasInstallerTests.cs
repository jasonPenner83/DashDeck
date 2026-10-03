using System.IO;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>Extras — LCARS so far — copied into the user's folders once (ADR-0043).</summary>
public sealed class ExtrasInstallerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-extras-{Guid.NewGuid():N}");

    public ExtrasInstallerTests() => Directory.CreateDirectory(_folder);

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

    private string Extras => Path.Combine(_folder, "extras");

    private string User(string kind) => Path.Combine(_folder, "user", kind);

    private void Ship(string pack, string kind, string file, string text)
    {
        Directory.CreateDirectory(Path.Combine(Extras, pack, kind));
        File.WriteAllText(Path.Combine(Extras, pack, kind, file), text);
    }

    [Fact]
    public void An_extra_lands_in_the_users_folders_once()
    {
        Ship("lcars", "themes", "lcars.json", "theme");
        Ship("lcars", "console", "lcars.json", "console");

        var (installed, problems) = ExtrasInstaller.InstallNew(Extras, User, []);

        Assert.Equal(["lcars"], installed);
        Assert.Empty(problems);
        Assert.Equal("theme", File.ReadAllText(Path.Combine(User("themes"), "lcars.json")));
        Assert.Equal("console", File.ReadAllText(Path.Combine(User("console"), "lcars.json")));
        Assert.False(Directory.Exists(User("stage")));

        // Deleted by the user, and already installed: it stays deleted.
        File.Delete(Path.Combine(User("themes"), "lcars.json"));
        Assert.Empty(ExtrasInstaller.InstallNew(Extras, User, ["lcars"]).Installed);
        Assert.False(File.Exists(Path.Combine(User("themes"), "lcars.json")));
    }

    [Fact]
    public void A_file_the_user_already_has_is_left_alone()
    {
        Ship("lcars", "themes", "lcars.json", "shipped");
        Directory.CreateDirectory(User("themes"));
        File.WriteAllText(Path.Combine(User("themes"), "lcars.json"), "mine");

        Assert.Equal(["lcars"], ExtrasInstaller.InstallNew(Extras, User, []).Installed);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(User("themes"), "lcars.json")));
    }

    [Fact]
    public void No_extras_folder_installs_nothing() =>
        Assert.Empty(ExtrasInstaller.InstallNew(Path.Combine(_folder, "nowhere"), User, []).Installed);

    [Theory]
    [InlineData("shipped/lcars-inspired", "yours/lcars-inspired")]
    [InlineData("yours/mine", "yours/mine")]
    [InlineData("builtin/modern", "builtin/modern")]
    public void A_choice_of_a_moved_shipped_file_finds_it_in_yours(string stored, string expected) =>
        Assert.Equal(expected, ExtrasInstaller.Moved(stored));

    private static string Lcars([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", "extras", "lcars");

    [Fact]
    public void The_lcars_extra_ships_all_four_kinds()
    {
        var root = Lcars();
        Assert.All(ExtrasInstaller.Kinds, kind => Assert.True(Directory.Exists(Path.Combine(root, kind)), kind));
    }
}
