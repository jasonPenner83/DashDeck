using DashDeck.Abstractions;
using Microsoft.Win32;

namespace DashDeck.Host.Stage;

/// <summary>
/// One thing that can be put on the stage, as offered by the picker.
/// </summary>
/// <remarks>
/// An option is not an occupant — it is the <em>offer</em> of one. Nothing is constructed
/// until it is chosen, which matters because constructing the video occupant loads VLC.
/// Unavailable options are still listed, greyed, because "Nuvio, not built yet" tells you
/// more about where this is going than an empty row does.
/// </remarks>
/// <param name="Name">Short uppercase name, matching the stage chip.</param>
/// <param name="Detail">One line under it — what it is, or why it is unavailable.</param>
/// <param name="Create">
/// Builds the occupant, or returns <see langword="null"/> for an empty stage. A
/// <see langword="null"/> factory means the option cannot be chosen at all.
/// </param>
public sealed record StageOption(string Name, string Detail, Func<IStageOccupant?>? Create)
{
    /// <summary>False for the placeholders — listed, but not choosable.</summary>
    public bool IsAvailable => Create is not null;

    /// <summary>
    /// Build the list the launcher shows.
    /// </summary>
    /// <param name="videoPath">
    /// A file from <c>--video</c>, if one was given. Without it the video option still
    /// works — it just asks which file when chosen.
    /// </param>
    /// <param name="clock">Injected, because nothing here reads the wall clock directly.</param>
    public static IReadOnlyList<StageOption> All(string? videoPath, IClock clock) =>
    [
        // The idle stage, and the default. There is no "nothing" option any more: an empty
        // stage announcing its own emptiness was honest but useless, and a clock is the
        // thing most often glanced at anyway.
        new StageOption("CLOCK", "Time and weather", () => new ClockWeatherStageOccupant(clock)),

        new StageOption(
            "VIDEO",
            videoPath is null ? "Pick a file" : System.IO.Path.GetFileName(videoPath),
            () => CreateVideo(videoPath)),

        // NuvioWeb, hosted rather than reparented. GPLv3 stays at arm's length that way
        // (Q18). Its origin answered 526 — Cloudflare's "bad origin certificate" — when
        // this was wired, so if it comes up blank that is their end, not ours.
        new StageOption("NUVIO", "app.nuvio.tv", () => new WebStageOccupant("NUVIO", NuvioUrl)),

        // OpenStreetMap rather than Google. Google's Maps JavaScript API terms forbid
        // in-vehicle turn-by-turn and there is no desktop SDK, so a Google map here could
        // only ever be a picture (Q18). This is a picture too — but an unencumbered one,
        // and the routing question stays open rather than being quietly violated.
        new StageOption("MAPS", "openstreetmap.org", () => new WebStageOccupant("MAPS", MapsUrl)),

        new StageOption("STREMIO", "web.stremio.com", () => new WebStageOccupant("STREMIO", StremioUrl)),
    ];

    /// <summary>Nuvio's web build. Changed here, not hunted through the code.</summary>
    public const string NuvioUrl = "https://app.nuvio.tv";

    /// <summary>Display-only map. Turn-by-turn is a separate, unanswered question (Q18).</summary>
    public const string MapsUrl = "https://www.openstreetmap.org";

    /// <summary>Stremio's official web player. Answered 200 when wired, unlike Nuvio's.</summary>
    public const string StremioUrl = "https://web.stremio.com/";

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
