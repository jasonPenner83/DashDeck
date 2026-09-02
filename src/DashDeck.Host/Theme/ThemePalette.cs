using System.Windows.Media;

namespace DashDeck.Host.Theme;

/// <summary>
/// One complete set of surface and text tokens.
/// </summary>
/// <remarks>
/// Day and Night are both dark. That is not an oversight: Q9 settled that a dash used in a
/// truck is dark in practice, and a light theme is a second design to maintain for a case
/// that may never occur. What Night changes is <em>luminance</em> — the automotive
/// convention, where night mode exists to stop the screen dazzling the driver rather than to
/// look different.
/// <para>
/// The four quality colours are deliberately <b>not</b> in here. Live, Stale, Unavailable
/// and Simulated are semantic constants: a reading that is stale should look identical
/// whatever else has been adjusted, or the one rule this project will not bend — that
/// quality is always rendered honestly — starts depending on a setting.
/// </para>
/// </remarks>
public sealed record ThemePalette(
    Color Canvas,
    Color Surface,
    Color Raised,
    Color Hairline,
    Color HairlineStrong,
    Color TextHigh,
    Color TextMid,
    Color TextLow,
    Color TextFaint)
{
    /// <summary>Full brightness. The palette the design system was drawn against.</summary>
    public static ThemePalette Day { get; } = new(
        Canvas: Hex("#0D0C0B"),
        Surface: Hex("#191816"),
        Raised: Hex("#211F1C"),
        Hairline: Hex("#2C2925"),
        HairlineStrong: Hex("#3A3630"),
        TextHigh: Hex("#F4F1EB"),
        TextMid: Hex("#A5A096"),
        TextLow: Hex("#6B665E"),
        TextFaint: Hex("#4A453E"));

    /// <summary>
    /// Dimmed for darkness. Surfaces drop further than text, so contrast survives while the
    /// screen stops being the brightest thing in the cab.
    /// </summary>
    public static ThemePalette Night { get; } = new(
        Canvas: Hex("#060505"),
        Surface: Hex("#0E0D0C"),
        Raised: Hex("#141311"),
        Hairline: Hex("#1B1916"),
        HairlineStrong: Hex("#262220"),
        TextHigh: Hex("#B5B0A8"),
        TextMid: Hex("#797469"),
        TextLow: Hex("#514C45"),
        TextFaint: Hex("#38332E"));

    internal static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

    /// <summary>Scale a colour's luminance, keeping its hue. Used to dim accents at night.</summary>
    internal static Color Dim(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));
}

/// <summary>
/// A choosable accent.
/// </summary>
/// <remarks>
/// A curated list rather than a colour picker, and B1 says why: the accent has to stay
/// distinguishable from the four quality colours, and contrast has to survive whatever is
/// chosen. A free picker lets someone select a green two shades off <c>Live</c> and quietly
/// break the thing the dash is most careful about.
/// <para>
/// These are spaced around the hue circle away from green (Live, ~145°), amber (Stale,
/// ~45°), blue (Simulated, ~230°) and red (Fault, ~5°). Ember is the tightest fit, sitting
/// between red and amber — it works because quality colours only ever appear as small chips
/// paired with a word, never as colour alone.
/// </para>
/// </remarks>
public sealed record AccentOption(string Name, Color Colour)
{
    public static AccentOption Ember { get; } = new("EMBER", ThemePalette.Hex("#FF7A1A"));

    public static IReadOnlyList<AccentOption> All { get; } =
    [
        Ember,
        new("CYAN", ThemePalette.Hex("#26C6DA")),
        new("VIOLET", ThemePalette.Hex("#B07BFF")),
        new("MAGENTA", ThemePalette.Hex("#F062B0")),
    ];
}
