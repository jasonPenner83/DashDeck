using System.IO;
using System.Text.Json;

namespace DashDeck.Host.Settings;

/// <summary>
/// Reading and writing the small JSON files DashDeck keeps beside itself.
/// </summary>
/// <remarks>
/// <b>Nothing here throws.</b> Every caller is a preference of some kind, and losing a
/// preference is never worth failing to start a dash over — a corrupt or half-written file
/// gives back nothing and a recorded reason. The one thing that must not happen is a
/// vehicle display that will not come up because a colour could not be parsed.
/// <para>
/// Shared by <see cref="SettingsStore"/> and <c>DashboardStore</c> so the two cannot drift
/// apart on the part that matters: where the files live, and the promise that a file
/// written by another build still loads.
/// </para>
/// </remarks>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// A path in <c>%LOCALAPPDATA%\DashDeck\</c>.
    /// </summary>
    /// <remarks>
    /// Where these files live is the decision, not an implementation detail. The application
    /// is a self-contained folder that <c>publish.ps1</c> <b>deletes and rewrites</b> on
    /// every build, so anything stored beside the executable would be destroyed by the next
    /// update. This folder is outside it — and it is still constraint C1 working as
    /// intended, since uninstalling remains deleting a folder and nothing touches the
    /// registry.
    /// </remarks>
    public static string InLocalAppData(string fileName) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashDeck",
        fileName);

    /// <summary>Read a file, or <see langword="null"/> if it is absent or unreadable.</summary>
    public static T? Load<T>(string path, out string? error)
        where T : class
    {
        error = null;

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Write a file. Reports why rather than failing.</summary>
    public static void Save<T>(string path, T value, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
