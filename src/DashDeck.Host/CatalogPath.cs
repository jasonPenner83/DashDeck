using IoFile = System.IO.File;
using IoPath = System.IO.Path;

namespace DashDeck.Host;

/// <summary>
/// Finds the data files that travel beside the binary.
/// </summary>
/// <remarks>
/// The catalogs are data, not code (ADR-0004), so they are not embedded — a new signal is
/// JSON, never a recompile. That means they have to be <em>found</em>: a packaged build ships
/// them beside the executable and finds them on the first iteration, while running from the
/// repository they are several directories up.
/// <para>
/// Shared by the signal catalog and the sensor catalog so the two cannot disagree about where
/// they live, which is the kind of divergence that only shows up in a published build.
/// </para>
/// </remarks>
public static class CatalogPath
{
    /// <summary>Walk up from the binary looking for <c>catalog/<paramref name="fileName"/></c>.</summary>
    public static string? Find(string fileName)
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = IoPath.Combine(dir, "catalog", fileName);

            if (IoFile.Exists(candidate))
            {
                return candidate;
            }

            dir = IoPath.GetDirectoryName(dir.TrimEnd(IoPath.DirectorySeparatorChar));
        }

        return null;
    }
}
