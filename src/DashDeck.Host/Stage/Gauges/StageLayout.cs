using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashDeck.Abstractions;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>What an element on the stage is.</summary>
public enum StageElementType
{
    /// <summary>A reading drawn as a dial, arc, bar, LCARS bar or number.</summary>
    Gauge,

    /// <summary>Fixed words: a heading, a label, an LCARS block caption.</summary>
    Text,

    /// <summary>The time, formatted as you like.</summary>
    Clock,

    /// <summary>A filled shape with its own corner radii — a frame, a divider, an LCARS elbow.</summary>
    Panel,

    /// <summary>A compass rose with the heading in the middle (ADR-0039). Reads a tablet sensor, truck first.</summary>
    Compass,

    /// <summary>A G meter: rings, a crosshair and a ball pushed the way the driver is (ADR-0039).</summary>
    GMeter,

    /// <summary>
    /// A set temperature drawn large, with a thin arc showing where it sits in its range — the
    /// climate panel's zone readout (ADR-0040). Reads a signal; any range works.
    /// </summary>
    Setpoint,

    /// <summary>A row of steps lit up to the value — fan speed, seat heat or cooling (ADR-0040).</summary>
    Levels,

    /// <summary>A pill that lights when its signal is on — A/C, AUTO, RECIRC, a defroster (ADR-0040).</summary>
    Indicator,

    /// <summary>A frosted glass panel: a translucent tint, a sheen across the top, a soft shadow (ADR-0040).</summary>
    Glass,

    /// <summary>
    /// A warning light: an icon that lights when its signal says so — check engine, low fuel, a
    /// door (ADR-0041). Dark when off; dimmer still, with a grey dot, when the truck has not said.
    /// </summary>
    Warning,
}

/// <summary>
/// The surface a layout is drawn on, in its own pixels (ADR-0040): the stage, or the climate panel
/// in the two bands below it. Positions are in these, and the canvas is scaled to the real region.
/// </summary>
public sealed record LayoutCanvas(string Name, double Width, double Height)
{
    /// <summary>The stage: four bands less the launcher bar.</summary>
    public static LayoutCanvas Stage { get; } = new("stage", 912, 636);

    /// <summary>The climate panel: the two bands where the cards are.</summary>
    public static LayoutCanvas Climate { get; } = new("climate panel", 912, 390);

    /// <summary>The console: the two bands below the stage when DASH is chosen (ADR-0041).</summary>
    public static LayoutCanvas Console { get; } = new("console", 912, 390);
}

/// <summary>A layout's own fonts (ADR-0042): either may be left out to keep the theme's.</summary>
/// <param name="Ui">Numbers and headings — the theme's <c>UiFont</c>.</param>
/// <param name="Mono">Captions and labels — the theme's <c>MonoFont</c>.</param>
public sealed record LayoutFonts(string? Ui = null, string? Mono = null);

/// <summary>How a gauge is drawn.</summary>
public enum GaugeStyle
{
    /// <summary>A round meter with a needle, ticks and a bezel — the F-150 cluster's idiom.</summary>
    Dial,

    /// <summary>A thick sweep that fills to the value, with the number in the middle.</summary>
    Arc,

    /// <summary>A straight track that fills to the value, horizontal or vertical.</summary>
    Bar,

    /// <summary>A row of pill segments that light up to the value, with an end cap — the LCARS look.</summary>
    LcarsBar,

    /// <summary>The number, large, with its label. No scale.</summary>
    Digital,
}

/// <summary>
/// Where a gauge's number comes from: one signal, optionally minus another, scaled — or one
/// sensor from the sensor catalog.
/// </summary>
/// <remarks>
/// Enough for the derived readings a cluster shows — boost is manifold pressure minus barometric,
/// in psi — without a formula language. Anything cleverer is a component (ADR-0023).
/// <para>
/// A <see cref="Sensor"/> (ADR-0039) is read through the sensor service: the truck's value when it
/// has one, otherwise the tablet's, and the gauge says which underneath — a fallback is named on
/// screen, never a silent stand-in (ADR-0016). It costs no request budget of its own.
/// </para>
/// </remarks>
public sealed record GaugeSource
{
    /// <summary>The catalog signal id, e.g. <c>engine.intakeManifoldPressure</c>.</summary>
    public string Signal { get; init; } = "";

    /// <summary>A sensor catalog id instead of a signal, e.g. <c>attitude.pitch</c>. Not both.</summary>
    public string? Sensor { get; init; }

    /// <summary>True when this source is a sensor rather than a signal.</summary>
    [JsonIgnore]
    public bool IsSensor => !string.IsNullOrWhiteSpace(Sensor);

    /// <summary>A second signal subtracted from the first, or null.</summary>
    public string? Minus { get; init; }

    /// <summary>Used for <see cref="Minus"/> when it has no reading — barometric barely moves, so 101.325 kPa is a fair stand-in.</summary>
    public double? MinusFallback { get; init; }

    /// <summary>Multiplied after the subtraction: 0.1450377 turns kPa into psi.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Added last.</summary>
    public double Offset { get; init; }

    /// <summary>How often to ask, while the gauge is on screen. Every gauge shares the adapter's ~19 a second.</summary>
    public double RateHz { get; init; } = 1;
}

/// <summary>A coloured band on the scale: a redline, a normal range.</summary>
public sealed record GaugeZone(double From, double To, string Colour);

/// <summary>
/// One element on the stage: where it sits, and what it is — a gauge, some text, the clock, or a
/// panel. Fields that do not apply to its type are ignored.
/// </summary>
public sealed record GaugeSpec
{
    /// <summary>A name for it, for problems and for Describe(). Optional.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gauge unless it says otherwise.</summary>
    public StageElementType Type { get; init; } = StageElementType.Gauge;

    /// <summary>For a gauge: how it is drawn.</summary>
    public GaugeStyle Style { get; init; } = GaugeStyle.Dial;

    /// <summary>For text: the words. For a clock: the .NET time format, default <c>HH:mm</c>.</summary>
    public string Content { get; init; } = "";

    /// <summary>For text, a clock or a panel: its colour — <c>#RRGGBB</c> or <c>@token</c>.</summary>
    public string? Colour { get; init; }

    /// <summary>For text and the clock: size in px.</summary>
    public double FontSize { get; init; } = 24;

    /// <summary>For text and the clock: <c>ui</c> (the theme's headline font) or <c>mono</c> (its label font).</summary>
    public string Font { get; init; } = "mono";

    /// <summary>For text and the clock: <c>left</c>, <c>center</c> or <c>right</c>.</summary>
    public string Align { get; init; } = "left";

    /// <summary>
    /// For a panel: corner radii, <c>"12"</c> for all four or <c>"40,0,0,40"</c> for top-left,
    /// top-right, bottom-right, bottom-left. A big radius on one corner of a thick panel is an
    /// LCARS elbow.
    /// </summary>
    public string Radius { get; init; } = "0";

    /// <summary>Left edge, in stage pixels (the stage is 912 × 636).</summary>
    public double X { get; init; }

    /// <summary>Top edge, in stage pixels.</summary>
    public double Y { get; init; }

    public double Width { get; init; } = 200;

    public double Height { get; init; } = 200;

