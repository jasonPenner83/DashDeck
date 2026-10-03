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

    // ── Examples ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reference copies: <c>themes\examples\</c> beside your themes. Not loaded — only files
    /// directly in <see cref="UserFolder"/> are themes.
    /// </summary>
    public string ExamplesFolder => Path.Combine(UserFolder, "examples");

    /// <summary>
    /// Write every theme DashDeck ships into <see cref="ExamplesFolder"/>, to read and copy from:
    /// the shipped files exactly as they are, with the font files and licences beside them, and the
    /// built-in DashDeck look written out with <em>every</em> token set — the full vocabulary with
    /// its default values, in one file.
    /// </summary>
    /// <remarks>
    /// Rewritten whenever they differ from the running build, so an edit there is lost — the README
    /// says to copy one up a folder first. Never throws.
    /// </remarks>
    /// <returns>Why they could not be written, or null.</returns>
    public string? WriteExamples()
    {
        try
        {
            Directory.CreateDirectory(ExamplesFolder);

            WriteText("README.txt", ExamplesReadme);
            WriteText("modern.json", BuiltInHeader + EveryToken().ToJson() + Environment.NewLine);

            if (ShippedFolder is not null && Directory.Exists(ShippedFolder))
            {
                foreach (var path in Directory.EnumerateFiles(ShippedFolder))
                {
                    var extension = Path.GetExtension(path);
                    if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(Path.GetFileName(path), File.ReadAllText(path));
                    }
                    else if (FontExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        var target = Path.Combine(ExamplesFolder, Path.GetFileName(path));
                        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(path).Length)
                        {
                            File.Copy(path, target, overwrite: true);
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        void WriteText(string name, string content)
        {
            var target = Path.Combine(ExamplesFolder, name);
            if (!File.Exists(target) || File.ReadAllText(target) != content)
            {
                File.WriteAllText(target, content);
            }
        }
    }

    /// <summary>The Modern look with every token written out, day and night, as a theme file.</summary>
    private static ThemeDefinition EveryToken()
    {
        var day = ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: false);
        var night = ThemeResolver.Resolve(ThemeDefinition.BuiltIn, night: true);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var nights = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var token in ThemeTokens.All)
        {
            tokens[token.Key] = token.Kind switch
            {
                TokenKind.Colour => day.Colour(token.Key).ToString(),
                TokenKind.Number => day.Number(token.Key).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => day.Fonts[token.Key],
            };

            // Night values wherever dimming the day value would not give the same colour — the
            // hand-tuned surfaces and text, and tokens that follow another — so a copy of this file
            // looks the same after dark.
            if (token.Kind is TokenKind.Colour &&
                night.Colour(token.Key) != day.Colour(token.Key).Dim(ThemeTokens.Factor(token.Night)))
            {
                nights[token.Key] = night.Colour(token.Key).ToString();
            }
        }

        return ThemeDefinition.BuiltIn with { Name = "Modern (every token)", Tokens = tokens, Night = nights };
    }

    private const string BuiltInHeader = """
        // The built-in Modern look, written out with EVERY token set (ADR-0036, ADR-0043).
        // DashDeck draws this theme from code, so this copy is only a reference: the whole vocabulary
        // in one file. Editing it here changes nothing, and it is put back at the next launch.
        // To make your own: copy it up one folder (into themes\), rename it, change what you like,
        // and press RELOAD in Settings ▸ Themes. Anything you delete falls back to the default.
        // Every token is explained in docs/writing-a-theme.md.

        """;

    private const string ExamplesReadme = """
        DashDeck theme examples
        =======================

        These are the themes that ship with DashDeck, kept here as references:

          modern.json             the built-in Modern look, with every token written out
          glass.json              the Glass theme

        LCARS (inspired) is not a shipped theme any more: it was put in your own themes folder
        the first time this version ran, as yours to edit or delete. A spare copy is in
        DashDeck's own folder under catalog\extras\lcars.

        They are NOT loaded from this folder, and they are rewritten every time DashDeck starts,
        so any change made here is lost. To use one as a starting point:

          1. Copy the .json - and any .ttf files it lists under "fontFiles" - up one folder,
             into  ...\DashDeck\themes\
          2. Change its "name" so you can tell it apart in the list.
          3. Edit it in Notepad and save.
          4. In DashDeck: Settings > Themes > RELOAD, then tap it to wear it.

        Problems with the file are listed at the top of Settings > Themes. A bad value costs that
        one token; it never stops the dash starting.

        """;

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
