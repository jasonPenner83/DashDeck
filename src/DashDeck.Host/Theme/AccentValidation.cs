using System.Globalization;
using System.Windows.Media;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Theme;

/// <summary>The result of checking a proposed accent.</summary>
/// <param name="IsUsable">False when the colour would break something.</param>
/// <param name="Message">Why, in words a person can act on. Empty when it is fine.</param>
public sealed record AccentCheck(bool IsUsable, string Message);

/// <summary>
/// Decides whether a colour can be the accent.
/// </summary>
/// <remarks>
/// ADR-0013 banned a colour picker because the failure mode is silent: someone picks a green
/// two shades from <c>Live</c>, or a yellow indistinguishable from <c>Stale</c>, and the
/// distinction the dash is most careful about quietly stops working. That reasoning was about
/// the <em>silence</em>, not about custom colours as such — so ADR-0014 allows custom accents,
/// and this is what stops them being silent.
/// <para>
/// Two rules. It must be far enough in hue from the four quality colours to stay
/// distinguishable, and bright enough to read on the dark canvas.
/// </para>
/// </remarks>
public static class AccentValidation
{
    /// <summary>Below this the accent disappears against the canvas.</summary>
    private const double MinimumLuminance = 0.18;

    /// <summary>
    /// How many degrees of hue an accent must keep from each quality colour.
    /// </summary>
    /// <remarks>
    /// <b>Calibrated against Ember</b>, the tightest colour already on the dash: it sits
    /// 18.02° from <c>Stale</c>, so the floor is 18 and Ember is the boundary case. The rule
    /// is therefore "no closer than something that already ships", which is a claim that can
    /// be checked rather than a number picked by feel. The first attempt picked 25° by feel
    /// and rejected Ember itself.
    /// <para>
    /// Deriving it from the presets at runtime was the other option and is worse: a careless
    /// new preset would silently lower the bar for everyone. A constant plus
    /// <c>AccentValidationTests</c> fails the build instead.
    /// </para>
    /// </remarks>
    private const double MinimumHueSeparation = 18;

    /// <summary>Parse <c>#RRGGBB</c> (with or without the hash), or fail politely.</summary>
    public static bool TryParse(string? text, out Color colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('#');

        if (trimmed.Length != 6 ||
            !int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        colour = Color.FromRgb(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));

        return true;
    }

    /// <summary>Check a colour against both rules.</summary>
    public static AccentCheck Check(Color colour)
    {
        if (Luminance(colour) < MinimumLuminance)
        {
            return new AccentCheck(false, "Too dark to read on the dash background.");
        }

        foreach (var (name, quality) in QualityColours())
        {
            var separation = HueSeparation(Hue(colour), Hue(quality));

            // Strictly less, so a preset sitting exactly on the floor still passes its own rule.
            if (separation < MinimumHueSeparation)
            {
                return new AccentCheck(
                    false,
                    $"Too close to the {name} colour — they need to stay tellable apart.");
            }
        }

        return new AccentCheck(true, string.Empty);
    }

    /// <summary>
    /// The four semantic colours an accent must not be confused with, read from the same
    /// place the dash renders them so the two can never drift apart.
    /// </summary>
    private static IEnumerable<(string Name, Color Colour)> QualityColours()
    {
        yield return ("live", QualityPalette.Live.Color);
        yield return ("stale", QualityPalette.Stale.Color);
        yield return ("simulated", QualityPalette.Simulated.Color);
        yield return ("fault", QualityPalette.Fault.Color);
    }

    /// <summary>Hue in degrees, 0-360.</summary>
    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        if (delta < 1e-6)
        {
            return 0;
        }

        var hue = max == r
            ? 60 * (((g - b) / delta) % 6)
            : max == g
                ? 60 * (((b - r) / delta) + 2)
                : 60 * (((r - g) / delta) + 4);

        return hue < 0 ? hue + 360 : hue;
    }

    /// <summary>Shortest distance around the hue circle.</summary>
    private static double HueSeparation(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360;
        return diff > 180 ? 360 - diff : diff;
    }

    /// <summary>Relative luminance, the usual perceptual weighting.</summary>
    private static double Luminance(Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;
}
