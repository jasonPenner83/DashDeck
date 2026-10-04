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

    /// <summary>The theme being worn (ADR-0036): <c>builtin/modern</c>, <c>shipped/…</c> or <c>yours/…</c>.</summary>
    [JsonPropertyName("themeId")]
    public string ThemeId { get; init; } = "builtin/modern";

    /// <summary>The stage layout (ADR-0037): <c>theme</c> to follow the theme, or a layout id.</summary>
    [JsonPropertyName("stageLayout")]
    public string StageLayout { get; init; } = "theme";

    /// <summary>The climate panel layout (ADR-0040): <c>theme</c> to follow the theme, or a layout id.</summary>
    [JsonPropertyName("climateLayout")]
    public string ClimateLayout { get; init; } = "theme";

    /// <summary>
    /// The extras from <c>catalog/extras</c> already put in the user's folders (ADR-0043) — once each,
    /// so one the user deleted stays deleted.
    /// </summary>
    [JsonPropertyName("installedExtras")]
    public IReadOnlyList<string> InstalledExtras { get; init; } = [];

    /// <summary>The console layout (ADR-0041): <c>theme</c> to follow the theme, or a layout id.</summary>
    [JsonPropertyName("consoleLayout")]
    public string ConsoleLayout { get; init; } = "theme";

    /// <summary>A preset's name, or <c>CUSTOM</c>.</summary>
    [JsonPropertyName("accentName")]
    public string AccentName { get; init; } = "EMBER";

    /// <summary>The accent as <c>#RRGGBB</c>. Authoritative — the name is for display.</summary>
    [JsonPropertyName("accentColour")]
    public string AccentColour { get; init; } = "#FF7A1A";

    /// <summary>
    /// How large web occupants render, as a multiplier. Below 1 fits more on screen.
    /// </summary>
    /// <remarks>
    /// The web players are built for phones held at arm's length, and on a 912-wide stage
    /// they waste most of it on padding. This is the browser's own zoom rather than a WPF
    /// transform, so the page reflows into the space instead of being drawn small.
    /// </remarks>
    [JsonPropertyName("webScale")]
    public double WebScale { get; init; } = 1.0;

    /// <summary>
    /// Keep an audio/video source playing in the background when you switch the stage to a silent
    /// occupant (ADR-0026). Off by default: it changes the plain "replacing ends it" behaviour
    /// (ADR-0025), so it is opt-in.
    /// </summary>
    [JsonPropertyName("keepStageAudio")]
    public bool KeepStageAudio { get; init; }

    /// <summary>Take GPS from the phone over the network (ADR-0027). Off by default.</summary>
    [JsonPropertyName("gpsEnabled")]
    public bool GpsEnabled { get; init; }

    /// <summary>Where the phone's GPS-share app is serving NMEA, as <c>host:port</c>.</summary>
    [JsonPropertyName("gpsEndpoint")]
    public string GpsEndpoint { get; init; } = "";

    /// <summary>How the phone's GPS arrives: <c>Bluetooth</c> (the default) or <c>Network</c>.</summary>
    [JsonPropertyName("gpsTransport")]
    public string GpsTransport { get; init; } = "Bluetooth";

    /// <summary>The paired phone's virtual COM port, e.g. <c>COM7</c>, for the Bluetooth transport.</summary>
    [JsonPropertyName("gpsSerialPort")]
    public string GpsSerialPort { get; init; } = "";

    /// <summary>
    /// Usable fuel tank, litres — a truck fact a range estimate needs and no PID reports
    /// (ADR-0029). Defaults to the 36 US-gallon tank (≈ 136 L) Jason's F-150 has.
    /// </summary>
    [JsonPropertyName("fuelTankLitres")]
    public double FuelTankLitres { get; init; } = 136;

    /// <summary>
    /// The OBD-II adapter's virtual COM port, e.g. <c>COM7</c>. Empty means run the
    /// synthetic truck.
    /// </summary>
    /// <remarks>
    /// Empty is the default and stays the default: most of this app's life is spent on a
    /// desk with no vehicle attached (ADR-0005), and a dash that comes up dead there would
    /// be worse than one that comes up simulated and says so.
    /// </remarks>
    [JsonPropertyName("adapterSerialPort")]
    public string AdapterSerialPort { get; init; } = "";

    /// <summary>
    /// The baud rate the adapter last answered at, tried first next time (ADR-0034). Zero when
    /// none has been seen. Saves the dash working through every rate on every launch.
    /// </summary>
    [JsonPropertyName("adapterBaudRate")]
    public int AdapterBaudRate { get; init; }

    /// <summary>
    /// What the adapter last said it was (its <c>ATI</c> reply). How it is recognised on another
    /// port when Windows renumbers it (ADR-0034). Empty when none has been seen.
    /// </summary>
    [JsonPropertyName("adapterIdentity")]
    public string AdapterIdentity { get; init; } = "";

    /// <summary>
    /// Ask the engine computer's standard values the fast way (ADR-0049): filtered to its answer,
    /// with a response count. On by default; applied at the next launch.
    /// </summary>
    [JsonPropertyName("fastRequests")]
    public bool FastRequests { get; init; } = true;
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

    /// <summary>
    /// Change some settings without touching the rest.
    /// </summary>
    /// <remarks>
    /// <b>The only safe way to write this file once more than one thing owns a setting in
    /// it.</b> <c>ThemeService</c> used to build a whole <see cref="UserSettings"/> from its
    /// own three fields and save that — correct while it was the sole writer, and silently
    /// destructive the moment anything else stored a preference here: the next theme change
    /// would reset that preference to its default with nothing to show for it.
    /// <para>
    /// Read, transform, write. Not atomic against another process, which does not matter —
    /// one dash, one writer at a time.
    /// </para>
    /// </remarks>
    public static void Update(Func<UserSettings, UserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Load()));
    }
}
