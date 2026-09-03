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
public sealed record StageOption(string Name, string Detail, Func<IStageOccupant?>? Create)
{
    /// <summary>False for the placeholders â€” listed, but not choosable.</summary>
    public bool IsAvailable => Create is not null;

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
    public static IReadOnlyList<StageOption> All(
        string? videoPath,
        IClock clock,
        IVehicleSignals signals,
        SensorService sensors,
        WeatherService weather,
        DisplaySettings display) =>
    [
        // The idle stage, and the default. There is no "nothing" option any more: an empty
        // stage announcing its own emptiness was honest but useless, and a clock is the
        // thing most often glanced at anyway.
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

        // The real Nuvio, not the third-party web client. app.nuvio.tv answered 526 when it
        // was wired and is not maintained by NuvioMedia; NuvioDesktop is. Listed even when it
        // is not installed, because "not installed" says more than a missing row.
        new StageOption(
            AppLaunchSpec.Nuvio.Name,
            AppLaunchSpec.Nuvio.IsInstalled ? "NuvioDesktop" : "NuvioDesktop — not installed",
            () => new AppStageOccupant(AppLaunchSpec.Nuvio)),

        // Development affordance: a plain Win32 window, to tell "our plumbing is wrong" from
        // "that application will not be embedded".
        new StageOption("PROBE", "Proves window adoption", () => new AppStageOccupant(AppLaunchSpec.Probe)),

        // OpenStreetMap rather than Google. Google's Maps JavaScript API terms forbid
        // in-vehicle turn-by-turn and there is no desktop SDK, so a Google map here could
        // only ever be a picture (Q18). This is a picture too â€” but an unencumbered one,
        // and the routing question stays open rather than being quietly violated.
        new StageOption("MAPS", "openstreetmap.org", () => new WebStageOccupant("MAPS", MapsUrl, display)),

        new StageOption("STREMIO", "web.stremio.com", () => new WebStageOccupant("STREMIO", StremioUrl, display)),

        // Both are web players, and both gate playback behind Widevine â€” which WebView2 does
        // not ship. The occupant probes for it and says so rather than presenting a player
        // that looks fine and refuses to make a sound.
        new StageOption("SPOTIFY", "open.spotify.com", () => new WebStageOccupant("SPOTIFY", SpotifyUrl, display)),

        new StageOption("MUSIC", "music.apple.com", () => new WebStageOccupant("MUSIC", AppleMusicUrl, display)),
    ];

    /// <summary>Nuvio's web build. Changed here, not hunted through the code.</summary>
    public const string NuvioUrl = "https://app.nuvio.tv";

    /// <summary>Display-only map. Turn-by-turn is a separate, unanswered question (Q18).</summary>
    public const string MapsUrl = "https://www.openstreetmap.org";

    /// <summary>Stremio's official web player. Answered 200 when wired, unlike Nuvio's.</summary>
    public const string StremioUrl = "https://web.stremio.com/";

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

