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

    /// <summary>Clearing the stage. Always available; the empty stage is a real state.</summary>
    public static StageOption Nothing { get; } =
        new("NOTHING", "Give the bands back to the widgets", () => null);

    /// <summary>
    /// Build the list the picker shows.
    /// </summary>
    /// <param name="videoPath">
    /// A file from <c>--video</c>, if one was given. Without it the video option still
    /// works — it just asks which file when chosen.
    /// </param>
    public static IReadOnlyList<StageOption> All(string? videoPath) =>
    [
        Nothing,

        new StageOption(
            "VIDEO",
            videoPath is null ? "Pick a file" : System.IO.Path.GetFileName(videoPath),
            () => CreateVideo(videoPath)),

        // Listed deliberately. NuvioWeb 1.0.3 exists, so this becomes a WebView2 occupant
        // rather than anything exotic — the same mechanism a map would use (Q18).
        new StageOption("NUVIO", "Not built yet", null),
        new StageOption("MAPS", "Not built yet", null),
    ];

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
