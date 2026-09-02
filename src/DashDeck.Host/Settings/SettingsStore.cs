using System.Text.Json.Serialization;

namespace DashDeck.Host.Settings;

/// <summary>
/// Everything the user has chosen. Deliberately small and deliberately flat.
/// </summary>
/// <remarks>
/// Every property has a default, and unknown properties in the file are ignored. That is
/// what lets a settings file written by an older build load into a newer one, and the other
/// way round: adding a setting is not a migration, and removing one is not a crash.
/// </remarks>
public sealed record UserSettings
{
    /// <summary>Day, Night or Auto. Stored by name so the file stays readable.</summary>
    [JsonPropertyName("themeMode")]
    public string ThemeMode { get; init; } = "Auto";

    /// <summary>A preset's name, or <c>CUSTOM</c>.</summary>
    [JsonPropertyName("accentName")]
    public string AccentName { get; init; } = "EMBER";

    /// <summary>The accent as <c>#RRGGBB</c>. Authoritative — the name is for display.</summary>
    [JsonPropertyName("accentColour")]
    public string AccentColour { get; init; } = "#FF7A1A";
}

/// <summary>
/// Loads and saves <see cref="UserSettings"/>.
/// </summary>
/// <remarks>
/// The file lives in <c>%LOCALAPPDATA%\DashDeck\</c>, and where it lives is the whole point
/// — see <see cref="JsonFile.InLocalAppData"/>, which is also what guarantees the settings
/// file and the dashboard file cannot end up in different places.
/// </remarks>
public static class SettingsStore
{
    /// <summary>Why the last load or save failed, if it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Where the settings file is. Shown in the settings screen.</summary>
    public static string Path => JsonFile.InLocalAppData("settings.json");

    /// <summary>
    /// Read what is stored, or the defaults.
    /// </summary>
    /// <remarks>
    /// Never throws. A corrupt or half-written file gives you the defaults and a recorded
    /// reason — losing a colour preference is not worth failing to start a dash over.
    /// </remarks>
    public static UserSettings Load()
    {
        var settings = JsonFile.Load<UserSettings>(Path, out var error);
        LastError = error;
        return settings ?? new UserSettings();
    }

    /// <summary>Write settings out. Never throws, for the same reason.</summary>
    public static void Save(UserSettings settings)
    {
        JsonFile.Save(Path, settings, out var error);
        LastError = error;
    }
}
