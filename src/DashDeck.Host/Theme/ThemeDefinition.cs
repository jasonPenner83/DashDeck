using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DashDeck.Host.Theme;

/// <summary>Where a theme came from.</summary>
public enum ThemeOrigin
{
    /// <summary>Shipped beside the executable, in <c>catalog/themes/</c>. Never written on the tablet.</summary>
    Shipped,

    /// <summary>The user's, in <c>%LOCALAPPDATA%\DashDeck\themes\</c>.</summary>
    Yours,

    /// <summary>Compiled in: the DashDeck look, for when no file can be found at all.</summary>
    BuiltIn,
}

/// <summary>
/// One theme file (ADR-0036): a name, and the tokens it changes.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "name": "LCARS (inspired)",
///   "description": "…",
///   "author": "…",
///   "fontFiles": [ "Antonio-Regular.ttf" ],
///   "tokens": { "canvas": "#000000", "buttonRadius": 28, "uiFont": "Antonio" },
///   "night":  { "canvas": "#000000" }
/// }
/// </code>
/// Only <c>name</c> is required. A token left out is the DashDeck look; a night value left out is
/// derived by dimming the day one. Font files named in <c>fontFiles</c> sit beside the theme file
/// and travel with it on import and export.
/// </remarks>
public sealed record ThemeDefinition
{
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    public string Author { get; init; } = "";

    /// <summary>
    /// The stage layout that comes with the theme (ADR-0037), by file name — <c>lcars</c> — or empty
    /// for the built-in cluster.
    /// </summary>
    public string StageLayout { get; init; } = "";

    /// <summary>
    /// The climate panel layout that comes with the theme (ADR-0040), by file name, or empty for
    /// the built-in Glass panel.
    /// </summary>
    public string ClimateLayout { get; init; } = "";

    /// <summary>Font files beside the theme file, carried with it.</summary>
    public IReadOnlyList<string> FontFiles { get; init; } = [];

    /// <summary>Token values as written — colours and fonts as text, numbers in invariant form.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>();

    /// <summary>Night values that should not be derived.</summary>
    public IReadOnlyDictionary<string, string> Night { get; init; } = new Dictionary<string, string>();

    // ── Where it lives ────────────────────────────────────────────────────────

    /// <summary>Shipped, yours, or compiled in.</summary>
    public ThemeOrigin Origin { get; init; } = ThemeOrigin.BuiltIn;

    /// <summary>The file it was read from, or null for the built-in one.</summary>
    public string? FilePath { get; init; }

    /// <summary>What the stored choice names: <c>shipped/lcars</c>, <c>yours/my-lcars</c>, <c>builtin/dashdeck</c>.</summary>
    public string Id => $"{Origin.ToString().ToLowerInvariant()}/{(FilePath is null ? "dashdeck" : Path.GetFileNameWithoutExtension(FilePath).ToLowerInvariant())}";

    /// <summary>The folder its font files are in, or null.</summary>
    public string? Folder => FilePath is null ? null : Path.GetDirectoryName(FilePath);

    /// <summary>The DashDeck look itself: no tokens changed.</summary>
    public static ThemeDefinition BuiltIn { get; } = new()
    {
        Name = "DashDeck",
        Description = "The default: warm dark surfaces and one accent.",
        Author = "DashDeck",
    };