    /// <summary>The caption, e.g. BOOST.</summary>
    public string Label { get; init; } = "";

    /// <summary>The unit written after the number, e.g. psi.</summary>
    public string Unit { get; init; } = "";

    /// <summary>.NET number format for the readout: <c>0</c>, <c>0.0</c>.</summary>
    public string Format { get; init; } = "0";

    public GaugeSource Source { get; init; } = new();

    public double Min { get; init; }

    public double Max { get; init; } = 100;

    /// <summary>Spacing of the long ticks and numerals. 0 for none.</summary>
    public double MajorTick { get; init; }

    /// <summary>Spacing of the short ticks. 0 for none.</summary>
    public double MinorTick { get; init; }

    public IReadOnlyList<GaugeZone> Zones { get; init; } = [];

    /// <summary>
    /// Style-specific tuning — needle width, arc thickness, colours, segment count. Every part has
    /// a default, so a gauge sets only what it changes. See <see cref="GaugeParts"/>.
    /// </summary>
    public Dictionary<string, JsonElement> Parts { get; init; } = [];

    public bool IsBig => Math.Min(Width, Height) > 200;

    // ── Typed access to parts ─────────────────────────────────────────────────

    public double Number(string part, double fallback) =>
        Parts.TryGetValue(part, out var e) && e.ValueKind is JsonValueKind.Number ? e.GetDouble() : fallback;

    public bool Flag(string part, bool fallback) =>
        Parts.TryGetValue(part, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    public string Text(string part, string fallback) =>
        Parts.TryGetValue(part, out var e) && e.ValueKind is JsonValueKind.String ? e.GetString()! : fallback;
}

/// <summary>The parts each style understands, with their defaults — the reference for a layout file.</summary>
public static class GaugeParts
{
    /// <summary>Parts every style takes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Common = new Dictionary<string, string>
    {
        ["labelColour"] = "colour of the caption — default the theme's caption colour",
        ["valueColour"] = "colour of the number — default the theme's headline text",
        ["labelSize"] = "caption size in px",
        ["valueSize"] = "number size in px",
        ["showLabel"] = "true/false",
        ["showValue"] = "true/false — the digital readout",
        ["showSource"] = "true/false — for a sensor source, where the reading came from (TRUCK, the tablet sensor, NOT LEVELLED). Default true",
        ["valueWeight"] = "the number's weight: thin, light, regular, medium, semibold or bold",
        ["labelWeight"] = "the caption's weight",
        ["unitSize"] = "draw the unit smaller than the number, at this size, px",
        ["unitColour"] = "the unit's colour when it is drawn smaller — default the caption colour",
        ["noData"] = "what shows with no reading — default NO DATA; \"–\" is quieter",
    };

    /// <summary>Parts the compass and G meter take (ADR-0039).</summary>
    public static readonly IReadOnlyDictionary<StageElementType, IReadOnlyDictionary<string, string>> ByElement = new Dictionary<StageElementType, IReadOnlyDictionary<string, string>>
    {
        [StageElementType.Compass] = new Dictionary<string, string>
        {
            ["mode"] = "\"rose\" — the card turns under a fixed marker (default) — or \"needle\": north stays up and a needle points the way you are heading",
            ["ringColour"] = "the outer ring, or \"none\"",
            ["cardinalTickColour"] = "the N, E, S and W ticks — default the accent",
            ["majorTickColour"] = "the NE, SE, SW and NW ticks",
            ["minorTickColour"] = "every 5°",
            ["tickStep"] = "degrees between ticks — default 5",
            ["northColour"] = "the N — default the accent",
            ["letterColour"] = "E, S and W",
            ["letterSize"] = "size of N; the others are three-quarters of it",
            ["showLetters"] = "true/false",
            ["markerColour"] = "the fixed marker (rose) or the needle (needle) — default the accent",
            ["valueColour"] = "the heading number — default the theme's headline text",
            ["valueSize"] = "heading number size in px",
            ["showValue"] = "true/false — the heading number",
            ["cardinalColour"] = "the NE / SW under the number — default the accent",
            ["showCardinal"] = "true/false",
            ["showSource"] = "true/false — where the heading came from. Default true",
        },
        [StageElementType.Setpoint] = new Dictionary<string, string>
        {
            ["arcColour"] = "the arc to the value — default the accent",
            ["trackColour"] = "the rest of the arc",
            ["thickness"] = "arc thickness, px — default 4",
            ["sweep"] = "degrees the arc covers — default 240",
            ["glow"] = "a soft glow round the arc: a colour, or \"none\"",
            ["valueColour"] = "the number — default the theme's headline text",
            ["valueSize"] = "number size, px",
            ["labelColour"] = "the caption",
            ["labelSize"] = "caption size, px",
            ["showArc"] = "true/false",
            ["valueWeight"] = "the number's weight: thin, light, regular, medium, semibold or bold",
            ["labelWeight"] = "the caption's weight",
        },
        [StageElementType.Levels] = new Dictionary<string, string>
        {
            ["steps"] = "how many steps — default 7",
            ["shape"] = "\"bars\" (rising, default) or \"dots\"",
            ["litColour"] = "lit steps — default the accent",
            ["unlitColour"] = "unlit steps",
            ["negativeColour"] = "lit steps when the value is below zero — seat cooling, say. Default an ice blue",
            ["gap"] = "space between steps, px",
            ["labelColour"] = "the caption",
            ["labelSize"] = "caption size, px",
            ["showValue"] = "true/false — the number beside the caption",
            ["labelWeight"] = "the caption's weight",
            ["positiveText"] = "a word before the value above zero — \"HEAT\" for a seat",
            ["negativeText"] = "a word before the value below zero — \"COOL\" for a seat",
        },
        [StageElementType.Indicator] = new Dictionary<string, string>
        {
            ["onAt"] = "lit when the value is at least this — default 1",
            ["below"] = "lit when the value is below this instead",
            ["equals"] = "lit only when the value is exactly this (an airflow setting, say)",
            ["bit"] = "lit when this bit of the value is set — 0 for the lowest",
            ["litColour"] = "fill when lit — default the accent",
            ["litText"] = "text when lit — default dark on the accent",
            ["unlitColour"] = "text and outline when off",
            ["radius"] = "corner radius, px — default half the height (a pill)",
            ["fontSize"] = "text size, px",
            ["style"] = "\"pill\" (default) or \"text\" — just the word, lit in its colour",
            ["align"] = "\"left\", \"center\" or \"right\" — where the word sits",
            ["labelWeight"] = "the word's weight",
        },
        [StageElementType.Warning] = new Dictionary<string, string>
        {
            ["icon"] = "checkEngine, oil, battery, coolant, fuel, seatbelt, door, brake or tpms — or your own as SVG path data on a 24 × 24 grid",
            ["onAt"] = "lit when the value is at least this — default 1",
            ["below"] = "lit when the value is below this instead — low fuel, low voltage",
            ["equals"] = "lit only when the value is exactly this",
            ["bit"] = "lit when this bit of the value is set — 0 for the lowest",
            ["litColour"] = "the icon when lit — default amber",
            ["unlitColour"] = "the icon when off — default a faint white",
        },
        [StageElementType.Glass] = new Dictionary<string, string>
        {
            ["tint"] = "the glass colour — default a cool white",
            ["opacity"] = "how much of the tint shows, 0–1 — default 0.07",
            ["sheen"] = "the brighter band across the top, 0–1 — default 0.10; 0 for none",
            ["edge"] = "the outline colour, or \"none\" — default a faint white",
            ["shadow"] = "the soft shadow under it, 0–1 — default 0.45; 0 for none",
        },
        [StageElementType.GMeter] = new Dictionary<string, string>
        {
            ["range"] = "g at the outer ring — default 1",
            ["rings"] = "how many rings — default 3 (a quarter, a half and the whole range)",
            ["ringColour"] = "the inner rings",
            ["outerRingColour"] = "the outer ring",
            ["crossColour"] = "the crosshair, or \"none\"",
            ["ballColour"] = "the ball — default the quality colour, so it reads like the dot on a gauge",
            ["ballSize"] = "ball diameter, px",
            ["showValue"] = "true/false — G and PEAK under the meter",
            ["valueColour"] = "the G number",
            ["valueSize"] = "size of G and PEAK, px",
            ["labelColour"] = "the G and PEAK captions",
            ["showSource"] = "true/false — where the reading came from. Default true",
        },
    };

