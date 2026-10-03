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
    /// <param name="shippedFolder">Layouts that ship beside the executable, or null.</param>
    /// <param name="userFolder">The user's own.</param>
    /// <param name="canvas">What these layouts are drawn on — the stage unless said (ADR-0040).</param>
    /// <param name="builtIns">The compiled-in layouts, the first being the fallback. The stage's unless said.</param>
    public StageLayoutLibrary(string? shippedFolder, string userFolder, LayoutCanvas? canvas = null, IReadOnlyList<StageLayout>? builtIns = null)
    {
        ShippedFolder = shippedFolder;
        UserFolder = userFolder;
        Canvas = canvas ?? LayoutCanvas.Stage;
        BuiltIns = builtIns ?? StageLayout.BuiltIns;
        Reload();
    }

    /// <summary>What every layout in this library is drawn on.</summary>
    public LayoutCanvas Canvas { get; }

    /// <summary>The compiled-in layouts, always there.</summary>
    public IReadOnlyList<StageLayout> BuiltIns { get; }

    /// <summary>The fallback when a chosen or named layout is missing: never a blank surface.</summary>
    public StageLayout Default => BuiltIns[0];

    public string? ShippedFolder { get; }

    public string UserFolder { get; }

    /// <summary>The built-in cluster, then shipped layouts, then yours, each alphabetical.</summary>
    public IReadOnlyList<StageLayout> Layouts { get; private set; } = [];

    public IReadOnlyList<LayoutLoadProblem> Problems { get; private set; } = [];

    public void Reload()
    {
        var layouts = new List<StageLayout>(BuiltIns);
        var problems = new List<LayoutLoadProblem>();

        layouts.AddRange(Load(ShippedFolder, LayoutOrigin.Shipped, problems, Canvas));
        layouts.AddRange(Load(UserFolder, LayoutOrigin.Yours, problems, Canvas));

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

    private static IEnumerable<StageLayout> Load(string? folder, LayoutOrigin origin, List<LayoutLoadProblem> problems, LayoutCanvas canvas)
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
                layouts.Add(StageLayout.Parse(File.ReadAllText(path), path, origin, canvas));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new LayoutLoadProblem(Path.GetFileName(path), ex.Message));
            }
        }

        return layouts.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reference copies: <c>stage\examples\</c> beside your layouts. Not loaded — only the
    /// files directly in <see cref="UserFolder"/> are layouts.
    /// </summary>
    public string ExamplesFolder => Path.Combine(UserFolder, "examples");

    /// <summary>
    /// Write every layout DashDeck ships into <see cref="ExamplesFolder"/>, to read and copy from:
    /// the shipped files exactly as they are, comments and all, and the built-in F-150 cluster —
    /// which otherwise exists only in code — as JSON.
    /// </summary>
    /// <remarks>
    /// Rewritten whenever they differ, so the examples always match the build that is running; an
    /// example you edited is put back, which is why the README says to copy one out first. Never
    /// throws: examples are a convenience, and a dash must not fail to start over one.
    /// </remarks>
    /// <returns>Why they could not be written, or null.</returns>
    public string? WriteExamples()
    {
        try
        {
            Directory.CreateDirectory(ExamplesFolder);

            if (Canvas == LayoutCanvas.Console)
            {
                Write("README.txt", ConsoleReadme);
                Write("modern.json", ConsoleHeader + StageLayout.BuiltInConsoleJson + Environment.NewLine);
            }
            else if (Canvas == LayoutCanvas.Climate)
            {
                Write("README.txt", ClimateReadme);
                Write("glass.json", ClimateHeader + StageLayout.BuiltInClimateJson + Environment.NewLine);
            }
            else
            {
                Write("README.txt", ExamplesReadme);
                Write("f150-cluster.json", BuiltInHeader + StageLayout.BuiltIn.ToJson() + Environment.NewLine);
                Write("compass.json", CompassHeader + StageLayout.BuiltInCompassJson + Environment.NewLine);
            }

            if (ShippedFolder is not null && Directory.Exists(ShippedFolder))
            {
                foreach (var path in Directory.EnumerateFiles(ShippedFolder, "*.json"))
                {
                    Write(Path.GetFileName(path), File.ReadAllText(path));
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        void Write(string name, string content)
        {
            var target = Path.Combine(ExamplesFolder, name);
            if (!File.Exists(target) || File.ReadAllText(target) != content)
            {
                File.WriteAllText(target, content);
            }
        }
    }

    private const string ConsoleHeader = """
        // The built-in console, Modern, written out as a reference (ADR-0041). DashDeck draws it
        // from code, so this copy is only for reading — editing it here changes nothing, and it is
        // put back at the next launch. To make your own: copy it up one folder (into console\),
        // keep the name modern.json to replace it, or rename it and choose it in Settings ▸ Themes ▸
        // CONSOLE LAYOUT. The canvas is 912 × 390. Every field is explained in
        // docs/writing-a-stage-layout.md.

        """;

    private const string ConsoleReadme = """
        DashDeck console examples
        =========================

        The console (DASH in the bottom bar) is drawn from a layout file, the same format as the
        stage (docs/writing-a-stage-layout.md), on a canvas 912 wide and 390 tall.

          modern.json         the built-in Modern console

        These are NOT loaded from this folder, and they are rewritten every time DashDeck starts.
        To make your own: copy one up a folder into  ...\DashDeck\console\, edit it, and choose
        it in Settings > Themes > CONSOLE LAYOUT (or keep the name modern.json to replace Modern).

        Warning lights for oil pressure, seatbelt, door, brake and tyres are placeholders until
        the truck's own are found: on the truck they stay dark with a grey dot. Check engine, low
        fuel, engine temperature and battery are real.

        """;

    private const string ClimateHeader = """
        // The built-in climate panel, Glass, written out as a reference (ADR-0040). DashDeck draws it
        // from code, so this copy is only for reading — editing it here changes nothing, and it is
        // put back at the next launch. To make your own: copy it up one folder (into climate\),
        // keep the name glass.json to replace it, or rename it and choose it in Settings ▸ Themes ▸
        // CLIMATE LAYOUT. The canvas is 912 × 390. Every field is explained in
        // docs/writing-a-stage-layout.md. Read only: nothing here changes the truck's climate.

        """;

    private const string ClimateReadme = """
        DashDeck climate panel examples
        ===============================

        The climate panel (CLIMATE in the bottom bar) is drawn from a layout file, the same format
        as the stage (docs/writing-a-stage-layout.md), on a canvas 912 wide and 390 tall.

          glass.json          the built-in Glass panel

        These are NOT loaded from this folder, and they are rewritten every time DashDeck starts.
        To make your own: copy one up a folder into  ...\DashDeck\climate\, edit it, and choose
        it in Settings > Themes > CLIMATE LAYOUT (or keep the name glass.json to replace Glass).

        It only shows what the truck reports. The climate signals (hvac.*) are placeholders until
        the truck's HVAC module is found, so on the truck they read a dash; on the synthetic truck
        they are marked Simulated. Nothing here changes the truck's climate.

        """;

    private const string CompassHeader = """
        // The built-in COMPASS screen, written out as a reference (ADR-0039). DashDeck draws it from
        // code, so this copy is only for reading — editing it here changes nothing, and it is put
        // back at the next launch. To make your own: copy it up one folder (into stage\), keep the
        // name compass.json, edit it, and RELOAD STAGE LAYOUT from the three-dot menu on COMPASS.
        // A compass.json of yours replaces this one wherever the launcher's compass entry shows it.
        // Every field is explained in docs/writing-a-stage-layout.md.

        """;

    private const string BuiltInHeader = """
        // The built-in F-150 cluster, written out as a reference (ADR-0037). DashDeck draws it from
        // code, so this copy is only for reading — editing it here changes nothing, and it is put
        // back at the next launch. To make your own: copy it up one folder (into stage\), rename
        // it, and choose it in Settings ▸ Themes ▸ STAGE LAYOUT. Every field is explained in
        // docs/writing-a-stage-layout.md.

        """;

    private const string ExamplesReadme = """
        DashDeck stage layout examples
        ==============================

        These are the stage layouts that ship with DashDeck, kept here as references:

          f150-cluster.json   the built-in six-dial cluster (boost, oil, volts, intake, throttle, load)
          compass.json        the built-in COMPASS screen (heading rose, G meter, pitch, roll, speed)
          lcars.json          the LCARS (inspired) stage, worn with the LCARS theme

        They are NOT loaded from this folder, and they are rewritten every time DashDeck starts,
        so any change made here is lost. To use one as a starting point:

          1. Copy it up one folder, into  ...\DashDeck\stage\
          2. Rename it if you like. Keeping the name  lcars.json  makes your copy replace the
             shipped LCARS stage whenever the LCARS theme is worn.
          3. Edit it in Notepad and save.
          4. In DashDeck: Settings > Themes > STAGE LAYOUT > RELOAD (or choose it there), or
             RELOAD STAGE LAYOUT from the three-dot menu while the stage shows it.

        Any problem with the file is listed in Settings > Themes > STAGE LAYOUT, under its name.
        A bad element is left out; it never blanks the stage.

        """;

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
