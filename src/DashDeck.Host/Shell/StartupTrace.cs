using System.Diagnostics;
using System.IO;
using System.Threading;
using DashDeck.Core.Diagnostics;

namespace DashDeck.Host.Shell;

/// <summary>
/// A breadcrumb trail through startup, and a watchdog that ends a launch which never arrives.
/// </summary>
/// <remarks>
/// <b>The failure this exists for:</b> a launch on the Surface hung before the window appeared,
/// wrote nothing, and then — because it was still running — held the single-instance lock, so
/// every later launch waited twenty seconds and exited silently too. One stuck start made the dash
/// unopenable until someone found the process in Task Manager.
/// <para>
/// So: each phase of <c>OnStartup</c> names itself here, the trail is written to
/// <c>startup.log</c> as it goes (fresh each launch, so it is always the last attempt), and a
/// background thread ends the process if no window has appeared within
/// <see cref="StartupWatch.Budget"/> — naming the phase it never finished. Ending it is the part
/// that matters most: a dash that cannot start must not become the reason the next one cannot.
/// </para>
/// <para>
/// Writing a file on every launch is the cost, and it is the right trade: this is the one log that
/// is read when there is nothing on screen to read instead.
/// </para>
/// </remarks>
internal static class StartupTrace
{
    private static readonly Stopwatch Watch = new();
    private static readonly object Gate = new();

    private static string? _phase;
    private static string? _path;
    private static bool _ready;

    /// <summary>Where the trail is written.</summary>
    public static string Path => _path ??= System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashDeck",
        "startup.log");

    /// <summary>Begin the trail and arm the watchdog. Called first thing in <c>OnStartup</c>.</summary>
    public static void Begin(IEnumerable<string> args, Action<string, Exception> fail)
    {
        Watch.Restart();
        _ready = false;

        Write($"launch {DateTimeOffset.Now:O}  pid {Environment.ProcessId}", fresh: true);
        Write($"args: {string.Join(' ', args)}");

        new Thread(() => Guard(fail))
        {
            IsBackground = true,
            Name = "DashDeck startup watchdog",
        }.Start();
    }

    /// <summary>Name the phase about to run. Cheap enough to call liberally.</summary>
    public static void Phase(string phase)
    {
        lock (Gate)
        {
            _phase = phase;
        }

        Write(StartupWatch.Line(phase, Watch.Elapsed));
    }

    /// <summary>
    /// The window is up. Disarms the watchdog.
    /// </summary>
    /// <remarks>
    /// Plain ASCII in the lines on purpose: this file is read with <c>Get-Content</c> in Windows
    /// PowerShell, which decodes as ANSI and turns an em dash into mojibake. A log that is hard to
    /// read is a log that gets skipped.
    /// </remarks>
    public static void Ready()
    {
        lock (Gate)
        {
            _ready = true;
        }

        Write(StartupWatch.Line("window shown - startup finished", Watch.Elapsed));
    }

    /// <summary>
    /// End a launch that never produced a window, after writing which phase it died in.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.Exit"/> rather than <c>Shutdown</c>: the dispatcher may be the thing
    /// that is stuck, and a shutdown that needs it would hang in turn. Exit code 4 so a caller can
    /// tell this from a crash.
    /// </remarks>
    private static void Guard(Action<string, Exception> fail)
    {
        while (true)
        {
            Thread.Sleep(250);

            lock (Gate)
            {
                if (_ready)
                {
                    return;
                }

                if (!StartupWatch.ShouldGiveUp(Watch.Elapsed))
                {
                    continue;
                }
            }

            var message = StartupWatch.Describe(_phase, Watch.Elapsed);

            Write(message);
            fail("Startup", new TimeoutException(message));

            Environment.Exit(4);
        }
    }

    private static void Write(string line, bool fresh = false)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            if (fresh)
            {
                File.WriteAllText(Path, line + Environment.NewLine);
                return;
            }

            File.AppendAllText(Path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // The trail must never be the thing that stops a launch.
        }
    }
}
