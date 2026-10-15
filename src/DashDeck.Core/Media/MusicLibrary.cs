namespace DashDeck.Core.Media;

/// <summary>One playable file, as little as is needed to show it on a card.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Title">What to show. The file name without its extension, until tags are read.</param>
/// <param name="Folder">The containing folder's name, which stands in for an album.</param>
public readonly record struct Track(string Path, string Title, string Folder);

/// <summary>
/// What DashDeck can play: the audio files under one folder, in a stable order.
/// </summary>
/// <remarks>
/// <b>Files on the tablet, not a service.</b> The dash is launched by hand in a truck that may have
/// no signal (C5), so the one source that always works is what is already on the disk. Streaming
/// services need accounts, tokens and a network, and each is its own component later if wanted.
/// <para>
/// Pure and platform-neutral: it takes the file names it is given rather than reaching for a disk,
/// so the ordering and the queue arithmetic are tested without one.
/// </para>
/// </remarks>
public static class MusicLibrary
{
    /// <summary>Extensions taken as playable.</summary>
    /// <remarks>
    /// What LibVLC handles without argument, which is the point of bundling it (ADR-0019's
    /// reasoning for the video occupant applies unchanged to audio). Deliberately not an
    /// exhaustive list of what it <i>can</i> open — these are the ones a person actually owns.
    /// </remarks>
    public static readonly string[] Extensions =
        [".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];

    /// <summary>Both separators, because a path is parsed the same wherever this runs.</summary>
    /// <remarks>
    /// <see cref="Path"/>'s own helpers follow the <i>host's</i> separator, so on Linux
    /// <c>GetFileName</c> of a Windows path returns the whole string — which made this quietly
    /// wrong in a project whose whole point is being platform-neutral, and showed up the moment
    /// the tests ran anywhere but Windows. Paths come from a Windows tablet; they are split here
    /// rather than deferred to whatever is underneath.
    /// </remarks>
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Whether a path looks playable by its extension.</summary>
    public static bool IsPlayable(string path)
    {
        var name = LastSegment(path);
        var dot = name.LastIndexOf('.');
        return dot >= 0 && Extensions.Contains(name[dot..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The file name, with either separator.</summary>
    private static string LastSegment(string path)
    {
        var cut = path.LastIndexOfAny(Separators);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    /// <summary>The containing folder's name, with either separator.</summary>
    private static string FolderName(string path)
    {
        var cut = path.LastIndexOfAny(Separators);
        if (cut <= 0)
        {
            return string.Empty;
        }

        var parent = path[..cut];
        return LastSegment(parent);
    }

    /// <summary>The file name without its extension.</summary>
    private static string TitleOf(string path)
    {
        var name = LastSegment(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// Turn a set of file paths into tracks, in the order they should play.
    /// </summary>
    /// <remarks>
    /// Sorted by folder then by file name, with <b>ordinal</b> comparison so the order does not
    /// shift with the machine's locale, and so a run of numbered files sorts the way a person
    /// numbered them. Unplayable files are dropped rather than shown greyed out: a folder of
    /// photos alongside the music should not fill the queue with things that cannot play.
    /// </remarks>
    public static IReadOnlyList<Track> From(IEnumerable<string> paths) =>
    [
        .. paths
            .Where(IsPlayable)
            .Select(p => new Track(p, TitleOf(p), FolderName(p)))
            .OrderBy(t => t.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Title, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The next index in a queue, wrapping at the end.
    /// </summary>
    /// <remarks>
    /// Wrapping rather than stopping, because the alternative in a truck is silence that needs a
    /// deliberate tap to end, and the tap is the thing worth avoiding while driving.
    /// </remarks>
    public static int Next(int current, int count) =>
        count <= 0 ? -1 : (((current + 1) % count) + count) % count;

    /// <summary>The previous index, wrapping at the start.</summary>
    public static int Previous(int current, int count) =>
        count <= 0 ? -1 : (((current - 1) % count) + count) % count;

    /// <summary>
    /// Where a tap on "previous" should actually go.
    /// </summary>
    /// <remarks>
    /// The behaviour every music player has and nobody writes down: once a track is past its first
    /// few seconds, "previous" restarts it rather than leaving. Reaching for the start of what is
    /// playing is far more common than reaching back one, and the sooner-than-you-think cutoff is
    /// what makes a double-tap feel like "go back two".
    /// </remarks>
    public static PreviousAction PreviousFrom(TimeSpan position) =>
        position < RestartWindow ? PreviousAction.GoBack : PreviousAction.Restart;

    /// <summary>How far into a track "previous" stops meaning "restart this one".</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(3);
}

/// <summary>What a tap on "previous" means at the moment it is tapped.</summary>
public enum PreviousAction
{
    /// <summary>Go to the previous track.</summary>
    GoBack,

    /// <summary>Start the current track again.</summary>
    Restart,
}
