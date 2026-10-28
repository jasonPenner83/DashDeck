using System.Globalization;
using DashDeck.Core.Updates;

namespace DashDeck.Core.Tools;

/// <summary>One of the programs that ships beside the dash.</summary>
/// <param name="Name">What it is called on the About page.</param>
/// <param name="RelativePath">Where it is, from the dash's own folder.</param>
/// <param name="What">One line on what it is for.</param>
public sealed record DashTool(string Name, string RelativePath, string What);

/// <summary>
/// The tools in a release, and how the dash opens one (ADR-0079).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each wants the adapter</b>, which serves one program at a time, so the dash has to let go of
/// it first. Opening a tool closes DashDeck, and the tool starts once the dash has exited. A tool
/// started beside a running dash would find its port in use.
/// </para>
/// <para>
/// <b>Out of process for that reason.</b> A program that has exited cannot start anything, so the
/// dash writes a batch file that waits for it, as the update's swap does (ADR-0061). Nothing is
/// installed (C1): it lives in the temp folder and deletes itself.
/// </para>
/// </remarks>
public static class DashTools
{
    /// <summary>How long the script waits for the dash to exit before starting the tool anyway, in seconds.</summary>
    public const int WaitSeconds = 30;

    /// <summary>The tools <c>publish.ps1</c> puts beside <c>DashDeck.Host.exe</c>, in the order shown.</summary>
    public static IReadOnlyList<DashTool> All { get; } =
    [
        new("ID hunter", @"IdHunter\IdHunter.exe", "Finds an identifier by listening while you work a control — a door, a seat heater, the climate."),
        new("ID matcher", @"IdMatcher\IdMatcher.exe", "Pairs DashDeck's signals with FORScan's live data through the tap, and listens to the bus."),
        new("Serial tap", @"SerialTap\SerialTap.exe", "Sits between FORScan and the adapter and records what FORScan asks."),
    ];

    /// <summary>
    /// The batch file that starts a tool once the dash with <paramref name="processId"/> has exited.
    /// </summary>
    /// <remarks>
    /// The wait ends after <see cref="WaitSeconds"/> whatever happens, and the tool starts then: a
    /// tool saying its port is in use is better than a tablet with nothing open on it.
    /// </remarks>
    public static string LaunchScript(int processId, string executablePath)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var exe = SwapScript.Quote(executablePath);
        // By hand rather than Path.GetDirectoryName, which on the build machine does not treat a
        // backslash as a separator.
        var cut = executablePath.LastIndexOfAny(['\\', '/']);
        ArgumentOutOfRangeException.ThrowIfLessThan(cut, 1, nameof(executablePath));
        var folder = SwapScript.Quote(executablePath[..cut]);

        return string.Create(CultureInfo.InvariantCulture, $"""
            @echo off
            rem Opens a DashDeck tool once the dash has let go of the adapter. Written by the dash,
            rem deletes itself. Nothing is installed: no service, no task, no registry.
            cd /d "%TEMP%"

            set /a tries=0
            :wait
            tasklist /FI "PID eq {processId}" 2>nul | find "{processId}" >nul
            if errorlevel 1 goto gone
            set /a tries+=1
            if %tries% GEQ {WaitSeconds} goto gone
            timeout /t 1 /nobreak >nul
            goto wait

            :gone
            start "" /D {folder} {exe}

            rem Deleting the running batch file works: cmd has already read the line.
            del "%~f0"
            exit /b 0
            """).ReplaceLineEndings("\r\n");
    }
}
