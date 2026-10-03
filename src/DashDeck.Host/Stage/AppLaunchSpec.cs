namespace DashDeck.Host.Stage;

/// <summary>
/// A native application the stage can host.
/// </summary>
/// <remarks>
/// <b>A launcher for apps you installed, not an extension mechanism.</b> ADR-0012 chose web
/// applets for third parties precisely because native code is a trust problem — an executable
/// runs with the user's privileges and nothing here sandboxes it. This is for pointing the
/// stage at something already on the machine, and it does not widen the ecosystem story.
/// </remarks>
/// <param name="Name">Short uppercase name for the launcher.</param>
/// <param name="Detail">One line under it.</param>
/// <param name="Candidates">
/// Where the executable might be, most likely first. A list rather than a path because MSI
/// installers land in different places depending on machine and version, and an occupant that
/// only knows one of them is an occupant that says "not installed" to somebody who installed
/// it.
/// </param>
/// <param name="Arguments">Anything the app needs on its command line.</param>
public sealed record AppLaunchSpec(
    string Name,
    string Detail,
    IReadOnlyList<string> Candidates,
    string Arguments = "")
{
    /// <summary>
    /// The first candidate that exists, or null.
    /// </summary>
    /// <remarks>
    /// Environment variables are expanded here rather than in the candidate list, so a spec
    /// stays a plain description and this stays the only place that touches the file system.
    /// </remarks>
    public string? Resolve()
    {
        foreach (var candidate in Candidates)
        {
            var path = Environment.ExpandEnvironmentVariables(candidate);

            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>True when the executable is actually on this machine.</summary>
    public bool IsInstalled => Resolve() is not null;

    /// <summary>
    /// A spec for an entry in the launcher file (ADR-0038) — NUVIO, STREMIO, PROBE and anything
    /// written there by hand. The curated install paths that kept the built-ins in code are a
    /// <c>paths</c> list in the file now.
    /// </summary>
    public static AppLaunchSpec FromLauncher(Launcher.LauncherEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AppLaunchSpec(
            entry.Name,
            entry.Detail ?? "",
            [.. (entry.Paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p))],
            entry.Arguments ?? "");
    }

    /// <summary>
    /// A spec for an app the user added through the UI.
    /// </summary>
    /// <remarks>
    /// The one difference from a built-in is the candidate list: a UI-added app was browsed to,
    /// so it is a single concrete path rather than a set of likely install locations. Everything
    /// downstream — <see cref="Resolve"/>, <see cref="IsInstalled"/>, adoption, the min-size
    /// clamp — is identical, which is the whole point: a user app is not a new kind of occupant,
    /// just another <see cref="AppLaunchSpec"/> handed to the same <c>AppStageOccupant</c>.
    /// </remarks>
    public static AppLaunchSpec FromUser(UserAppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AppLaunchSpec(
            entry.Name.Trim().ToUpperInvariant(),
            string.IsNullOrEmpty(entry.Path) ? "User app" : System.IO.Path.GetFileName(entry.Path),
            [entry.Path],
            entry.Arguments);
    }
}
