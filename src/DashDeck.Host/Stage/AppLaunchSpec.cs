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
    /// NuvioDesktop, if it is installed.
    /// </summary>
    /// <remarks>
    /// The reason this exists at all: the NUVIO occupant pointed at <c>app.nuvio.tv</c>, which
    /// is a third-party web client and answered 526 when it was wired. The real application is
    /// a Kotlin Multiplatform / Compose Desktop build shipped as an MSI — a JVM process with
    /// one real Win32 top-level window, which is the property that makes adopting it plausible
    /// at all. It is alpha, by its own authors' description.
    /// </remarks>
    public static AppLaunchSpec Nuvio { get; } = new(
        "NUVIO",
        "NuvioDesktop, if installed",
        [
            @"%ProgramFiles%\Nuvio\Nuvio.exe",
            @"%ProgramFiles%\NuvioDesktop\NuvioDesktop.exe",
            @"%LOCALAPPDATA%\Programs\Nuvio\Nuvio.exe",
            @"%LOCALAPPDATA%\Nuvio\Nuvio.exe",
        ]);

    /// <summary>
    /// A known-simple Win32 app, for proving the mechanism.
    /// </summary>
    /// <remarks>
    /// Character Map: a plain Win32 dialog with one top-level window and no tricks. If
    /// adoption fails on it, the problem is ours rather than the application's — and that
    /// distinction is worth a development-only occupant, because the alternative is debugging
    /// a JVM window and our own plumbing at the same time.
    /// <para>
    /// <b>Not Notepad</b>, which was the obvious choice and is wrong on Windows 11: the
    /// <c>System32</c> executable is a stub that starts the Store-packaged app in a different
    /// process and exits immediately. Ours reported a failed launch — correctly, and
    /// uselessly. Anything Store-packaged behaves the same way and cannot be adopted.
    /// </para>
    /// </remarks>
    public static AppLaunchSpec Probe { get; } = new(
        "PROBE",
        "Proves window adoption works",
        [@"%WINDIR%\System32\charmap.exe"]);
}
