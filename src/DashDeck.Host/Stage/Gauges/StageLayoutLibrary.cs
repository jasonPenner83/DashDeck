using System.IO;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>A layout file that could not be read, and why.</summary>
public sealed record LayoutLoadProblem(string File, string Reason);

/// <summary>
/// Every stage layout on the tablet: the built-in cluster, the shipped ones, and the user's own
/// (ADR-0037).
/// </summary>
/// <remarks>
/// The same shape as the theme library (ADR-0036), on purpose: shipped files beside the executable
/// in <c>catalog/stage/</c>, never written on the tablet; the user's in
/// <c>%LOCALAPPDATA%\DashDeck\stage\</c>, which a deploy never touches; RELOAD to pick up a hand
/// edit. A bad file is left out and named, and the built-in cluster is always there.
/// </remarks>
public sealed class StageLayoutLibrary
{
    public StageLayoutLibrary(string? shippedFolder, string userFolder)
    {
        ShippedFolder = shippedFolder;
        UserFolder = userFolder;
        Reload();
    }

    public string? ShippedFolder { get; }

    public string UserFolder { get; }

    /// <summary>The built-in cluster, then shipped layouts, then yours, each alphabetical.</summary>
    public IReadOnlyList<StageLayout> Layouts { get; private set; } = [];

    public IReadOnlyList<LayoutLoadProblem> Problems { get; private set; } = [];

    public void Reload()
    {
        var layouts = new List<StageLayout> { StageLayout.BuiltIn };
        var problems = new List<LayoutLoadProblem>();

        layouts.AddRange(Load(ShippedFolder, LayoutOrigin.Shipped, problems));
        layouts.AddRange(Load(UserFolder, LayoutOrigin.Yours, problems));

        Layouts = layouts;
        Problems = problems;
    }

    /// <summary>A layout by id (<c>shipped/lcars</c>), or null.</summary>
    public StageLayout? Find(string? id) =>
        Layouts.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A layout by the name a theme gives it (<c>lcars</c>) — yours first, so saving your own copy
    /// under the same file name overrides the shipped one — or an id; null when none matches.
    /// </summary>
    public StageLayout? FindForTheme(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Find(name)
            ?? Layouts.Where(l => string.Equals(l.Slug, name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.Origin is LayoutOrigin.Yours ? 0 : l.Origin is LayoutOrigin.Shipped ? 1 : 2)
                .FirstOrDefault();
    }

    private static IEnumerable<StageLayout> Load(string? folder, LayoutOrigin origin, List<LayoutLoadProblem> problems)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        var layouts = new List<StageLayout>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                layouts.Add(StageLayout.Parse(File.ReadAllText(path), path, origin));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new LayoutLoadProblem(Path.GetFileName(path), ex.Message));
            }
        }

        return layouts.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy a layout into the user's folder under a new file name — the way to start your own,
    /// then edit it by hand. Saving as the same name as a shipped layout overrides it for themes.
    /// </summary>
    public StageLayout SaveAs(StageLayout layout, string fileName)
    {
        var slug = Theme.ThemeLibrary.Slug(fileName);
        Directory.CreateDirectory(UserFolder);

        var path = Path.Combine(UserFolder, slug + ".json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(UserFolder, $"{slug}-{n}.json");
        }

        File.WriteAllText(path, (layout with { Name = layout.Origin is LayoutOrigin.Yours ? layout.Name : $"{layout.Name} (mine)" }).ToJson());
        Reload();
        return Layouts.Single(l => string.Equals(l.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Delete one of the user's layouts. Built-in and shipped ones cannot be.</summary>
    public bool Delete(StageLayout layout)
    {
        if (layout.Origin is not LayoutOrigin.Yours || layout.FilePath is null || !File.Exists(layout.FilePath))
        {
            return false;
        }

        File.Delete(layout.FilePath);
        Reload();
        return true;
    }
}
