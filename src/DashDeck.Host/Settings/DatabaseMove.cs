using System.Globalization;
using System.IO;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Dbc;

namespace DashDeck.Host.Settings;

/// <summary>
/// Moving the person's established signals into their own CAN databases, on disk (ADR-0078): the
/// pure part is <see cref="DbcMove"/>; this reads the databases that are there, keeps a copy of
/// every file it is about to change, and writes.
/// </summary>
public static class DatabaseMove
{
    /// <summary>What a move would do, from the store's file and the databases beside it.</summary>
    /// <exception cref="IOException">A database there would not open.</exception>
    public static DbcMoveResult Plan(UserSignalStore store, SignalCatalog shipped)
    {
        ArgumentNullException.ThrowIfNull(store);

        var folder = UserSignalStore.DatabaseFolder(store.Path);

        return DbcMove.Plan(store.Definitions, shipped, file =>
        {
            var path = Path.Combine(folder, file);
            return File.Exists(path) ? DbcReader.Load(path).Document : null;
        });
    }

    /// <summary>
    /// Do it: copy the signal file and each database about to change to a <c>.before-move-…bak</c>
    /// beside it, write the databases, then the signal file. Returns null when it all went, or what
    /// failed.
    /// </summary>
    /// <remarks>
    /// The databases are written first. Should the signal file then fail, the databases hold a
    /// few signals nothing refers to yet — harmless, and the next move finds them in place.
    /// </remarks>
    public static string? Apply(DbcMoveResult plan, UserSignalStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        var folder = UserSignalStore.DatabaseFolder(store.Path);
        var stamp = clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateDirectory(folder);
            Keep(store.Path, stamp);

            foreach (var (file, database) in plan.Databases)
            {
                var path = Path.Combine(folder, file);
                Keep(path, stamp);
                DbcWriter.Save(database, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Nothing was moved: {ex.Message}";
        }

        store.ReplaceAll(plan.Overlay);

        return store.LastError is { } error
            ? $"The databases were written, but your signal file was not, so the dash still reads them from it: {error}"
            : null;
    }

    /// <summary>The copy a file is kept as before a move changes it.</summary>
    public static string BackupOf(string path, string stamp) => $"{path}.before-move-{stamp}.bak";

    private static void Keep(string path, string stamp)
    {
        if (File.Exists(path))
        {
            File.Copy(path, BackupOf(path, stamp), overwrite: true);
        }
    }
}
