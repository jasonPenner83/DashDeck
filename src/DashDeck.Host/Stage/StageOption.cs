using DashDeck.Abstractions;
using DashDeck.Host.Sensors;
using DashDeck.Host.Settings;
using Microsoft.Win32;

namespace DashDeck.Host.Stage;

/// <summary>
/// One thing that can be put on the stage, as offered by the picker.
/// </summary>
/// <remarks>
/// An option is not an occupant â€” it is the <em>offer</em> of one. Nothing is constructed
/// until it is chosen, which matters because constructing the video occupant loads VLC.
/// Unavailable options are still listed, greyed, because "Nuvio, not built yet" tells you
/// more about where this is going than an empty row does.
/// </remarks>
/// <param name="Name">Short uppercase name, matching the stage chip.</param>
/// <param name="Detail">One line under it â€” what it is, or why it is unavailable.</param>
/// <param name="Create">
/// Builds the occupant, or returns <see langword="null"/> for an empty stage. A
/// <see langword="null"/> factory means the option cannot be chosen at all.
/// </param>
public sealed record StageOption(string Name, string Detail, Func<IStageOccupant?>? Create, StageKind Kind = StageKind.Screen)
{
    /// <summary>False for the placeholders â€” listed, but not choosable.</summary>
    public bool IsAvailable => Create is not null;

    /// <summary>The picker heading this option groups under.</summary>
    public string GroupLabel => Kind switch
    {
        StageKind.Web => "WEB",
        StageKind.App => "APPS",
        _ => "SCREENS",
    };

