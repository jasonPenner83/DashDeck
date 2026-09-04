using System.IO;

namespace DashDeck.Host.Components;

/// <summary>
/// Finds the <c>plugins/</c> folder, wherever the app is running from.
/// </summary>
/// <remarks>
/// The same problem the catalogs have (see <see cref="CatalogPath"/>): a packaged build ships
/// <c>plugins/</c> beside the executable and finds it at once, while running from the
/// repository it is several directories up. Walking up rather than assuming keeps the shell
/// runnable both ways, which is what the screenshot and test affordances rely on.
/// </remarks>
public static class PluginPath
{
    /// <summary>Walk up from the binary looking for a <c>plugins</c> directory.</summary>
    public static string? FindRoot()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "plugins");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}
