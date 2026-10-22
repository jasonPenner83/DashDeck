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

    /// <summary>
    /// The master switch for clearing trouble codes (ADR-0006 gate 2, ADR-0055). Off by default;
    /// read at the moment of clearing, so turning it off stops the next one at once.
    /// </summary>
    [JsonPropertyName("allowClearCodes")]
    public bool AllowClearCodes { get; init; }

    /// <summary>
    /// Which warnings interrupt the screen, where the user has said otherwise than the warnings
    /// file (ADR-0055). Keyed by warning id; absent means the file's default.
    /// </summary>
    [JsonPropertyName("warningPopups")]
    public Dictionary<string, bool> WarningPopups { get; init; } = [];

    /// <summary>
    /// Warnings dismissed while lit, and the trouble-code count each was dismissed at, so a light
    /// that stays on does not pop again at every start. Forgotten when the light is seen off.
    /// </summary>
    [JsonPropertyName("dismissedWarnings")]
    public Dictionary<string, double?> DismissedWarnings { get; init; } = [];

    /// <summary>Whether DashDeck's playback gets out of the way of spoken instructions (Q14).</summary>
    [JsonPropertyName("duckUnderInstructions")]
    public bool DuckUnderInstructions { get; init; } = true;

    /// <summary>Whether DashDeck sets the tablet's backlight (ADR-0068).</summary>
    [JsonPropertyName("brightnessEnabled")]
    public bool BrightnessEnabled { get; init; } = true;

    /// <summary>The backlight by day, per cent (ADR-0068).</summary>
    [JsonPropertyName("dayBrightness")]
    public int DayBrightness { get; init; } = 100;

    /// <summary>The backlight by night, per cent (ADR-0068).</summary>
    [JsonPropertyName("nightBrightness")]
    public int NightBrightness { get; init; } = 30;

    /// <summary>
    /// The level the backlight was at before DashDeck changed it, while it is still owed back —
    /// written when it is borrowed so a crash can be undone at the next launch (ADR-0068).
    /// </summary>
    [JsonPropertyName("backlightOriginal")]
    public int? BacklightOriginal { get; init; }

    /// <summary>The level DashDeck last left the backlight at, beside <see cref="BacklightOriginal"/>.</summary>
    [JsonPropertyName("backlightSet")]
    public int? BacklightSet { get; init; }

    /// <summary>What the map draws under its roads: <c>Road</c>, <c>Satellite</c> or <c>Terrain</c> (ADR-0071).</summary>
    [JsonPropertyName("mapBase")]
    public string MapBase { get; init; } = "Road";

    /// <summary>Which way is up on the map: <c>NorthUp</c> or <c>TrackUp</c> (ADR-0071).</summary>
    [JsonPropertyName("mapOrientation")]
    public string MapOrientation { get; init; } = "NorthUp";

    /// <summary>
    /// Whether a destination can be typed while the truck is moving (ADR-0072). Off by default,
    /// which keeps ADR-0065's speed gate; the owner turns it on.
    /// </summary>
    [JsonPropertyName("searchWhileMoving")]
    public bool SearchWhileMoving { get; init; }

    /// <summary>Whether the map draws the range lines (ADR-0071).</summary>
    [JsonPropertyName("mapRange")]
    public bool MapRange { get; init; } = true;

    /// <summary>
    /// Which Android the ANDROID cell opens: <c>Regular</c> or <c>Automotive</c> (ADR-0075). Chosen
    /// by holding the cell.
    /// </summary>
    [JsonPropertyName("androidFlavour")]
    public string AndroidFlavour { get; init; } = "Regular";

    /// <summary>
    /// Whether Android hears the tablet's microphone — for "Hey Google" and voice typing (ADR-0075).
    /// The emulator gives it silence otherwise. A settings-file switch; on unless turned off.
    /// </summary>
    [JsonPropertyName("androidMicrophone")]
    public bool AndroidMicrophone { get; init; } = true;

    /// <summary>
    /// How the emulator draws (ADR-0075): <c>auto</c> — the tablet's GPU, falling back to the
    /// emulator's own choice if it will not start that way — <c>emulator</c> to leave it to the
    /// emulator, or one of its <c>-gpu</c> modes (<c>host</c>, <c>angle_indirect</c>,
    /// <c>swiftshader_indirect</c>) as it is. A settings-file switch.
    /// </summary>
    [JsonPropertyName("androidGpu")]
    public string AndroidGpu { get; init; } = "auto";

    /// <summary>
    /// How big a screen Android draws (ADR-0075): <c>SHARP</c>, <c>BALANCED</c> or <c>FAST</c> — the
    /// stage's own pixels, three quarters, or half, scaled up to fill. Chosen in the hold menu.
    /// </summary>
    [JsonPropertyName("androidPicture")]
    public string AndroidPicture { get; init; } = "BALANCED";
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
    private static readonly Lock Gate = new();

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
    /// one dash at a time — but serialised within this one.
    /// </para>
    /// </remarks>
    public static void Update(Func<UserSettings, UserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        // One process, but no longer one thread: the backlight's worker writes here too
        // (ADR-0068), and two read-transform-writes interleaved would lose one of them.
        lock (Gate)
        {
            Save(change(Load()));
        }
    }
}
