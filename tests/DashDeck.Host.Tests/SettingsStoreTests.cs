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
    public void Fast_requests_are_on_for_a_file_written_before_they_existed_and_off_when_turned_off()
    {
        var older = JsonSerializer.Deserialize<UserSettings>("""{ "themeMode": "Day" }""", Options);
        var off = JsonSerializer.Deserialize<UserSettings>("""{ "fastRequests": false }""", Options);

        Assert.True(older!.FastRequests);
        Assert.False(off!.FastRequests);
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
    /// Every stored setting names itself in the file, and no two name themselves the same.
    /// </summary>
    /// <remarks>
    /// Both halves of this were broken at once by adding six settings, and the way it broke is
    /// worth keeping a guard for.
    /// <para>
    /// A text insertion landed <b>between</b> <c>[JsonPropertyName("warningPopups")]</c> and the
    /// property it decorates, so the new property inherited the attribute and two properties
    /// claimed one name. <see cref="System.Text.Json"/> does not shrug at that — it throws on
    /// <i>every</i> read of the type, so the whole settings file stopped loading and five tests
    /// went red at once. A collision is therefore not a tidiness matter.
    /// </para>
    /// <para>
    /// The missing-attribute half is quieter and would have shipped. There is no naming policy on
    /// the serializer, so a property without the attribute writes itself <c>PascalCase</c> into a
    /// file that is camelCase throughout. Reading is case-insensitive, so nothing would have
    /// failed — it would just have written a settings file with two spelling conventions in it,
    /// and only the explicit ones would survive a future change of policy.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_setting_names_itself_in_the_file_and_no_two_share_a_name()
    {
        var properties = typeof(UserSettings).GetProperties();
        var unnamed = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<string>();

        foreach (var property in properties)
        {
            var attribute = property
                .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .FirstOrDefault();

            if (attribute is null)
            {
                unnamed.Add(property.Name);
                continue;
            }

            if (names.TryGetValue(attribute.Name, out var already))
            {
                collisions.Add($"{property.Name} and {already} both claim \"{attribute.Name}\"");
            }
            else
            {
                names[attribute.Name] = property.Name;
            }
        }

        Assert.True(
            unnamed.Count == 0,
            "These would write themselves PascalCase into a camelCase file:\n" + string.Join("\n", unnamed));

        Assert.True(
            collisions.Count == 0,
            "A duplicate name throws on every read of the settings file:\n" + string.Join("\n", collisions));
    }

    /// <summary>
    /// The type can actually be read, which a name collision prevents outright.
    /// </summary>
    /// <remarks>
    /// The test above explains why; this is the one-line proof, and it fails for a collision
    /// introduced in any way — not only a missing or stolen attribute.
    /// </remarks>
    [Fact]
    public void The_settings_type_can_be_deserialised_at_all()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("{}", Options);

        Assert.NotNull(settings);
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
