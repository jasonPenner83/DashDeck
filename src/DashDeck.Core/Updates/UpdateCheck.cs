using System.Globalization;

namespace DashDeck.Core.Updates;

/// <summary>One file attached to a release.</summary>
/// <param name="Name">The file name, e.g. <c>DashDeck-v0.9.0-win-x64.zip</c>.</param>
/// <param name="Url">Where to download it.</param>
/// <param name="Bytes">Its size, for the screen to say before anyone commits to it.</param>
public readonly record struct ReleaseAsset(string Name, string Url, long Bytes);

/// <summary>What a check concluded. Each one needs different buttons on screen.</summary>
public enum UpdateVerdict
{
    /// <summary>Nothing has been asked yet. The resting state, because nothing asks on its own.</summary>
    NotChecked,

    /// <summary>The newest release is this one, or older.</summary>
    UpToDate,

    /// <summary>There is a newer release and a file to fetch.</summary>
    Available,

    /// <summary>Newer, but nothing attached that this machine can use.</summary>
    NoDownload,

    /// <summary>The release could not be read — offline, rate-limited, or a shape we do not know.</summary>
    Unreadable,

    /// <summary>This build cannot say what it is, so "newer" has no meaning.</summary>
    UnknownRunningVersion,
}

/// <summary>What a check found, and everything the screen needs to say about it.</summary>
public sealed record UpdateStatus(
    UpdateVerdict Verdict,
    ReleaseVersion? Running = null,
    ReleaseVersion? Latest = null,
    ReleaseAsset? Asset = null,
    DateTimeOffset? PublishedUtc = null,
    string? NotesUrl = null,
    string? Problem = null);

/// <summary>
/// Whether a newer DashDeck exists — asked, never volunteered.
/// </summary>
/// <remarks>
/// <b>Nothing here runs unless a person presses the button</b> (ADR-0061). There is no check at
/// launch, no badge, no toast, and no stored "last checked" to drive one later. A dash that
/// interrupts a drive to mention a release is worse than one that is a version behind, and the
/// person asking is the only signal that the moment is right.
/// <para>
/// The decision is pure and the fetching is not, so this takes what a release said and returns
/// what to show. The shape of GitHub's JSON lives at the edge where it can change without
/// anything here caring.
/// </para>
/// </remarks>
public static class UpdateCheck
{
    /// <summary>The asset suffix this dash can actually run.</summary>
    public const string WindowsSuffix = "-win-x64.zip";

    /// <summary>
    /// Compare what is running against what a release says, and decide what the screen offers.
    /// </summary>
    public static UpdateStatus Evaluate(
        string? runningVersion,
        string? latestTag,
        IReadOnlyList<ReleaseAsset> assets,
        DateTimeOffset? publishedUtc = null,
        string? notesUrl = null)
    {
        if (!ReleaseVersion.TryParse(runningVersion, out var running))
        {
            // A build that cannot name itself must not be told it is out of date: every
            // comparison against it would be a guess, and the guess decides whether a tablet
            // in a truck replaces its own folder.
            return new UpdateStatus(
                UpdateVerdict.UnknownRunningVersion,
                Problem: $"this build reports its version as \"{runningVersion}\"");
        }

        if (!ReleaseVersion.TryParse(latestTag, out var latest))
        {
            return new UpdateStatus(
                UpdateVerdict.Unreadable,
                Running: running,
                Problem: string.IsNullOrWhiteSpace(latestTag)
                    ? "the latest release has no tag"
                    : $"the latest release is tagged \"{latestTag}\", which is not a version");
        }

        if (!running.IsOlderThan(latest))
        {
            return new UpdateStatus(UpdateVerdict.UpToDate, running, latest, PublishedUtc: publishedUtc, NotesUrl: notesUrl);
        }

        if (ChooseAsset(assets, latest) is not { } asset)
        {
            return new UpdateStatus(
                UpdateVerdict.NoDownload,
                running,
                latest,
                PublishedUtc: publishedUtc,
                NotesUrl: notesUrl,
                Problem: $"{latest} is published with nothing ending {WindowsSuffix}");
        }

        return new UpdateStatus(UpdateVerdict.Available, running, latest, asset, publishedUtc, notesUrl);
    }

    /// <summary>
    /// The file to fetch out of what a release carries.
    /// </summary>
    /// <remarks>
    /// Matched by its suffix rather than its whole name, because the version in the middle is
    /// written differently from the tag. When a release carries more than one — a second
    /// architecture, or a re-upload — the one naming this version wins, and if that is still
    /// ambiguous nothing is chosen: downloading 334 MB of the wrong thing over a hotspot is
    /// worse than saying so.
    /// </remarks>
    public static ReleaseAsset? ChooseAsset(IReadOnlyList<ReleaseAsset> assets, ReleaseVersion version)
    {
        var usable = assets
            .Where(a => a.Name.EndsWith(WindowsSuffix, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Bytes > 0 && !string.IsNullOrWhiteSpace(a.Url))
            .ToList();

        if (usable.Count <= 1)
        {
            return usable.Count == 1 ? usable[0] : null;
        }

        var named = usable
            .Where(a => a.Name.Contains(version.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>The headline, as the screen says it.</summary>
    public static string Describe(UpdateStatus status) => status.Verdict switch
    {
        UpdateVerdict.NotChecked => "Not checked",
        UpdateVerdict.UpToDate => $"You are on {status.Running} — nothing newer",
        UpdateVerdict.Available => string.Create(
            CultureInfo.InvariantCulture,
            $"{status.Latest} is available — {Size(status.Asset!.Value.Bytes)}"),
        UpdateVerdict.NoDownload => $"{status.Latest} is published, with no file for this machine",
        UpdateVerdict.UnknownRunningVersion => "This build cannot say which version it is",
        _ => "Could not read the latest release",
    };

    /// <summary>Megabytes, which is the only unit a 300 MB download needs.</summary>
    public static string Size(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0} MB");

    /// <summary>
    /// How long ago it was published, or empty when a release does not say. Days, because the
    /// question behind it is "is this fresh or has it been sitting there".
    /// </summary>
    public static string Age(DateTimeOffset? publishedUtc, DateTimeOffset nowUtc)
    {
        if (publishedUtc is not { } published)
        {
            return "";
        }

        var days = (nowUtc - published).TotalDays;

        return days switch
        {
            < 0 => "published just now",
            < 1 => "published today",
            < 2 => "published yesterday",
            _ => string.Create(CultureInfo.InvariantCulture, $"published {days:0} days ago"),
        };
    }
}