    // ── Reading and writing ───────────────────────────────────────────────────

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Read a theme file. Token values are kept as written; whether they make sense is the
    /// resolver's business, so one bad colour costs that colour, not the theme.
    /// </summary>
    /// <exception cref="InvalidDataException">Not JSON, not an object, or no name.</exception>
    public static ThemeDefinition Parse(string json, string? filePath = null, ThemeOrigin origin = ThemeOrigin.Yours)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"not readable JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException("a theme is a JSON object");
            }

            var name = Text(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("a theme needs a name");
            }

            return new ThemeDefinition
            {
                Name = name.Trim(),
                Description = Text(root, "description")?.Trim() ?? "",
                Author = Text(root, "author")?.Trim() ?? "",
                StageLayout = Text(root, "stageLayout")?.Trim() ?? "",
                ClimateLayout = Text(root, "climateLayout")?.Trim() ?? "",
                FontFiles = Strings(root, "fontFiles"),
                Tokens = Values(root, "tokens"),
                Night = Values(root, "night"),
                FilePath = filePath,
                Origin = origin,
            };
        }
    }

    /// <summary>The theme as a file, numbers as numbers, ready to hand-edit.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,

            // "°" and "—" stay readable in a file meant to be edited by hand.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);

            if (Description.Length > 0)
            {
                writer.WriteString("description", Description);
            }

            if (Author.Length > 0)
            {
                writer.WriteString("author", Author);
            }

            if (StageLayout.Length > 0)
            {
                writer.WriteString("stageLayout", StageLayout);
            }

            if (ClimateLayout.Length > 0)
            {
                writer.WriteString("climateLayout", ClimateLayout);
            }

            if (FontFiles.Count > 0)
            {
                writer.WriteStartArray("fontFiles");
                foreach (var file in FontFiles)
                {
                    writer.WriteStringValue(file);
                }

                writer.WriteEndArray();
            }

            WriteValues(writer, "tokens", Tokens);

            if (Night.Count > 0)
            {
                WriteValues(writer, "night", Night);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValues(Utf8JsonWriter writer, string property, IReadOnlyDictionary<string, string> values)
    {
        writer.WriteStartObject(property);

        // Vocabulary order, so files written by DashDeck read the same way every time; anything
        // unknown after, as written, so a newer build's token survives an older build's save.
        var ordered = ThemeTokens.All.Select(t => t.Key)
            .Where(k => values.ContainsKey(k))
            .Concat(values.Keys.Where(k => !ThemeTokens.TryGet(k, out _)).Order(StringComparer.Ordinal));

        foreach (var key in ordered)
        {
            var value = values[key];
            if (ThemeTokens.TryGet(key, out var token) && token.Kind is TokenKind.Number &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                writer.WriteNumber(key, number);
            }
            else
            {
                writer.WriteString(key, value);
            }
        }

        writer.WriteEndObject();
    }

    private static string? Text(JsonElement root, string name) =>
        TryProperty(root, name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement root, string name) =>
        TryProperty(root, name, out var value) && value.ValueKind is JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind is JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    private static Dictionary<string, string> Values(JsonElement root, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!TryProperty(root, name, out var block) || block.ValueKind is not JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in block.EnumerateObject())
        {
            // The vocabulary's own spelling, so "Canvas" and "canvas" are one token.
            var key = ThemeTokens.TryGet(property.Name, out var token) ? token.Key : property.Name;

            values[key] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.Number => property.Value.GetDouble().ToString(CultureInfo.InvariantCulture),
                _ => property.Value.GetRawText(),
            };
        }

        return values;
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Every token's value for one theme, day or night, and what was wrong with the file.</summary>
public sealed record ResolvedTheme(
    IReadOnlyDictionary<string, ThemeColour> Colours,
    IReadOnlyDictionary<string, double> Numbers,
    IReadOnlyDictionary<string, string> Fonts,
    IReadOnlyList<string> Problems)
{
    public ThemeColour Colour(string key) => Colours[key];

    public double Number(string key) => Numbers[key];
}

/// <summary>
/// Turns a theme file into a value for every token (ADR-0036).
/// </summary>
/// <remarks>
/// For each token: the theme's value if it gave a usable one, else the default — which may name
/// another token, followed in the same mode. At night: the theme's night value; else its day
/// value dimmed by the token's rule; else the DashDeck night value. A value that cannot be used
/// costs that token and a line in <see cref="ResolvedTheme.Problems"/>, never the theme: a
/// screen in a truck must always come up dressed in something.
/// </remarks>
public static class ThemeResolver
{
    public static ResolvedTheme Resolve(ThemeDefinition theme, bool night)
    {
        var problems = new List<string>();

        foreach (var key in theme.Tokens.Keys.Concat(theme.Night.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!ThemeTokens.TryGet(key, out _))
            {
                problems.Add($"'{key}' is not a token DashDeck knows — ignored");
            }
        }

        var colours = new Dictionary<string, ThemeColour>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        var fonts = new Dictionary<string, string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in ThemeTokens.All)
        {
            switch (token.Kind)
            {
                case TokenKind.Colour:
                    colours[token.Key] = ColourOf(token, theme, night, problems, reported, depth: 0);
                    break;
                case TokenKind.Number:
                    numbers[token.Key] = NumberOf(token, theme, problems);
                    break;
                default:
                    fonts[token.Key] = FontOf(token, theme);
                    break;
            }
        }

