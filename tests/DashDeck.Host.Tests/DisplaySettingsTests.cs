using DashDeck.Host.Settings;

namespace DashDeck.Host.Tests;

/// <summary>
/// The web scale, and the read-modify-write it forced.
/// </summary>
/// <remarks>
/// The interesting test here is not the clamp — it is that two owners can write the same
/// settings file without destroying each other's fields, which stopped being hypothetical the
/// moment anything other than the theme stored a preference.
/// </remarks>
public sealed class DisplaySettingsTests
{
    /// <summary>
    /// The bug this exists to prevent. <c>ThemeService</c> used to build a whole
    /// <see cref="UserSettings"/> from its own three fields and save it, which was correct
    /// while it was the sole writer and silently destructive afterwards: the next theme change
    /// would reset the web scale to its default with nothing on screen to explain it.
    /// </summary>
    [Fact]
    public void Updating_one_setting_leaves_the_others_alone()
    {
        var stored = new UserSettings
        {
            ThemeMode = "Night",
            AccentName = "CYAN",
            AccentColour = "#00E5FF",
            WebScale = 0.7,
        };

        // What a theme change now does.
        var afterTheme = stored with { ThemeMode = "Day", AccentName = "EMBER", AccentColour = "#FF7A1A" };

        Assert.Equal(0.7, afterTheme.WebScale);

        // And the other direction.
        var afterScale = afterTheme with { WebScale = 0.5 };

        Assert.Equal("Day", afterScale.ThemeMode);
        Assert.Equal("#FF7A1A", afterScale.AccentColour);
        Assert.Equal(0.5, afterScale.WebScale);
    }

    /// <summary>An older file has no scale at all, and must load rather than fail.</summary>
    [Fact]
    public void A_settings_file_without_a_scale_defaults_to_full_size()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(
            """{ "themeMode": "Auto", "accentName": "EMBER", "accentColour": "#FF7A1A" }""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(settings);
        Assert.Equal(1.0, settings.WebScale);
    }

    /// <summary>
    /// A settings file is editable by hand, and WebView2 throws on a factor it dislikes
    /// rather than clamping — so nothing from outside is trusted straight.
    /// </summary>
    [Theory]
    [InlineData(0.7, 0.7)]
    [InlineData(0.0, DisplaySettings.MinimumScale)]
    [InlineData(-3.0, DisplaySettings.MinimumScale)]
    [InlineData(99.0, DisplaySettings.MaximumScale)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void A_scale_from_outside_is_brought_into_range(double stored, double expected) =>
        Assert.Equal(expected, DisplaySettings.Clamp(stored));

    /// <summary>Every offered scale is one WebView2 will accept, so no chip can throw.</summary>
    [Fact]
    public void Every_offered_scale_is_usable()
    {
        Assert.NotEmpty(DisplaySettings.Choices);

        foreach (var scale in DisplaySettings.Choices)
        {
            Assert.Equal(scale, DisplaySettings.Clamp(scale));
        }
    }
}