    /// <summary>Parts by style.</summary>
    public static readonly IReadOnlyDictionary<GaugeStyle, IReadOnlyDictionary<string, string>> ByStyle = new Dictionary<GaugeStyle, IReadOnlyDictionary<string, string>>
    {
        [GaugeStyle.Dial] = new Dictionary<string, string>
        {
            ["startAngle"] = "degrees from 12 o'clock where the scale starts — default -135",
            ["sweep"] = "degrees the scale covers — default 270",
            ["face"] = "dial face colour, or \"none\"",
            ["bezel"] = "\"chrome\", \"ring\" or \"none\"",
            ["bezelColour"] = "ring colour when bezel is \"ring\"",
            ["needleColour"] = "needle colour",
            ["needleWidth"] = "needle width at the hub, px",
            ["needleGlow"] = "glow colour around the needle, or \"none\"",
            ["hubColour"] = "the pivot's centre colour",
            ["tickColour"] = "long tick colour",
            ["minorTickColour"] = "short tick colour",
            ["tickLength"] = "long tick length, px",
            ["numerals"] = "true/false — numbers on the scale",
            ["numeralColour"] = "number colour",
            ["numeralDivisor"] = "divide the scale numbers by this: 1000 shows rpm as 1–7",
        },
        [GaugeStyle.Arc] = new Dictionary<string, string>
        {
            ["startAngle"] = "degrees from 12 o'clock where the arc starts — default -135",
            ["sweep"] = "degrees the arc covers — default 270",
            ["thickness"] = "arc thickness, px",
            ["trackColour"] = "the unfilled arc",
            ["fillColour"] = "the filled part — default the theme accent",
            ["tickColour"] = "tick colour",
        },
        [GaugeStyle.Bar] = new Dictionary<string, string>
        {
            ["orientation"] = "\"horizontal\" or \"vertical\"",
            ["thickness"] = "track thickness, px",
            ["trackColour"] = "the unfilled track",
            ["fillColour"] = "the filled part — default the theme accent",
            ["tickColour"] = "tick colour",
            ["radius"] = "corner radius of the track, px",
        },
        [GaugeStyle.LcarsBar] = new Dictionary<string, string>
        {
            ["orientation"] = "\"horizontal\" or \"vertical\"",
            ["segments"] = "how many pills — default 20",
            ["segmentGap"] = "gap between pills, px",
            ["thickness"] = "pill height (or width when vertical), px",
            ["trackColour"] = "unlit pills",
            ["fillColour"] = "lit pills — default the theme accent",
            ["capColour"] = "the end cap that carries the caption",
            ["capWidth"] = "end cap width, px",
        },
        [GaugeStyle.Digital] = new Dictionary<string, string>
        {
            ["frameColour"] = "outline colour, or \"none\"",
            ["frameRadius"] = "outline corner radius, px",
            ["align"] = "\"left\", \"center\" or \"right\" — lays the caption above the number, aligned",
            ["labelPosition"] = "\"above\" puts the caption over the number; \"below\" (default) under it, centred",
            ["labelGap"] = "space between the caption and the number when above, px",
        },
    };

    public static bool Knows(GaugeStyle style, string part) =>
        Common.ContainsKey(part) || (ByStyle.TryGetValue(style, out var parts) && parts.ContainsKey(part));

    /// <summary>Whether an element of this type takes this part. Gauges by style; compass and G meter by type.</summary>
    public static bool Knows(GaugeSpec element, string part) => element.Type switch
    {
        StageElementType.Gauge => Knows(element.Style, part),
        _ => ByElement.TryGetValue(element.Type, out var parts) && parts.ContainsKey(part),
    };
}

/// <summary>Where a gauge layout came from.</summary>
public enum LayoutOrigin
{
    BuiltIn,
    Shipped,
    Yours,
}

/// <summary>
/// A whole stage, as a file (ADR-0037): every element's position and size — gauges with their
/// source, scale and look, text, the clock, panels.
/// </summary>
/// <remarks>
/// Modelled on a Home Assistant dashboard in YAML, as JSON with comments: the layout is data, so
/// changing the stage is editing a file and pressing RELOAD, and a GUI can write the same file later.
/// </remarks>
public sealed record StageLayout
{
    /// <summary>The surface it is drawn on — the stage, or the climate panel (ADR-0040).</summary>
    [JsonIgnore]
    public LayoutCanvas Canvas { get; init; } = LayoutCanvas.Stage;

    /// <summary>The stage's size in its own pixels. Positions are in these.</summary>
    public const double StageWidth = 912;

    /// <inheritdoc cref="StageWidth"/>
    public const double StageHeight = 636;

    public required string Name { get; init; }

    public string Description { get; init; } = "";

    public string Author { get; init; } = "";

    /// <summary>Behind the gauges: <c>#RRGGBB</c>, <c>@token</c>, or null for the theme's canvas.</summary>
    public string? Background { get; init; }

    /// <summary>
    /// The layout's own type, or null to use the theme's — <c>{ "ui": "Segoe UI", "mono": "Segoe UI" }</c>.
    /// <c>ui</c> is the numbers and headings, <c>mono</c> the captions. A comma list falls back in
    /// order (<c>"Segoe UI Variable Display, Segoe UI"</c>). Only this layout changes.
    /// </summary>
    public LayoutFonts? Fonts { get; init; }

    /// <summary>Everything on the stage, drawn in order — later elements on top.</summary>
    public IReadOnlyList<GaugeSpec> Elements { get; init; } = [];

    /// <summary>The elements that are gauges.</summary>
    [JsonIgnore]
    public IEnumerable<GaugeSpec> Gauges => Elements.Where(e => e.Type is StageElementType.Gauge);

    [JsonIgnore]
    public LayoutOrigin Origin { get; init; } = LayoutOrigin.BuiltIn;

    [JsonIgnore]
    public string? FilePath { get; init; }

    /// <summary>A built-in layout's name for itself, since it has no file: <c>default</c>, <c>compass</c>.</summary>
    [JsonIgnore]
    public string? BuiltInSlug { get; init; }

