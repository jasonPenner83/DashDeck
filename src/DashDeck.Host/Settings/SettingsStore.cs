using System.IO;
using System.Text.Json;
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
/// The file lives in <c>%LOCALAPPDATA%\DashDeck\</c>, and where it lives is the whole point.
/// The application is a self-contained folder that <c>publish.ps1</c> <b>deletes and
/// rewrites</b> on every build, so anything stored beside the executable would be destroyed
/// by the next update. <c>%LOCALAPPDATA%</c> is outside that folder: settings survive a
/// rebuild, an update, and deleting the app entirely.
/// <para>
/// That is also constraint C1 working as intended — settings in <c>%LOCALAPPDATA%</c>, never
/// the registry, and uninstalling is still deleting a folder.
/// </para>
/// </remarks>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Why the last load or save failed, if it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Where the settings file is. Shown in the settings screen.</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashDeck",
        "settings.json");

    /// <summary>
    /// Read what is stored, or the defaults.
    /// </summary>
    /// <remarks>
    /// Never throws. A corrupt or half-written file gives you the defaults and a recorded
    /// reason — losing a colour preference is not worth failing to start a dash over.
    /// </remarks>
    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new UserSettings();
            }

            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path), Options)
                   ?? new UserSettings();
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return new UserSettings();
        }
    }

    /// <summary>Write settings out. Never throws, for the same reason.</summary>
    public static void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
