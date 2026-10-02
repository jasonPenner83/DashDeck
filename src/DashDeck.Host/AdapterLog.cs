using DashDeck.Abstractions;
using IoFile = System.IO.File;

namespace DashDeck.Host;

/// <summary>
/// What the adapter link did, one line at a time, in <c>%LOCALAPPDATA%\DashDeck\adapter.log</c>.
/// </summary>
/// <remarks>
/// For the tests that happen in the truck (docs/08-in-vehicle-testing.md). "It didn't reconnect"
/// is where a diagnosis starts, not where it ends: this records when the adapter was found, when
/// it dropped and with what error, and why each attempt to find it again failed — so the next
/// report can be the file rather than a memory of the screen. Kept small (rolled over at 512 KB)
/// and never allowed to fail the dash.
/// </remarks>
public static class AdapterLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>Where the log is.</summary>
    public static string Path => Settings.JsonFile.InLocalAppData("adapter.log");

    public static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                var path = Path;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

                if (IoFile.Exists(path) && new System.IO.FileInfo(path).Length > MaxBytes)
                {
                    IoFile.Move(path, path + ".old", overwrite: true);
                }

                var stamp = SystemClock.Instance.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                IoFile.AppendAllText(path, $"{stamp}  {line}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never be the thing that takes the dash down.
        }
    }
}