    /// <summary>The file name without extension, lower case — what a theme names (<c>"gaugeLayout": "lcars"</c>).</summary>
    [JsonIgnore]
    public string Slug => FilePath is null ? BuiltInSlug ?? "default" : Path.GetFileNameWithoutExtension(FilePath).ToLowerInvariant();

    /// <summary>The elements that read tablet sensors — the ones a view must poll (ADR-0039).</summary>
    [JsonIgnore]
    public bool UsesSensors => Elements.Any(e => e.Type is StageElementType.Compass or StageElementType.GMeter || e.Source.IsSensor);

    /// <summary>True when there is a G meter, whose peak the stage's menu can reset.</summary>
    [JsonIgnore]
    public bool HasGMeter => Elements.Any(e => e.Type is StageElementType.GMeter);

    /// <summary><c>builtin/default</c>, <c>shipped/lcars</c>, <c>yours/towing</c>.</summary>
    [JsonIgnore]
    public string Id => $"{Origin.ToString().ToLowerInvariant()}/{Slug}";

    /// <summary>Gauges that were left out, and things worth fixing in the ones kept.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Problems { get; init; } = [];

    // ── Reading and writing ───────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Read a layout. A gauge that cannot be drawn honestly is left out and named in
    /// <see cref="Problems"/>; the rest still load.
    /// </summary>
    /// <exception cref="InvalidDataException">Not JSON, no name, or not a layout at all.</exception>
    /// <param name="canvas">What it is drawn on — the stage unless said. Positions are checked against it.</param>
    public static StageLayout Parse(string json, string? filePath = null, LayoutOrigin origin = LayoutOrigin.Yours, LayoutCanvas? canvas = null)
    {
        canvas ??= LayoutCanvas.Stage;
        StageLayout? layout;
        try
        {
            layout = JsonSerializer.Deserialize<StageLayout>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"not a readable gauge layout: {ex.Message}", ex);
        }

        if (layout is null || string.IsNullOrWhiteSpace(layout.Name))
        {
            throw new InvalidDataException("a gauge layout needs a name");
        }

        var problems = new List<string>();
        var kept = new List<GaugeSpec>();

        if (layout.Background is { } background && !IsColour(background))
        {
            problems.Add($"background '{background}' is not a colour (#RRGGBB or @token) — using the theme's canvas");
            layout = layout with { Background = null };
        }

        for (var i = 0; i < layout.Elements.Count; i++)
        {
            var gauge = layout.Elements[i];
            var name = gauge.Id.Length > 0 ? gauge.Id
                : gauge.Label.Length > 0 ? gauge.Label
                : $"{gauge.Type.ToString().ToLowerInvariant()} {i + 1}";

            // A compass reads the heading unless it names another sensor.
            if (gauge.Type is StageElementType.Compass && !gauge.Source.IsSensor && string.IsNullOrWhiteSpace(gauge.Source.Signal))
            {
                gauge = gauge with { Source = gauge.Source with { Sensor = "attitude.heading" } };
            }

            if (Refusal(gauge) is { } refusal)
            {
                problems.Add($"{name}: {refusal} — left out");
                continue;
            }

            problems.AddRange(Warnings(gauge, canvas).Select(w => $"{name}: {w}"));
            kept.Add(gauge);
        }

        return layout with
        {
            Name = layout.Name.Trim(),
            Elements = kept,
            Problems = problems,
            FilePath = filePath,
            Origin = origin,
            Canvas = canvas,
        };
    }

