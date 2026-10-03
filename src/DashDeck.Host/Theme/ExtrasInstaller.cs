using System.IO;

namespace DashDeck.Host.Theme;

/// <summary>
/// Puts DashDeck's optional extras — LCARS (inspired) so far — into the user's own folders, once
/// (ADR-0043).
/// </summary>
/// <remarks>
/// An extra is a folder under <c>catalog/extras/</c> holding any of <c>themes/</c>, <c>stage/</c>,
/// <c>climate/</c> and <c>console/</c>. The first launch that sees it copies those files into the
/// matching folders in <c>%LOCALAPPDATA%\DashDeck\</c>, as if they had been imported: from then on
/// they are the user's — editable, exportable, deletable — and a deploy never touches them. A file of
/// the same name already there is left alone, and an extra is installed only once, so deleting it
/// keeps it deleted. Its spare copy stays in <c>catalog\extras</c> beside the executable.
/// </remarks>
public static class ExtrasInstaller
{
    /// <summary>The folders an extra can carry, each copied to the user folder of the same name.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["themes", "stage", "climate", "console"];

    /// <summary>Install every extra not installed before.</summary>
    /// <param name="extrasRoot">The <c>catalog/extras</c> folder, or null when it is not there.</param>
    /// <param name="userFolder">The user's folder for a kind: <c>themes</c> → <c>…\DashDeck\themes</c>.</param>
    /// <param name="already">Extras installed before, by folder name.</param>
    /// <returns>The extras installed now, and why any file could not be copied.</returns>
    public static (IReadOnlyList<string> Installed, IReadOnlyList<string> Problems) InstallNew(
        string? extrasRoot, Func<string, string> userFolder, IReadOnlyCollection<string> already)
    {
        var installed = new List<string>();
        var problems = new List<string>();

        if (extrasRoot is null || !Directory.Exists(extrasRoot))
        {
            return (installed, problems);
        }

        foreach (var pack in Directory.EnumerateDirectories(extrasRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(pack);
            if (already.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var kind in Kinds)
                {
                    var source = Path.Combine(pack, kind);
                    if (!Directory.Exists(source))
                    {
                        continue;
                    }

                    var target = userFolder(kind);
                    Directory.CreateDirectory(target);

                    foreach (var file in Directory.EnumerateFiles(source))
                    {
                        var to = Path.Combine(target, Path.GetFileName(file));
                        if (!File.Exists(to))
                        {
                            File.Copy(file, to);
                        }
                    }
                }

                installed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name} could not be installed: {ex.Message}");
            }
        }

        return (installed, problems);
    }

    /// <summary>
    /// A stored choice that named a shipped file which has since moved to the user's folder:
    /// <c>shipped/lcars-inspired</c> → <c>yours/lcars-inspired</c>. Anything else is returned as is.
    /// </summary>
    public static string Moved(string id) =>
        id.StartsWith("shipped/", StringComparison.OrdinalIgnoreCase) ? "yours/" + id["shipped/".Length..] : id;
}
