using System.Globalization;

namespace DashDeck.Core.Diagnostics;

/// <summary>
/// Whether a launch has taken so long that it is stuck rather than slow, and what to say about it.
/// </summary>
/// <remarks>
/// <b>Written because a launch hung with no window and nothing in the log</b>, which turned into
/// two separate problems at once: nobody could tell what it was doing, and the stuck process went
/// on holding the single-instance lock, so every launch after it failed too. A dash that cannot
/// start must end itself and leave a note.
/// <para>
/// The budget is deliberately generous. A cold start on a Surface Pro 7 loads a self-contained
/// runtime, the native VLC plugins, the component host and the theme before it draws anything,
/// and the adapter search opens serial ports that may not answer. Slow is normal; a minute is
/// not, and the difference matters because ending a launch that was merely slow is its own fault.
/// </para>
/// </remarks>
public static class StartupWatch
{
    /// <summary>How long a launch gets to put a window on screen before it is treated as stuck.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    /// <summary>Whether to give up on a launch that has been running this long without a window.</summary>
    public static bool ShouldGiveUp(TimeSpan elapsed, TimeSpan? budget = null) =>
        elapsed >= (budget ?? Budget);

    /// <summary>
    /// What to write when a launch is abandoned: the phase it never finished, and how long it
    /// spent there. The phase is the whole value of this — "stuck" is not a diagnosis.
    /// </summary>
    public static string Describe(string? phase, TimeSpan elapsed) => string.Create(
        CultureInfo.InvariantCulture,
        $"startup gave up after {elapsed.TotalSeconds:0} s, still in: {Phase(phase)}");

    /// <summary>One line for the startup log as a phase begins.</summary>
    public static string Line(string? phase, TimeSpan elapsed) => string.Create(
        CultureInfo.InvariantCulture,
        $"{elapsed.TotalMilliseconds,8:0} ms  {Phase(phase)}");

    private static string Phase(string? phase) =>
        string.IsNullOrWhiteSpace(phase) ? "(no phase recorded)" : phase.Trim();
}
