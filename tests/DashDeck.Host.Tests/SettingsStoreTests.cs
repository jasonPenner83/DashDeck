using System.Text.Json;
using DashDeck.Host.Settings;

namespace DashDeck.Host.Tests;

/// <summary>
/// The persistence contract: a settings file written by one build has to load into another.
/// </summary>
/// <remarks>
/// This is the answer to "will my colours survive an update". They survive because the file
/// lives outside the folder <c>publish.ps1</c> deletes, and because neither an older nor a
/// newer shape of the file is fatal. The second half is what these check.
/// </remarks>
public sealed class SettingsStoreTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void A_file_from_an_older_build_keeps_the_settings_it_knew_about()
    {
        // No accentColour at all — what a build before custom accents would have written.
        var settings = JsonSerializer.Deserialize<UserSettings>(
            """{ "themeMode": "Night", "accentName": "CYAN" }""", Options);

        Assert.NotNull(settings);
        Assert.Equal("Night", settings.ThemeMode);
        Assert.Equal("CYAN", settings.AccentName);
        Assert.Equal("#FF7A1A", settings.AccentColour);
    }

    [Fact]
    public void A_file_from_a_newer_build_loads_rather_than_throwing()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>(
            """{ "themeMode": "Day", "somethingNotInventedYet": { "nested": true } }""", Options);

        Assert.NotNull(settings);
        Assert.Equal("Day", settings.ThemeMode);
    }

    /// <summary>
    /// Outside the app folder, which is the entire point — <c>publish.ps1</c> deletes and
    /// rewrites <c>dist\DashDeck</c> on every build.
    /// </summary>
    [Fact]
    public void Settings_live_in_local_app_data_not_beside_the_executable()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(expected, SettingsStore.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, SettingsStore.Path, StringComparison.OrdinalIgnoreCase);
    }
}
