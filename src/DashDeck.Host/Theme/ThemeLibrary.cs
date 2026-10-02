using System.IO;

namespace DashDeck.Host.Theme;

/// <summary>A theme file that could not be read, and why.</summary>
public sealed record ThemeLoadProblem(string File, string Reason);

/// <summary>
/// Every theme on the tablet: the shipped ones and the user's own (ADR-0036).
/// </summary>
/// <remarks>
/// Like Home Assistant's <c>themes/</c> folder: a theme is a file, so sharing one is copying a
/// file, and <see cref="Reload"/> picks up an edit made by hand without a restart. Shipped themes
/// sit beside the executable in <c>catalog/themes/</c>, which a deploy rewrites, so they are never
/// written to; the user's live in <c>%LOCALAPPDATA%\DashDeck\themes\</c>, which a deploy never
/// touches.
/// <para>
/// <b>Nothing here throws</b> for a bad file: it is left out of the list and reported, and the
/// built-in DashDeck look is always there to fall back on.
/// </para>
/// </remarks>
public sealed class ThemeLibrary
{
    /// <summary>Font files a theme may carry. Anything else in <c>fontFiles</c> is refused.</summary>
    private static readonly string[] FontExtensions = [".ttf", ".otf"];

    public ThemeLibrary(string? shippedFolder, string userFolder)
    {
        ShippedFolder = shippedFolder;
        UserFolder = userFolder;
        Reload();
    }

    /// <summary>The shipped themes' folder, or null when it could not be found.</summary>
    public string? ShippedFolder { get; }

    /// <summary>Where the user's themes are kept. Created when the first one is saved.</summary>
    public string UserFolder { get; }

    /// <summary>The built-in look, then shipped themes, then yours, each alphabetical.</summary>
    public IReadOnlyList<ThemeDefinition> Themes { get; private set; } = [];

    /// <summary>Files that could not be read at the last load.</summary>
    public IReadOnlyList<ThemeLoadProblem> Problems { get; private set; } = [];

    /// <summary>Read both folders again — the same as HA's "reload themes".</summary>
    public void Reload()
    {
        var themes = new List<ThemeDefinition> { ThemeDefinition.BuiltIn };
        var problems = new List<ThemeLoadProblem>();

        themes.AddRange(LoadFolder(ShippedFolder, ThemeOrigin.Shipped, problems));
        themes.AddRange(LoadFolder(UserFolder, ThemeOrigin.Yours, problems));

        Themes = themes;
        Problems = problems;
    }

    /// <summary>The theme with this id, or null.</summary>
    public ThemeDefinition? Find(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ThemeDefinition> LoadFolder(string? folder, ThemeOrigin origin, List<ThemeLoadProblem> problems)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        var themes = new List<ThemeDefinition>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                themes.Add(ThemeDefinition.Parse(File.ReadAllText(path), path, origin));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new ThemeLoadProblem(Path.GetFileName(path), ex.Message));
            }
        }

        return themes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ── Changing the user's folder ────────────────────────────────────────────

    /// <summary>
    /// Copy a theme file — and the font files it names, from beside it — into the user's folder.
    /// </summary>
    /// <returns>The imported theme, as it now reads from the user's folder.</returns>
    /// <exception cref="InvalidDataException">The file is not a theme, or names a font file that is not there or not a font.</exception>
    /// <exception cref="IOException">It could not be copied.</exception>
    public ThemeDefinition Import(string sourcePath)
    {
        var theme = ThemeDefinition.Parse(File.ReadAllText(sourcePath), sourcePath);
        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;

        var fonts = theme.FontFiles.Select(f => FontPath(sourceFolder, f)).ToList();

        Directory.CreateDirectory(UserFolder);
        var target = UniquePath(Slug(theme.Name));

        foreach (var font in fonts)
        {
            File.Copy(font, Path.Combine(UserFolder, Path.GetFileName(font)), overwrite: true);
        }

        File.Copy(sourcePath, target);
        Reload();
        return Themes.Single(t => string.Equals(t.FilePath, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Write a theme — and its font files — into a folder, to share or back up.
    /// </summary>
    /// <returns>The path of the theme file written.</returns>
    public string Export(ThemeDefinition theme, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);

        if (theme.Folder is { } folder)
        {
            foreach (var font in theme.FontFiles)
            {
                var source = FontPath(folder, font);
                File.Copy(source, Path.Combine(targetFolder, Path.GetFileName(source)), overwrite: true);
            }
        }

        var path = Path.Combine(targetFolder, Slug(theme.Name) + ".json");
        File.WriteAllText(path, theme.ToJson());
        return path;
    }

    /// <summary>
    /// Copy a theme into the user's folder under a new name — the way to start one of your own
    /// from one you like, then edit the file by hand.
    /// </summary>
    public ThemeDefinition SaveAs(ThemeDefinition theme, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("a theme needs a name", nameof(name));
        }

        Directory.CreateDirectory(UserFolder);

        if (theme.Folder is { } folder)
        {
            foreach (var font in theme.FontFiles)
            {
                var source = FontPath(folder, font);
                var copy = Path.Combine(UserFolder, Path.GetFileName(source));
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, copy, overwrite: true);
                }
            }
        }

        var target = UniquePath(Slug(name));
        File.WriteAllText(target, (theme with { Name = name.Trim() }).ToJson());
        Reload();
        return Themes.Single(t => string.Equals(t.FilePath, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Delete one of the user's themes. Shipped and built-in ones cannot be.</summary>
    /// <remarks>Font files are left: another theme of yours may use the same one.</remarks>
    public bool Delete(ThemeDefinition theme)
    {
        if (theme.Origin is not ThemeOrigin.Yours || theme.FilePath is null || !File.Exists(theme.FilePath))
        {
            return false;
        }

        File.Delete(theme.FilePath);
        Reload();
        return true;
    }

    /// <summary>
    /// A font file named by a theme, refused unless it is a font file in that same folder —
    /// a theme from somewhere else must not be able to name <c>..\..\anything</c>.
    /// </summary>
    private static string FontPath(string folder, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) ||
            !FontExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' is not a font file beside the theme (.ttf or .otf, no folders)");
        }

        var path = Path.Combine(folder, fileName);
        return File.Exists(path)
            ? path
            : throw new InvalidDataException($"the theme names '{fileName}', which is not beside it");
    }

    private string UniquePath(string slug)
    {
        var path = Path.Combine(UserFolder, slug + ".json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(UserFolder, $"{slug}-{n}.json");
        }

        return path;
    }

    /// <summary>A file name from a theme name: "LCARS (inspired)" → <c>lcars-inspired</c>.</summary>
    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 0 ? slug : "theme";
    }
}
