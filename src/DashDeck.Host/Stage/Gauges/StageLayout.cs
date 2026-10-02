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
}

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
/// Where a gauge's number comes from: one signal, optionally minus another, scaled.
/// </summary>
/// <remarks>
/// Enough for the derived readings a cluster shows — boost is manifold pressure minus barometric,
/// in psi — without a formula language. Anything cleverer is a component (ADR-0023).
/// </remarks>
public sealed record GaugeSource
{
    /// <summary>The catalog signal id, e.g. <c>engine.intakeManifoldPressure</c>.</summary>
    public string Signal { get; init; } = "";

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
        },
    };

    public static bool Knows(GaugeStyle style, string part) =>
        Common.ContainsKey(part) || (ByStyle.TryGetValue(style, out var parts) && parts.ContainsKey(part));
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
    /// <summary>The stage's size in its own pixels. Positions are in these.</summary>
    public const double StageWidth = 912;

    /// <inheritdoc cref="StageWidth"/>
    public const double StageHeight = 636;

    public required string Name { get; init; }

    public string Description { get; init; } = "";

    public string Author { get; init; } = "";

    /// <summary>Behind the gauges: <c>#RRGGBB</c>, <c>@token</c>, or null for the theme's canvas.</summary>
    public string? Background { get; init; }

    /// <summary>Everything on the stage, drawn in order — later elements on top.</summary>
    public IReadOnlyList<GaugeSpec> Elements { get; init; } = [];

    /// <summary>The elements that are gauges.</summary>
    [JsonIgnore]
    public IEnumerable<GaugeSpec> Gauges => Elements.Where(e => e.Type is StageElementType.Gauge);

    [JsonIgnore]
    public LayoutOrigin Origin { get; init; } = LayoutOrigin.BuiltIn;

    [JsonIgnore]
    public string? FilePath { get; init; }

    /// <summary>The file name without extension, lower case — what a theme names (<c>"gaugeLayout": "lcars"</c>).</summary>
    [JsonIgnore]
    public string Slug => FilePath is null ? "default" : Path.GetFileNameWithoutExtension(FilePath).ToLowerInvariant();

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
    public static StageLayout Parse(string json, string? filePath = null, LayoutOrigin origin = LayoutOrigin.Yours)
    {
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

            if (Refusal(gauge) is { } refusal)
            {
                problems.Add($"{name}: {refusal} — left out");
                continue;
            }

            problems.AddRange(Warnings(gauge).Select(w => $"{name}: {w}"));
            kept.Add(gauge);
        }

        return layout with
        {
            Name = layout.Name.Trim(),
            Elements = kept,
            Problems = problems,
            FilePath = filePath,
            Origin = origin,
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
            case StageElementType.Panel when ParseRadius(g.Radius) is null:
                return $"radius '{g.Radius}' should be one number or four, comma-separated";
            case StageElementType.Text or StageElementType.Panel:
                return null;
        }

        if (string.IsNullOrWhiteSpace(g.Source.Signal))
        {
            return "no source signal";
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
    private static IEnumerable<string> Warnings(GaugeSpec g)
    {
        if (g.X < 0 || g.Y < 0 || g.X + g.Width > StageWidth + 0.5 || g.Y + g.Height > StageHeight + 0.5)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"reaches outside the {StageWidth} × {StageHeight} stage and will be cut off");
        }

        if (g.Colour is { } colour && !IsColour(colour))
        {
            yield return $"colour '{colour}' is not a colour (#RRGGBB or @token) — using the default";
        }

        if (g.Type is not StageElementType.Gauge)
        {
            yield break;
        }

        foreach (var part in g.Parts.Keys.Where(p => !GaugeParts.Knows(g.Style, p)))
        {
            yield return $"'{part}' is not a part of a {g.Style.ToString().ToLowerInvariant()} gauge — ignored";
        }

        foreach (var (part, value) in g.Parts)
        {
            if (part.EndsWith("Colour", StringComparison.Ordinal) || part is "face" or "needleGlow")
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