    /// <summary>The layout as a file, ready to edit by hand.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>Why an element cannot be drawn at all, or null.</summary>
    private static string? Refusal(GaugeSpec g)
    {
        if (g.Width <= 0 || g.Height <= 0)
        {
            return "width and height must be positive";
        }

        switch (g.Type)
        {
            case StageElementType.Text when string.IsNullOrWhiteSpace(g.Content):
                return "text with no content";
            case StageElementType.Clock:
                try
                {
                    _ = DateTime.UnixEpoch.ToString(g.Content.Length > 0 ? g.Content : "HH:mm", CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    return $"'{g.Content}' is not a time format";
                }

                return null;
            case StageElementType.Panel or StageElementType.Glass when ParseRadius(g.Radius) is null:
                return $"radius '{g.Radius}' should be one number or four, comma-separated";
            case StageElementType.Text or StageElementType.Panel or StageElementType.Glass:
                return null;
            case StageElementType.Levels when g.Number("steps", 7) is < 1 or > 20:
                return "steps must be 1 to 20";
            case StageElementType.Warning when !WarningIcons.IsKnown(g.Text("icon", "")):
                return $"icon '{g.Text("icon", "")}' is not one of {string.Join(", ", WarningIcons.Names)} or SVG path data";
            case StageElementType.Compass when !string.IsNullOrWhiteSpace(g.Source.Signal):
                return "a compass reads a sensor (source.sensor), not a signal";
            case StageElementType.Compass:
            case StageElementType.GMeter:
                return g.Number("range", 1) is > 0 and <= 10
                    ? null
                    : "range must be above 0 and at most 10 g";
        }

        if (g.Source.IsSensor && !string.IsNullOrWhiteSpace(g.Source.Signal))
        {
            return "a source is a signal or a sensor, not both";
        }

        if (g.Source.IsSensor && g.Source.Minus is not null)
        {
            return "minus works with signals only — a sensor source cannot subtract";
        }

        if (string.IsNullOrWhiteSpace(g.Source.Signal) && !g.Source.IsSensor)
        {
            return "no source — give source.signal or source.sensor";
        }

        if (!(g.Max > g.Min))
        {
            return string.Create(CultureInfo.InvariantCulture, $"max ({g.Max}) must be above min ({g.Min})");
        }

        if (g.MajorTick < 0 || g.MinorTick < 0)
        {
            return "tick spacing cannot be negative";
        }

        if ((g.MinorTick > 0 && (g.Max - g.Min) / g.MinorTick > 400) || (g.MajorTick > 0 && (g.Max - g.Min) / g.MajorTick > 400))
        {
            return "tick spacing is so fine the scale would be solid";
        }

        if (g.Source.Scale == 0 || !double.IsFinite(g.Source.Scale))
        {
            return "source.scale must be a non-zero number";
        }

        if (g.Source.RateHz is <= 0 or > 10)
        {
            return "source.rateHz must be above 0 and at most 10 — every gauge shares one link";
        }

        try
        {
            _ = 1.0.ToString(g.Format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return $"format '{g.Format}' is not a number format";
        }

        return null;
    }

    /// <summary>Things worth fixing in a gauge that is still drawn.</summary>
    private static IEnumerable<string> Warnings(GaugeSpec g, LayoutCanvas canvas)
    {
        if (g.X < 0 || g.Y < 0 || g.X + g.Width > canvas.Width + 0.5 || g.Y + g.Height > canvas.Height + 0.5)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"reaches outside the {canvas.Width} × {canvas.Height} {canvas.Name} and will be cut off");
        }

        if (g.Colour is { } colour && !IsColour(colour))
        {
            yield return $"colour '{colour}' is not a colour (#RRGGBB or @token) — using the default";
        }

        if (g.Type is StageElementType.Text or StageElementType.Clock or StageElementType.Panel)
        {
            yield break;
        }

        foreach (var part in g.Parts.Keys.Where(p => !GaugeParts.Knows(g, p)))
        {
            var what = g.Type is StageElementType.Gauge ? $"{g.Style.ToString().ToLowerInvariant()} gauge" : ElementName(g.Type);
            yield return $"'{part}' is not a part of a {what} — ignored";
        }

        foreach (var (part, value) in g.Parts)
        {
            if (part.EndsWith("Colour", StringComparison.Ordinal) || part is "face" or "needleGlow" or "ringColour" or "crossColour" or "tint" or "edge" or "glow" or "litText")
            {
                var text = value.ValueKind is JsonValueKind.String ? value.GetString()! : value.GetRawText();
                if (!IsColour(text) && !text.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"'{part}': '{text}' is not a colour (#RRGGBB, @token or none) — using the default";
                }
            }
        }

        foreach (var zone in g.Zones)
        {
            if (!IsColour(zone.Colour))
            {
                yield return $"a zone's colour '{zone.Colour}' is not a colour — that zone is not drawn";
            }

            if (zone.To <= zone.From)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"a zone from {zone.From} to {zone.To} is empty");
            }
        }
    }

    /// <summary>How a layout file writes an element type: <c>gMeter</c>, <c>compass</c>.</summary>
    private static string ElementName(StageElementType type) =>
        JsonNamingPolicy.CamelCase.ConvertName(type.ToString());

    /// <summary>Corner radii: one number for all four, or four for top-left, top-right, bottom-right, bottom-left.</summary>
    public static double[]? ParseRadius(string text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        var numbers = new List<double>();

        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0 || !double.IsFinite(n))
            {
                return null;
            }

            numbers.Add(n);
        }

        return numbers.Count switch
        {
            1 => [numbers[0], numbers[0], numbers[0], numbers[0]],
            4 => [.. numbers],
            _ => null,
        };
    }

    /// <summary><c>#RRGGBB</c> and friends, or <c>@token</c> naming a theme colour.</summary>
    public static bool IsColour(string text) =>
        ThemeColour.TryParse(text, out _)
        || (text.StartsWith('@') && ThemeTokens.TryGet(text[1..], out var token) && token.Kind is TokenKind.Colour);

    // ── The built-in layout ───────────────────────────────────────────────────

    /// <summary>
    /// The cluster that shipped before layouts were files: boost and oil temperature large, volts,
    /// intake, throttle and load small, in the F-150's own style. SAVE AS makes it yours to edit.
    /// </summary>
    public static StageLayout BuiltIn { get; } = Parse(BuiltInJson, null, LayoutOrigin.BuiltIn);

    /// <summary>
    /// The COMPASS screen, as a layout (ADR-0039): the heading rose, the G meter, pitch and roll,
    /// speed and outside temperature, and the phone's position — what was drawn in code before.
    /// The launcher's <c>compass</c> entry shows it; a layout of yours called <c>compass</c> replaces it.
    /// </summary>
    public static StageLayout BuiltInCompass { get; } = Parse(BuiltInCompassJson, null, LayoutOrigin.BuiltIn) with { BuiltInSlug = CompassSlug };

    /// <summary>What the compass layout is called, and what the launcher's <c>compass</c> entry looks for.</summary>
    public const string CompassSlug = "compass";

    /// <summary>Every layout compiled in.</summary>
    public static IReadOnlyList<StageLayout> BuiltIns { get; } = [BuiltIn, BuiltInCompass];

    /// <summary>
    /// The climate panel (ADR-0040), drawn as type rather than boxes: each side's temperature large at
    /// its edge with the seat beneath, the fan and airflow between, the switches as words that light.
    /// Read only. A climate layout of yours called <c>clean</c>, or the one a theme names, replaces
    /// it; Glass, the first built-in, ships as <c>catalog/climate/glass.json</c>.
    /// </summary>
    public static StageLayout BuiltInClimate { get; } =
        Parse(BuiltInClimateJson, null, LayoutOrigin.BuiltIn, LayoutCanvas.Climate) with { BuiltInSlug = ClimateSlug };

    /// <summary>What the built-in climate layout is called.</summary>
    public const string ClimateSlug = "clean";

    /// <summary>The climate layouts compiled in.</summary>
    public static IReadOnlyList<StageLayout> ClimateBuiltIns { get; } = [BuiltInClimate];

    /// <summary>
    /// The console (ADR-0041), drawn as type rather than boxes: speed large in the middle, engine and
    /// fuel down the left, range and economy down the right, warning lights that show only when on,
    /// the odometer along the bottom. A console layout of yours called <c>clean</c>, or the one a
    /// theme names, replaces it; Modern, the first built-in, ships as <c>catalog/console/modern.json</c>.
    /// </summary>
    public static StageLayout BuiltInConsole { get; } =
        Parse(BuiltInConsoleJson, null, LayoutOrigin.BuiltIn, LayoutCanvas.Console) with { BuiltInSlug = ConsoleSlug };

    /// <summary>What the built-in console layout is called.</summary>
    public const string ConsoleSlug = "clean";

    /// <summary>The console layouts compiled in.</summary>
    public static IReadOnlyList<StageLayout> ConsoleBuiltIns { get; } = [BuiltInConsole];

    /// <summary>The console layout as text, comments and all — what <c>console\examples\clean.json</c> holds.</summary>
    internal const string BuiltInConsoleJson = """
    {
      "name": "Clean",
      "description": "Type, not boxes: speed large in the middle, engine and fuel on the left, range and economy on the right, warning lights that only show when they are on, and the odometer along the bottom.",
      "author": "DashDeck",
      // Black behind; white and grey type from the theme's text colours, so it dims at night with the
      // rest of the dash. Only what is lit — a switch, a heater, a warning — has a colour of its own.
      "background": "#000000",
      // One family, two weights. Windows 11's Segoe UI Variable, or Segoe UI where it is not installed.
      "fonts": { "ui": "Segoe UI Variable Display, Segoe UI", "mono": "Segoe UI Variable Text, Segoe UI" },
      "elements": [
        // ── Speed ── the one large thing on the screen.
        { "id": "speed", "style": "digital", "x": 296, "y": 22, "width": 320, "height": 176,
          "format": "0", "min": 0, "max": 250,
          "source": { "signal": "vehicle.speed", "rateHz": 4 },
          "parts": { "align": "center", "valueSize": 138, "valueWeight": "light", "valueColour": "@textHigh", "noData": "–" } },
        { "id": "speedUnit", "type": "text", "x": 296, "y": 196, "width": 320, "height": 24,
          "content": "km/h", "fontSize": 16, "align": "center", "colour": "@textMid", "font": "mono", "parts": { "weight": "regular" } },

        // ── Engine and fuel ── a caption, then the number; space does the separating.
        { "id": "rpm", "style": "digital", "x": 40, "y": 34, "width": 220, "height": 66,
          "label": "RPM", "format": "#,0", "min": 0, "max": 7000,
          "source": { "signal": "engine.rpm", "rateHz": 3 },
          "parts": { "align": "left", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "noData": "–" } },
        { "id": "coolant", "style": "digital", "x": 40, "y": 118, "width": 220, "height": 66,
          "label": "ENGINE", "unit": "°C", "format": "0", "min": -40, "max": 150,
          "source": { "signal": "engine.coolantTemp", "rateHz": 0.5 },
          "parts": { "align": "left", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 16, "unitColour": "@textMid", "noData": "–" } },
        { "id": "fuel", "style": "digital", "x": 40, "y": 202, "width": 220, "height": 66,
          "label": "FUEL", "unit": "%", "format": "0", "min": 0, "max": 100,
          "source": { "signal": "fuel.levelPercent", "rateHz": 0.2 },
          "parts": { "align": "left", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 16, "unitColour": "@textMid", "noData": "–" } },

        // ── Range and economy ── the same, aligned right. Placeholders until the truck's own are found.
        { "id": "range", "style": "digital", "x": 652, "y": 34, "width": 220, "height": 66,
          "label": "RANGE", "unit": "km", "format": "0", "min": 0, "max": 2000,
          "source": { "signal": "fuel.range", "rateHz": 0.5 },
          "parts": { "align": "right", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 16, "unitColour": "@textMid", "noData": "–" } },
        { "id": "economy", "style": "digital", "x": 652, "y": 118, "width": 220, "height": 66,
          "label": "ECONOMY", "unit": "L/100km", "format": "0.0", "min": 0, "max": 99.9,
          "source": { "signal": "fuel.economy", "rateHz": 1 },
          "parts": { "align": "right", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 16, "unitColour": "@textMid", "noData": "–" } },
        { "id": "outside", "style": "digital", "x": 652, "y": 202, "width": 220, "height": 66,
          "label": "OUTSIDE", "unit": "°", "format": "0", "min": -50, "max": 60,
          "source": { "signal": "ambient.airTemp", "rateHz": 0.1 },
          "parts": { "align": "right", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 34, "valueWeight": "light", "valueColour": "@textHigh", "noData": "–" } },

        // ── Warning lights ── almost invisible until one comes on; then it is the only colour here.
        { "id": "checkEngine", "type": "warning", "x": 283, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "diagnostics.checkEngine", "rateHz": 0.2 }, "parts": { "icon": "checkEngine", "litColour": "#FFB000", "unlitColour": "#14FFFFFF" } },
        { "id": "oil", "type": "warning", "x": 323, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "warning.oilPressure", "rateHz": 0.5 }, "parts": { "icon": "oil", "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "battery", "type": "warning", "x": 363, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "vehicle.controlModuleVoltage", "rateHz": 0.5 }, "parts": { "icon": "battery", "below": 11.8, "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "hot", "type": "warning", "x": 403, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "engine.coolantTemp", "rateHz": 0.5 }, "parts": { "icon": "coolant", "onAt": 112, "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "lowFuel", "type": "warning", "x": 443, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "fuel.levelPercent", "rateHz": 0.2 }, "parts": { "icon": "fuel", "below": 12, "litColour": "#FFB000", "unlitColour": "#14FFFFFF" } },
        { "id": "seatbelt", "type": "warning", "x": 483, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "warning.seatbelt", "rateHz": 0.5 }, "parts": { "icon": "seatbelt", "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "door", "type": "warning", "x": 523, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "warning.doorAjar", "rateHz": 0.5 }, "parts": { "icon": "door", "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "brake", "type": "warning", "x": 563, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "warning.parkingBrake", "rateHz": 0.5 }, "parts": { "icon": "brake", "litColour": "#FF453A", "unlitColour": "#14FFFFFF" } },
        { "id": "tyres", "type": "warning", "x": 603, "y": 250, "width": 26, "height": 26,
          "source": { "signal": "warning.tirePressure", "rateHz": 0.2 }, "parts": { "icon": "tpms", "litColour": "#FFB000", "unlitColour": "#14FFFFFF" } },

        // ── Along the bottom ── small and quiet: the things you look for, not at.
        { "id": "odometer", "style": "digital", "x": 40, "y": 310, "width": 260, "height": 56,
          "label": "ODOMETER", "unit": "km", "format": "#,0", "min": 0, "max": 2000000,
          "source": { "signal": "vehicle.odometer", "rateHz": 0.05 },
          "parts": { "align": "left", "labelSize": 11, "labelWeight": "regular", "labelColour": "@textLow",
                     "valueSize": 20, "valueWeight": "regular", "valueColour": "@textMid", "unitSize": 13, "unitColour": "@textLow", "noData": "–" } },
        { "id": "codes", "style": "digital", "x": 612, "y": 310, "width": 260, "height": 56,
          "label": "STORED CODES", "format": "0", "min": 0, "max": 127,
          "source": { "signal": "diagnostics.dtcCount", "rateHz": 0.1 },
          "parts": { "align": "right", "labelSize": 11, "labelWeight": "regular", "labelColour": "@textLow",
                     "valueSize": 20, "valueWeight": "regular", "valueColour": "@textMid", "noData": "–" } }
      ]
    }
    """;

    /// <summary>The climate layout as text, comments and all — what <c>climate\examples\clean.json</c> holds.</summary>
    internal const string BuiltInClimateJson = """
    {
      "name": "Clean",
      "description": "Type, not boxes: each side's temperature large at its edge, the seat beneath it, the fan and airflow in the middle, and the switches as words that light. Shows what the truck reports; changes nothing.",
      "author": "DashDeck",
      // Black behind; white and grey type from the theme's text colours, so it dims at night with the
      // rest of the dash. Only what is lit — a switch, a heater, a warning — has a colour of its own.
      "background": "#000000",
      "fonts": { "ui": "Segoe UI Variable Display, Segoe UI", "mono": "Segoe UI Variable Text, Segoe UI" },
      "elements": [
        // ── Driver ── at the left edge.
        { "id": "driver", "style": "digital", "x": 40, "y": 30, "width": 240, "height": 108,
          "label": "DRIVER", "unit": "°", "format": "0.0", "min": 10, "max": 35,
          "source": { "signal": "hvac.driverSetTemp", "rateHz": 0.5 },
          "parts": { "align": "left", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 72, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 40, "unitColour": "@textMid", "noData": "–" } },
        // The seat heats (warm, HEAT 1–3) and cools (blue, COOL 1–3); the wheel heats.
        { "id": "driverSeat", "type": "levels", "x": 40, "y": 160, "width": 150, "height": 40,
          "label": "SEAT", "min": -3, "max": 3,
          "source": { "signal": "seat.driver.climate", "rateHz": 0.2 },
          "parts": { "steps": 3, "shape": "dots", "gap": 10, "litColour": "#FF8A3D", "negativeColour": "#4DA3FF", "unlitColour": "@hairlineStrong",
                     "labelColour": "@textMid", "labelSize": 12, "labelWeight": "regular", "positiveText": "HEAT", "negativeText": "COOL" } },
        { "id": "wheel", "type": "indicator", "x": 40, "y": 214, "width": 200, "height": 26, "label": "HEATED WHEEL",
          "source": { "signal": "steeringWheel.heat", "rateHz": 0.2 },
          "parts": { "style": "text", "align": "left", "fontSize": 13, "labelWeight": "regular", "litColour": "#FF8A3D", "unlitColour": "@textFaint" } },

        // ── The middle ── the fan, where the air goes, and the cabin.
        { "id": "fan", "type": "levels", "x": 336, "y": 34, "width": 240, "height": 46,
          "label": "FAN", "min": 0, "max": 7,
          "source": { "signal": "hvac.fanSpeed", "rateHz": 0.5 },
          "parts": { "steps": 7, "shape": "dots", "gap": 12, "litColour": "@textHigh", "unlitColour": "@hairlineStrong",
                     "labelColour": "@textMid", "labelSize": 12, "labelWeight": "regular" } },
        // Airflow is one signal of bits: 1 face, 2 feet, 4 windshield.
        { "id": "face", "type": "indicator", "x": 336, "y": 108, "width": 72, "height": 26, "label": "FACE",
          "source": { "signal": "hvac.airflow", "rateHz": 0.5 },
          "parts": { "bit": 0, "style": "text", "align": "left", "fontSize": 14, "labelWeight": "regular", "litColour": "@textHigh", "unlitColour": "@textFaint" } },
        { "id": "feet", "type": "indicator", "x": 420, "y": 108, "width": 72, "height": 26, "label": "FEET",
          "source": { "signal": "hvac.airflow", "rateHz": 0.5 },
          "parts": { "bit": 1, "style": "text", "align": "center", "fontSize": 14, "labelWeight": "regular", "litColour": "@textHigh", "unlitColour": "@textFaint" } },
        { "id": "glassAir", "type": "indicator", "x": 492, "y": 108, "width": 84, "height": 26, "label": "SCREEN",
          "source": { "signal": "hvac.airflow", "rateHz": 0.5 },
          "parts": { "bit": 2, "style": "text", "align": "right", "fontSize": 14, "labelWeight": "regular", "litColour": "@textHigh", "unlitColour": "@textFaint" } },
        { "id": "cabin", "style": "digital", "x": 336, "y": 160, "width": 240, "height": 66,
          "label": "CABIN", "unit": "°", "format": "0.0", "min": -40, "max": 80,
          "source": { "signal": "hvac.cabinTemp", "rateHz": 0.5 },
          "parts": { "align": "center", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 30, "valueWeight": "light", "valueColour": "@textMid", "noData": "–" } },

        // ── Passenger ── at the right edge, mirrored.
        { "id": "passenger", "style": "digital", "x": 632, "y": 30, "width": 240, "height": 108,
          "label": "PASSENGER", "unit": "°", "format": "0.0", "min": 10, "max": 35,
          "source": { "signal": "hvac.passengerSetTemp", "rateHz": 0.5 },
          "parts": { "align": "right", "labelSize": 12, "labelWeight": "regular", "labelColour": "@textMid",
                     "valueSize": 72, "valueWeight": "light", "valueColour": "@textHigh", "unitSize": 40, "unitColour": "@textMid", "noData": "–" } },
        { "id": "passengerSeat", "type": "levels", "x": 722, "y": 160, "width": 150, "height": 40,
          "label": "SEAT", "min": -3, "max": 3,
          "source": { "signal": "seat.passenger.climate", "rateHz": 0.2 },
          "parts": { "steps": 3, "shape": "dots", "gap": 10, "litColour": "#FF8A3D", "negativeColour": "#4DA3FF", "unlitColour": "@hairlineStrong",
                     "labelColour": "@textMid", "labelSize": 12, "labelWeight": "regular", "positiveText": "HEAT", "negativeText": "COOL" } },

        // ── The switches ── words that light when on, grey when off, with a dash when not known.
        { "id": "auto", "type": "indicator", "x": 40, "y": 316, "width": 100, "height": 32, "label": "AUTO",
          "source": { "signal": "hvac.auto", "rateHz": 0.5 },
          "parts": { "style": "text", "align": "left", "fontSize": 16, "labelWeight": "regular", "litColour": "#4DA3FF", "unlitColour": "@textFaint" } },
        { "id": "ac", "type": "indicator", "x": 150, "y": 316, "width": 100, "height": 32, "label": "A/C",
          "source": { "signal": "hvac.airConditioning", "rateHz": 0.5 },
          "parts": { "style": "text", "align": "left", "fontSize": 16, "labelWeight": "regular", "litColour": "#4DA3FF", "unlitColour": "@textFaint" } },
        { "id": "recirc", "type": "indicator", "x": 260, "y": 316, "width": 120, "height": 32, "label": "RECIRC",
          "source": { "signal": "hvac.recirculate", "rateHz": 0.5 },
          "parts": { "style": "text", "align": "left", "fontSize": 16, "labelWeight": "regular", "litColour": "#4DA3FF", "unlitColour": "@textFaint" } },
        { "id": "frontDefrost", "type": "indicator", "x": 390, "y": 316, "width": 130, "height": 32, "label": "DEFROST",
          "source": { "signal": "hvac.frontDefrost", "rateHz": 0.5 },
          "parts": { "style": "text", "align": "left", "fontSize": 16, "labelWeight": "regular", "litColour": "#FF8A3D", "unlitColour": "@textFaint" } },
        { "id": "rearDefrost", "type": "indicator", "x": 530, "y": 316, "width": 110, "height": 32, "label": "REAR",
          "source": { "signal": "hvac.rearDefrost", "rateHz": 0.5 },
          "parts": { "style": "text", "align": "left", "fontSize": 16, "labelWeight": "regular", "litColour": "#FF8A3D", "unlitColour": "@textFaint" } },
        // Outside air is a standard signal: it reads on the real truck today.
        { "id": "outside", "style": "digital", "x": 692, "y": 300, "width": 180, "height": 60,
          "label": "OUTSIDE", "unit": "°", "format": "0", "min": -50, "max": 60,
          "source": { "signal": "ambient.airTemp", "rateHz": 0.1 },
          "parts": { "align": "right", "labelSize": 11, "labelWeight": "regular", "labelColour": "@textLow",
                     "valueSize": 22, "valueWeight": "regular", "valueColour": "@textMid", "noData": "–" } }
      ]
    }
    """;

    /// <summary>The compass layout as text, comments and all — what <c>stage\examples\compass.json</c> holds.</summary>
    internal const string BuiltInCompassJson = """
    {
      "name": "Compass",
      "description": "Where the truck is pointing, how it is sitting and what it is doing: heading, G, pitch and roll, speed and outside air. Truck first, tablet second, and every reading says which.",
      "author": "DashDeck",
      "elements": [
        // The rose. "parts" tunes it: "mode": "needle" keeps north up; every colour is a part.
        { "id": "heading", "type": "compass", "x": 31, "y": 96, "width": 320, "height": 344,
          "source": { "sensor": "attitude.heading" } },

        // Position from the phone's GPS (ADR-0027). Any gauge can read a sensor like this.
        { "id": "latitude", "style": "digital", "x": 31, "y": 456, "width": 160, "height": 96,
          "label": "LATITUDE", "format": "0.0000",
          "source": { "sensor": "location.latitude" }, "min": -90, "max": 90,
          "parts": { "valueSize": 18, "valueColour": "@textMid" } },
        { "id": "longitude", "style": "digital", "x": 191, "y": 456, "width": 160, "height": 96,
          "label": "LONGITUDE", "format": "0.0000",
          "source": { "sensor": "location.longitude" }, "min": -180, "max": 180,
          "parts": { "valueSize": 18, "valueColour": "@textMid" } },

        // The G meter. "parts": { "range": 0.5 } makes the outer ring half a g.
        { "id": "g", "type": "gMeter", "x": 369, "y": 150, "width": 240, "height": 312 },

        // Pitch and roll need the mount levelled (Settings > Mount); until then they say so.
        { "id": "pitch", "style": "digital", "x": 627, "y": 170, "width": 127, "height": 120,
          "label": "PITCH", "unit": "°", "format": "0.0",
          "source": { "sensor": "attitude.pitch" }, "min": -45, "max": 45,
          "parts": { "valueSize": 30 } },
        { "id": "roll", "style": "digital", "x": 754, "y": 170, "width": 127, "height": 120,
          "label": "ROLL", "unit": "°", "format": "0.0",
          "source": { "sensor": "attitude.roll" }, "min": -45, "max": 45,
          "parts": { "valueSize": 30 } },
        // Speed and outside air are vehicle signals, as on any gauge.
        { "id": "speed", "style": "digital", "x": 627, "y": 320, "width": 127, "height": 120,
          "label": "SPEED", "unit": "km/h", "format": "0",
          "source": { "signal": "vehicle.speed", "rateHz": 1 }, "min": 0, "max": 250,
          "parts": { "valueSize": 30 } },
        { "id": "outside", "style": "digital", "x": 754, "y": 320, "width": 127, "height": 120,
          "label": "OUTSIDE", "unit": "°C", "format": "0",
          "source": { "signal": "ambient.airTemp", "rateHz": 0.1 }, "min": -50, "max": 60,
          "parts": { "valueSize": 30 } }
      ]
    }
    """;

    private const string BuiltInJson = """
    {
      "name": "F-150 cluster",
      "description": "Boost and oil temperature large; volts, intake air, throttle and load small. What the factory cluster leaves out, drawn its way.",
      "author": "DashDeck",
      "elements": [
        {
          "id": "boost", "style": "dial", "x": 134, "y": 28, "width": 300, "height": 300,
          "label": "BOOST", "unit": "psi", "format": "0.0",
          "source": { "signal": "engine.intakeManifoldPressure", "minus": "engine.barometricPressure", "minusFallback": 101.325, "scale": 0.1450377, "rateHz": 3 },
          "min": -15, "max": 25, "majorTick": 5, "minorTick": 2.5,
          "zones": [ { "from": 18, "to": 25, "colour": "#E8531E" } ]
        },
        {
          "id": "oil", "style": "dial", "x": 478, "y": 28, "width": 300, "height": 300,
          "label": "OIL TEMP", "unit": "°C", "format": "0",
          "source": { "signal": "engine.oilTemp", "rateHz": 1 },
          "min": 40, "max": 150, "majorTick": 20, "minorTick": 10,
          "zones": [ { "from": 135, "to": 150, "colour": "#E8531E" } ]
        },
        {
          "id": "volts", "style": "dial", "x": 83, "y": 380, "width": 164, "height": 164,
          "label": "VOLTS", "unit": "V", "format": "0.0",
          "source": { "signal": "vehicle.controlModuleVoltage", "rateHz": 1 },
          "min": 8, "max": 18, "majorTick": 2, "minorTick": 1,
          "parts": { "numerals": false }
        },
        {
          "id": "intake", "style": "dial", "x": 277, "y": 380, "width": 164, "height": 164,
          "label": "INTAKE", "unit": "°C", "format": "0",
          "source": { "signal": "engine.intakeAirTemp", "rateHz": 0.5 },
          "min": -20, "max": 60, "majorTick": 20, "minorTick": 10,
          "parts": { "numerals": false }
        },
        {
          "id": "throttle", "style": "dial", "x": 471, "y": 380, "width": 164, "height": 164,
          "label": "THROTTLE", "unit": "%", "format": "0",
          "source": { "signal": "engine.throttlePosition", "rateHz": 4 },
          "min": 0, "max": 100, "majorTick": 25, "minorTick": 5,
          "parts": { "numerals": false }
        },
        {
          "id": "load", "style": "dial", "x": 665, "y": 380, "width": 164, "height": 164,
          "label": "LOAD", "unit": "%", "format": "0",
          "source": { "signal": "engine.load", "rateHz": 2 },
          "min": 0, "max": 100, "majorTick": 25, "minorTick": 5,
          "parts": { "numerals": false }
        }
      ]
    }
    """;
}

