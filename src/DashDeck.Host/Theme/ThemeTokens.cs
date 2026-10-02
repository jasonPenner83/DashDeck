using System.Globalization;

namespace DashDeck.Host.Theme;

/// <summary>A colour as a theme file writes it — kept free of WPF so the theme model can be tested anywhere.</summary>
public readonly record struct ThemeColour(byte R, byte G, byte B, byte A = 255)
{
    public static ThemeColour Transparent { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Read <c>#RRGGBB</c>, <c>#AARRGGBB</c>, <c>#RGB</c> or <c>transparent</c>. False for anything else.
    /// </summary>
    public static bool TryParse(string? text, out ThemeColour colour)
    {
        colour = default;
        var trimmed = (text ?? "").Trim();

        if (trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            colour = Transparent;
            return true;
        }

        if (!trimmed.StartsWith('#'))
        {
            return false;
        }

        var hex = trimmed[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        }

        if (hex.Length is not (6 or 8) ||
            !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        colour = hex.Length == 6
            ? new ThemeColour((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new ThemeColour((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)(value >> 24));
        return true;
    }

    /// <summary><c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque.</summary>
    public override string ToString() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>Scale brightness, keeping hue and alpha — how night is derived.</summary>
    public ThemeColour Dim(double factor) => new(
        (byte)Math.Clamp(Math.Round(R * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(G * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(B * factor), 0, 255),
        A);

    /// <summary>This colour drawn at <see cref="A"/> over an opaque background.</summary>
    public ThemeColour Over(ThemeColour background)
    {
        var a = A / 255.0;
        return new(
            (byte)Math.Round((R * a) + (background.R * (1 - a))),
            (byte)Math.Round((G * a) + (background.G * (1 - a))),
            (byte)Math.Round((B * a) + (background.B * (1 - a))));
    }

    /// <summary>WCAG relative luminance, 0–1.</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
    }

    /// <summary>WCAG contrast ratio between two opaque colours, 1–21.</summary>
    public static double Contrast(ThemeColour a, ThemeColour b)
    {
        var (light, dark) = (a.RelativeLuminance(), b.RelativeLuminance());
        if (dark > light)
        {
            (light, dark) = (dark, light);
        }

        return (light + 0.05) / (dark + 0.05);
    }
}

/// <summary>What a token holds.</summary>
public enum TokenKind
{
    Colour,
    Number,
    Font,
}

/// <summary>How a colour token's night value is derived when the theme does not give one.</summary>
public enum NightRule
{
    /// <summary>Backgrounds and lines: dimmed hard, so the screen stops being the brightest thing in the cab.</summary>
    Surface,

    /// <summary>Text and glyphs: dimmed less, so contrast survives.</summary>
    Ink,

    /// <summary>The accent, dimmed least — it marks what is selected.</summary>
    Accent,

    /// <summary>Not dimmed: numbers, fonts, and colours that are already dark.</summary>
    Keep,
}

/// <summary>
/// One named value a theme can set.
/// </summary>
/// <param name="Key">The name in a theme file — <c>canvas</c>, <c>buttonRadius</c>.</param>
/// <param name="Kind">Colour, number or font.</param>
/// <param name="Default">The DashDeck look. A value starting <c>@</c> follows another token, the way an HA theme variable can name another.</param>
/// <param name="NightDefault">The DashDeck night value, where the original night palette was tuned by hand rather than derived.</param>
/// <param name="Night">How night is derived when neither the theme nor <paramref name="NightDefault"/> says.</param>
/// <param name="ResourceKey">The application resource the shell reads it from.</param>
/// <param name="Description">What it paints, for the reference in Settings and the docs.</param>
public sealed record ThemeToken(
    string Key,
    TokenKind Kind,
    string Default,
    string? NightDefault,
    NightRule Night,
    string ResourceKey,
    string Description);

/// <summary>
/// The vocabulary a theme is written in (ADR-0036).
/// </summary>
/// <remarks>
/// Modelled on Home Assistant themes: a flat list of named values, every one with a default, so a
/// theme sets only what it changes and the rest falls through to the DashDeck look. A default
/// can name another token (<c>@textLow</c>), so a theme that changes the text ramp carries the
/// captions with it without having to say so.
/// <para>
/// <b>The four signal-quality colours are not here, and never will be</b> (ADR-0013). A stale
/// reading looks stale under every theme.
/// </para>
/// </remarks>
public static class ThemeTokens
{
    public static IReadOnlyList<ThemeToken> All { get; } =
    [
        // Surfaces — the DashDeck night values were tuned by hand, and stay exactly as they were.
        new("canvas", TokenKind.Colour, "#0D0C0B", "#060505", NightRule.Surface, "CanvasBrush", "The screen behind everything."),
        new("surface", TokenKind.Colour, "#191816", "#0E0D0C", NightRule.Surface, "SurfaceBrush", "Cards, panels and list rows."),
        new("raised", TokenKind.Colour, "#211F1C", "#141311", NightRule.Surface, "RaisedBrush", "Things that sit above a surface: text fields, menus, badges."),
        new("hairline", TokenKind.Colour, "#2C2925", "#1B1916", NightRule.Surface, "HairlineBrush", "Dividers and card outlines."),
        new("hairlineStrong", TokenKind.Colour, "#3A3630", "#262220", NightRule.Surface, "HairlineStrongBrush", "Stronger outlines."),

        // The text ramp.
        new("textHigh", TokenKind.Colour, "#F4F1EB", "#B5B0A8", NightRule.Ink, "TextHighBrush", "Headline text."),
        new("textMid", TokenKind.Colour, "#A5A096", "#797469", NightRule.Ink, "TextMidBrush", "Body text and status."),
        new("textLow", TokenKind.Colour, "#6B665E", "#514C45", NightRule.Ink, "TextLowBrush", "Quiet text."),
        new("textFaint", TokenKind.Colour, "#4A453E", "#38332E", NightRule.Ink, "TextFaintBrush", "Fine print."),

        // The accent and what it marks.
        new("accent", TokenKind.Colour, "#FF7A1A", null, NightRule.Accent, "AccentBrush", "What is selected or current. Must stay tellable apart from the quality colours."),
        new("accentWash", TokenKind.Number, "0.08", null, NightRule.Keep, "", "How strongly a selected button is filled with the accent: 0.08 is a tint, 1 is solid."),
        new("selectedText", TokenKind.Colour, "@accent", null, NightRule.Accent, "SelectedTextBrush", "Text on a selected button. Dark, when the wash is solid."),
        new("onAccent", TokenKind.Colour, "#140A02", null, NightRule.Keep, "OnAccentBrush", "Text drawn on solid accent."),

        // Where things sit.
        new("caption", TokenKind.Colour, "@textLow", null, NightRule.Ink, "CaptionBrush", "Small capital labels: card names, section headings."),
        new("stripBackground", TokenKind.Colour, "@canvas", null, NightRule.Surface, "StripBackgroundBrush", "The status strip across the top."),
        new("navBackground", TokenKind.Colour, "@canvas", null, NightRule.Surface, "NavBackgroundBrush", "The navigation bar across the bottom."),
        new("navText", TokenKind.Colour, "@textMid", null, NightRule.Ink, "NavTextBrush", "Navigation labels and icons."),
        new("buttonBackground", TokenKind.Colour, "transparent", null, NightRule.Surface, "ButtonBackgroundBrush", "Buttons and chips when not selected."),
        new("buttonBorder", TokenKind.Colour, "@hairlineStrong", null, NightRule.Surface, "ButtonBorderBrush", "Their outline."),
        new("buttonText", TokenKind.Colour, "@textMid", null, NightRule.Ink, "ButtonTextBrush", "Their text."),

        // Shape.
        new("buttonRadius", TokenKind.Number, "14", null, NightRule.Keep, "ButtonRadius", "Corner radius of buttons and chips. Half their height (28) makes a pill."),
        new("cardRadius", TokenKind.Number, "14", null, NightRule.Keep, "CardRadius", "Corner radius of dash cards."),
        new("panelRadius", TokenKind.Number, "12", null, NightRule.Keep, "PanelRadius", "Corner radius of settings rows and panels."),
        new("borderWidth", TokenKind.Number, "1", null, NightRule.Keep, "BorderWidth", "Outline width of buttons, chips and fields."),

        // Lettering.
        new("uiFont", TokenKind.Font, "Archivo, Segoe UI", null, NightRule.Keep, "UiFont", "Values and headlines."),
        new("monoFont", TokenKind.Font, "IBM Plex Mono, Consolas", null, NightRule.Keep, "MonoFont", "Labels, captions and buttons."),
    ];

    private static readonly Dictionary<string, ThemeToken> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string key, out ThemeToken token) => ByKey.TryGetValue(key, out token!);

    /// <summary>The factor a night rule dims by.</summary>
    public static double Factor(NightRule rule) => rule switch
    {
        NightRule.Surface => 0.55,
        NightRule.Ink => 0.74,
        NightRule.Accent => 0.82,
        _ => 1.0,
    };

    /// <summary>The range a number token may take. Outside it the theme's value is refused, not clamped.</summary>
    public static (double Min, double Max) Range(string key) => key switch
    {
        "accentWash" => (0, 1),
        "borderWidth" => (0, 6),
        _ => (0, 60),
    };
}
