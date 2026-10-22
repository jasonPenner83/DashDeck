namespace DashDeck.Core.Phone;

/// <summary>Reading what ADB says (ADR-0076).</summary>
public static class AdbAnswers
{
    /// <summary>
    /// True when a shell command such as <c>wm size</c> was done: it exited cleanly and ADB printed no
    /// failure of its own — <c>adb.exe: device offline</c>, <c>error: device not found</c> — nor Android
    /// an exception. Those commands print nothing when they work.
    /// </summary>
    public static bool Done(int exitCode, string output) =>
        exitCode == 0
        && !(output ?? "").Contains("adb.exe:", StringComparison.Ordinal)
        && !(output ?? "").Contains("error:", StringComparison.OrdinalIgnoreCase)
        && !(output ?? "").Contains("Exception", StringComparison.Ordinal);

    /// <summary>The first line ADB said — why a command failed — without its own prefixes.</summary>
    public static string State(string output)
    {
        var line = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length == 0 ? "no answer" : line.Replace("adb.exe: ", "", StringComparison.Ordinal).Replace("error: ", "", StringComparison.Ordinal);
    }
}