        return new ResolvedTheme(colours, numbers, fonts, problems);
    }

    private static ThemeColour ColourOf(
        ThemeToken token, ThemeDefinition theme, bool night, List<string> problems, HashSet<string> reported, int depth)
    {
        if (night && Usable(theme.Night, token, problems, reported, "night") is { } nightValue)
        {
            return nightValue;
        }

        if (Usable(theme.Tokens, token, problems, reported, "") is { } dayValue)
        {
            return night ? dayValue.Dim(ThemeTokens.Factor(token.Night)) : dayValue;
        }

        var fallback = night ? token.NightDefault ?? token.Default : token.Default;

        if (fallback.StartsWith('@') && depth < 8 && ThemeTokens.TryGet(fallback[1..], out var followed))
        {
            return ColourOf(followed, theme, night, problems, reported, depth + 1);
        }

        ThemeColour.TryParse(fallback, out var value);
        return night && token.NightDefault is null ? value.Dim(ThemeTokens.Factor(token.Night)) : value;
    }

    private static ThemeColour? Usable(
        IReadOnlyDictionary<string, string> values, ThemeToken token, List<string> problems, HashSet<string> reported, string block)
    {
        if (!values.TryGetValue(token.Key, out var text))
        {
            return null;
        }

        if (ThemeColour.TryParse(text, out var colour))
        {
            return colour;
        }

        if (reported.Add($"{block}:{token.Key}"))
        {
            problems.Add($"{(block.Length > 0 ? "night " : "")}'{token.Key}': '{text}' is not a colour (#RRGGBB) — using the default");
        }

        return null;
    }

    private static double NumberOf(ThemeToken token, ThemeDefinition theme, List<string> problems)
    {
        var fallback = double.Parse(token.Default, CultureInfo.InvariantCulture);

        if (!theme.Tokens.TryGetValue(token.Key, out var text))
        {
            return fallback;
        }

        var (min, max) = ThemeTokens.Range(token.Key);

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= min && value <= max)
        {
            return value;
        }

        problems.Add(string.Create(CultureInfo.InvariantCulture, $"'{token.Key}': '{text}' should be a number from {min} to {max} — using the default"));
        return fallback;
    }

    private static string FontOf(ThemeToken token, ThemeDefinition theme) =>
        theme.Tokens.TryGetValue(token.Key, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : token.Default;

    /// <summary>
    /// Readability, checked on the resolved day values: text against what it sits on. These are
    /// warnings shown beside the theme, not refusals — the person choosing it can see the result.
    /// </summary>
    public static IReadOnlyList<string> Legibility(ResolvedTheme theme)
    {
        var warnings = new List<string>();
        var canvas = theme.Colour("canvas");

        void Check(string text, string background, ThemeColour behind, double floor, string what)
        {
            var ratio = ThemeColour.Contrast(theme.Colour(text).Over(behind), behind);
            if (ratio < floor)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{what} is hard to read: '{text}' on '{background}' is {ratio:0.0}:1 (aim for {floor}:1)"));
            }
        }

        Check("textHigh", "canvas", canvas, 4.5, "Headline text");
        Check("textMid", "surface", theme.Colour("surface").Over(canvas), 3, "Body text");
        Check("caption", "canvas", canvas, 3, "Captions");
        Check("navText", "navBackground", theme.Colour("navBackground").Over(canvas), 3, "Navigation");
        Check("buttonText", "buttonBackground", theme.Colour("buttonBackground").Over(canvas), 3, "Button text");

        var wash = theme.Colour("accent") with { A = (byte)Math.Round(theme.Number("accentWash") * 255) };
        Check("selectedText", "the selected fill", wash.Over(canvas), 3, "Selected button text");

        return warnings;
    }
}