/// <summary>A gauge's reading: the number to draw, and how far to trust it.</summary>
/// <param name="Value">The computed value, or NaN when there is none.</param>
/// <param name="Quality">The worse of the qualities it was computed from.</param>
public readonly record struct GaugeReading(double Value, SignalQuality Quality)
{
    /// <summary>True when there is a number to draw at all — Live, Simulated, or Stale (dimmed).</summary>
    public bool HasValue => !double.IsNaN(Value) && Quality is not SignalQuality.Unavailable;

    /// <summary>
    /// Compute a gauge's reading from its signals.
    /// </summary>
    /// <remarks>
    /// A gauge with no reading draws no needle and no number — never a confident zero. That is
    /// rule 7 in CLAUDE.md, and the old cluster broke it: on a truck without oil temperature the
    /// OIL TEMP needle sat on 40 °C as if the oil were cold.
    /// </remarks>
    public static GaugeReading Compute(GaugeSource source, SignalValue primary, SignalValue? minus)
    {
        if (!Has(primary))
        {
            return new(double.NaN, primary.Quality is SignalQuality.Stale ? SignalQuality.Stale : SignalQuality.Unavailable);
        }

        var value = primary.Value;
        var quality = primary.Quality;

        if (source.Minus is not null)
        {
            if (minus is { } m && Has(m))
            {
                value -= m.Value;
                quality = Worse(quality, m.Quality);
            }
            else if (source.MinusFallback is { } fallback)
            {
                value -= fallback;
            }
            else
            {
                return new(double.NaN, SignalQuality.Unavailable);
            }
        }

        return new((value * source.Scale) + source.Offset, quality);
    }

    /// <summary>
    /// A gauge's reading from a sensor (ADR-0039), scaled and offset like a signal. A sensor reading
    /// that is not usable — no tablet sensor, not levelled — is no reading, never a zero.
    /// </summary>
    public static GaugeReading FromSensor(GaugeSource source, double value, SignalQuality quality) =>
        double.IsNaN(value) || quality is not (SignalQuality.Live or SignalQuality.Simulated)
            ? new(double.NaN, SignalQuality.Unavailable)
            : new((value * source.Scale) + source.Offset, quality);

    /// <summary>A value worth drawing: usable, or stale with a number still in it.</summary>
    private static bool Has(SignalValue v) =>
        !double.IsNaN(v.Value) && v.Quality is SignalQuality.Live or SignalQuality.Simulated or SignalQuality.Stale;

    /// <summary>Stale beats Simulated beats Live: the least trustworthy input wins.</summary>
    private static SignalQuality Worse(SignalQuality a, SignalQuality b)
    {
        static int Rank(SignalQuality q) => q switch
        {
            SignalQuality.Unavailable => 3,
            SignalQuality.Stale => 2,
            SignalQuality.Simulated => 1,
            _ => 0,
        };

        return Rank(a) >= Rank(b) ? a : b;
    }
}
