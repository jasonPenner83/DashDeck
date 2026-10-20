using System.Globalization;

namespace DashDeck.Core.Updates;

/// <summary>What a swap has to know. Every path is absolute; none may be empty.</summary>
/// <param name="ProcessId">The dash's own process, which the script waits for.</param>
/// <param name="CurrentFolder">The folder being replaced.</param>
/// <param name="StagedFolder">The unpacked new copy, which moves into its place.</param>
/// <param name="PreviousFolder">Where the old folder is kept rather than deleted.</param>
/// <param name="ExecutablePath">What to start again afterwards.</param>
/// <param name="LogPath">Where the script says what it did, since nobody is watching it.</param>
public readonly record struct SwapRequest(
    int ProcessId,
    string CurrentFolder,
    string StagedFolder,
    string PreviousFolder,
    string ExecutablePath,
    string LogPath);

/// <summary>
/// The batch file that replaces the dash's folder after the dash has exited.
/// </summary>
/// <remarks>
/// <b>Out of process because it has to be.</b> Windows holds a running executable's file open, so
/// a program cannot replace its own folder from inside itself. The usual answers are a service, an
/// installer or a scheduled task — all three install something, which C1 forbids. A batch file in
/// the temp folder that waits for one process to exit, moves two folders and deletes itself
/// installs nothing and leaves nothing behind.
/// <para>
/// <b>Moves, never copies, and never deletes first.</b> A move within a volume is an entry change
/// rather than 300 MB of I/O, so the window where neither folder is in place is milliseconds
/// instead of a minute. The old folder is moved aside and kept; if the second move fails, it is
/// moved straight back, which is the whole reason the first step is not a delete.
/// </para>
/// <para>
/// <b>It stands somewhere else first.</b> cmd inherits the dash's working directory, and when the
/// dash was started from its shortcut or from Explorer that <i>is</i> the folder being replaced.
/// Windows will not move a folder a process is standing in, so the first move failed on the tablet
/// every time and the dash never came back (Q40). The script's first act is to leave.
/// </para>
/// <para>
/// <b>Every way out starts a dash</b> except the one where the dash never exited. A swap that
/// cannot happen leaves the old folder in place and opens it again, so a failed update looks like
/// nothing happened rather than like a tablet with no dash on it.
/// </para>
/// <para>
/// Generated as text, and tested as text. The order of these lines is the difference between a
/// recoverable failure and a tablet with no dash on it in a parking lot, which is worth pinning
/// in a truth table rather than trusting to a careful afternoon.
/// </para>
/// </remarks>
public static class SwapScript
{
    /// <summary>How long to wait for the dash to exit before giving up, in seconds.</summary>
    public const int WaitSeconds = 30;

    /// <summary>
    /// How many times to try moving the current folder aside, a second apart. Something can hold a
    /// file in it for a moment after the dash exits — the map's browser reads its page from there,
    /// and an antivirus may be scanning what just closed.
    /// </summary>
    public const int MoveTries = 15;

    /// <summary>Build the script. Throws when a path is missing, because a blank path in a
    /// <c>move</c> is how a batch file deletes the wrong thing.</summary>
    public static string Build(SwapRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(request.ProcessId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StagedFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PreviousFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LogPath);

        var current = Quote(request.CurrentFolder);
        var staged = Quote(request.StagedFolder);
        var previous = Quote(request.PreviousFolder);
        var exe = Quote(request.ExecutablePath);
        var log = Quote(request.LogPath);

        return string.Create(CultureInfo.InvariantCulture, $"""
            @echo off
            rem DashDeck update swap. Written by the dash, runs after it exits, deletes itself.
            rem Nothing is installed: no service, no task, no registry.

            rem Out of the dash's folder before anything else. cmd starts in the dash's working
            rem directory, which is the folder being replaced, and Windows will not move a folder
            rem a process is standing in.
            cd /d "%TEMP%"

            echo swap started %DATE% %TIME%> {log}

            rem Wait for the dash to let go of its own files. Without this the move fails.
            set /a tries=0
            :wait
            tasklist /FI "PID eq {request.ProcessId}" 2>nul | find "{request.ProcessId}" >nul
            if errorlevel 1 goto gone
            set /a tries+=1
            if %tries% GEQ {WaitSeconds} goto stillrunning
            timeout /t 1 /nobreak >nul
            goto wait

            :stillrunning
            echo DashDeck did not exit within {WaitSeconds}s - nothing was changed>> {log}
            exit /b 1

            :gone
            rem The previous previous copy is the only thing deleted, and only once the new one
            rem has arrived to replace it. One generation back is kept, not two.
            if exist {previous} rd /s /q {previous}

            rem If it is still there, a move would put the current folder inside it rather than
            rem beside it, and the rollback would then restore the wrong thing.
            if exist {previous} (
                echo could not clear the previous copy - nothing was changed>> {log}
                goto restart
            )

            set /a tries=0
            :aside
            move {current} {previous} >> {log} 2>&1
            if not errorlevel 1 goto moved
            set /a tries+=1
            if %tries% GEQ {MoveTries} goto stuck
            timeout /t 1 /nobreak >nul
            goto aside

            :stuck
            echo could not move the current folder aside after {MoveTries} tries - nothing was changed>> {log}
            goto restart

            :moved
            move {staged} {current} >> {log} 2>&1
            if errorlevel 1 (
                echo could not move the new folder in - putting the old one back>> {log}
                move {previous} {current} >> {log} 2>&1
                goto restart
            )

            echo swapped, starting {exe}>> {log}
            start "" /D {current} {exe}

            rem Deleting the running batch file works: cmd has already read the line.
            del "%~f0"
            exit /b 0

            :restart
            rem The swap did not happen, so the folder in place is the one that was running.
            rem Open it again rather than leave the tablet with no dash on it.
            echo starting the version that was already there>> {log}
            start "" /D {current} {exe}
            exit /b 1
            """).ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// Double quotes, always, whether or not the path has a space in it.
    /// </summary>
    /// <remarks>
    /// The tablet's own deploy lives under <c>C:\Users\ABC\Documents\app\DashDeck</c> — and a
    /// <c>Documents</c> with a space two folders up is exactly how an unquoted <c>move</c> in a
    /// batch file goes somewhere nobody intended.
    /// </remarks>
    public static string Quote(string path) => $"\"{path.TrimEnd('\\', '/')}\"";
}