    /// <summary>
    /// Build the list the launcher shows.
    /// </summary>
    /// <param name="videoPath">
    /// A file from <c>--video</c>, if one was given. Without it the video option still
    /// works â€” it just asks which file when chosen.
    /// </param>
    /// <param name="clock">Injected, because nothing here reads the wall clock directly.</param>
    /// <param name="signals">
    /// Named-signal access, for occupants that want vehicle data. The compass is the first;
    /// it asks the truck for a heading before it asks the tablet.
    /// </param>
    /// <param name="sensors">
    /// The tablet's sensors, resolved truck-first. Passed in rather than built here because it
    /// outlives any one occupant: it holds the mount reference and the vehicle declarations.
    /// </param>
    /// <param name="userApps">
    /// Native apps the user added through the settings UI (ADR-0024). They flow through the same
    /// <see cref="AppStageOccupant"/> as the built-in launchers — the list is the only thing that
    /// was ever hardcoded — and are appended after the built-ins, in the order they were added.
    /// </param>
    public static IReadOnlyList<StageOption> All(
        string? videoPath,
        IClock clock,
        IVehicleSignals signals,
        SensorService sensors,
        WeatherService weather,
        DisplaySettings display,
        IReadOnlyList<UserAppEntry>? userApps = null) =>
    [
        // ── SCREENS: rendered inside DashDeck, never a separate process. ──────────────────

        // The idle default (F12/B6): an auxiliary gauge cluster in the F-150's style, showing
        // what the factory cluster leaves out — boost, oil temp, voltage. A truck's home
        // screen wanting gauges is a better idle than a clock, and it means there is no
        // arbitrary "last occupant" to restore on ignition.
        new StageOption("GAUGES", "Boost, oil, volts — what the cluster hides", () => new GaugesStageOccupant(signals)),

        // Time and weather, the other idle. No "nothing" option: an empty stage announcing its
        // own emptiness was honest but useless.
        new StageOption("CLOCK", "Time and weather", () => new ClockWeatherStageOccupant(clock, weather)),

        // Truck first, tablet second, and it says which â€” see SensorService.
        new StageOption("COMPASS", "Heading, attitude, G", () => new CompassStageOccupant(signals, sensors)),

        // Android Auto and CarPlay through a Carlinkit dongle (ADR-0019). The dongle is
        // chosen and not bought, so this runs against a synthetic one and says so.
        new StageOption("PHONE", "Android Auto Â· CarPlay", () => new PhoneLinkStageOccupant(clock)),

        new StageOption(
            "VIDEO",
            videoPath is null ? "Pick a file" : System.IO.Path.GetFileName(videoPath),
            () => CreateVideo(videoPath)),

        // ── WEB: pages in WebView2 — external services, but still inside DashDeck. ─────────

        // OpenStreetMap rather than Google. Google's Maps JavaScript API terms forbid
        // in-vehicle turn-by-turn and there is no desktop SDK, so a Google map here could
        // only ever be a picture (Q18). This is a picture too â€” but an unencumbered one,
        // and the routing question stays open rather than being quietly violated.
        new StageOption("MAPS", "openstreetmap.org", () => new WebStageOccupant("MAPS", MapsUrl, display), StageKind.Web),

        // Both are web players, and both gate playback behind Widevine â€” which WebView2 does
        // not ship. The occupant probes for it and says so rather than presenting a player
        // that looks fine and refuses to make a sound.
        new StageOption("SPOTIFY", "open.spotify.com", () => new WebStageOccupant("SPOTIFY", SpotifyUrl, display), StageKind.Web),

        new StageOption("MUSIC", "music.apple.com", () => new WebStageOccupant("MUSIC", AppleMusicUrl, display), StageKind.Web),

        // ── APPS: separate Windows programs, owned and placed over the stage where that works
        //    (ADR-0020/0021), otherwise left in their own window. The built-ins below ship with
        //    DashDeck; the user's own are appended after them. ─────────────────────────────

        // The real Nuvio, not the third-party web client. app.nuvio.tv answered 526 when it
        // was wired and is not maintained by NuvioMedia; NuvioDesktop is. Listed even when it
        // is not installed, because "not installed" says more than a missing row.
        new StageOption(
            AppLaunchSpec.Nuvio.Name,
            AppLaunchSpec.Nuvio.IsInstalled ? "NuvioDesktop" : "NuvioDesktop — not installed",
            () => new AppStageOccupant(AppLaunchSpec.Nuvio),
            StageKind.App),

        // The desktop shell rather than web.stremio.com. Same reasoning as Nuvio: the real
        // application is better than a browser tab of it, and the stage can host one now.
        new StageOption(
            AppLaunchSpec.Stremio.Name,
            AppLaunchSpec.Stremio.IsInstalled ? "Stremio desktop" : "Stremio — not installed",
            () => new AppStageOccupant(AppLaunchSpec.Stremio),
            StageKind.App),

        // Development affordance: a plain Win32 window, to tell "our plumbing is wrong" from
        // "that application will not be embedded".
        new StageOption("PROBE", "Proves window adoption", () => new AppStageOccupant(AppLaunchSpec.Probe), StageKind.App),

        // The user's own apps, added through Settings (ADR-0024). Everything above is shipped in
        // code; everything here Jason pointed the tablet at himself. A launcher whose executable
        // has since moved still lists — greyed by IsAvailable is not the story here, the occupant
        // itself says "not installed" when chosen — but its detail line says so up front.
        .. (userApps ?? []).Select(app => new StageOption(
            app.Name.Trim().ToUpperInvariant(),
            app.IsInstalled ? System.IO.Path.GetFileName(app.Path) : "not found — check the path",
            () => new AppStageOccupant(AppLaunchSpec.FromUser(app)),
            StageKind.App)),
    ];

    /// <summary>Display-only map. Turn-by-turn is a separate, unanswered question (Q18).</summary>
    public const string MapsUrl = "https://www.openstreetmap.org";

    /// <summary>Spotify's web player. Needs a login, which the WebView2 profile keeps.</summary>
    public const string SpotifyUrl = "https://open.spotify.com";

    /// <summary>Apple Music on the web. Same login story, same DRM question.</summary>
    public const string AppleMusicUrl = "https://music.apple.com";

    private static IStageOccupant? CreateVideo(string? videoPath)
    {
        var path = videoPath;

        if (path is null || !System.IO.File.Exists(path))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Play on the stage",
                Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.m4v;*.webm;*.ts|Every file|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            path = dialog.FileName;
        }

        return new VideoStageOccupant(path);
    }
}

