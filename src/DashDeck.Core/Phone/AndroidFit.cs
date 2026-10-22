using System.Globalization;

namespace DashDeck.Core.Phone;

/// <summary>
/// What the phone's screen is told while it is mirrored, so its picture fills a region of the dash
/// exactly (ADR-0076): a size for <c>wm size</c> and a density for <c>wm density</c>.
/// </summary>
/// <remarks>
/// scrcpy mirrors the screen Android was told it has, not its panel, so the picture is that size and
/// shape edge to edge — no turning, no crop. The density makes <b>Android's units the dash's own</b>:
/// one dp is drawn the size of one of the dash's units, whatever the placement, so switching between
/// full screen and the stage changes how much Android shows, never how big it is. At 600 dp or more on
/// its short side Android lays itself out as a tablet.
/// </remarks>
/// <param name="Width">The size for <c>wm size</c>.</param>
/// <param name="Height">See <paramref name="Width"/>.</param>
/// <param name="Density">For <c>wm density</c>.</param>
public sealed record AndroidFit(int Width, int Height, int Density)
{
    /// <summary>Android's density for one dp to one of WPF's units at 100 %: Android's own baseline.</summary>
    private const double BaselineDensity = 160;

    /// <summary>
    /// The fit for a region: its own shape, in pixels at most <paramref name="maxSide"/> on the long
    /// side, sides a multiple of eight as the phone's encoder wants them. Null when a size is unknown.
    /// </summary>
    /// <param name="regionWidth">The region the picture is drawn in, in screen pixels.</param>
    /// <param name="regionHeight">See <paramref name="regionWidth"/>.</param>
    /// <param name="dpiScale">Screen pixels to one of WPF's units: 2 on the Surface at 200 %.</param>
    public static AndroidFit? For(int regionWidth, int regionHeight, double dpiScale, int maxSide)
    {
        if (regionWidth <= 0 || regionHeight <= 0 || !(dpiScale > 0) || maxSide < 320)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(regionWidth, regionHeight));
        var width = Math.Max(8, (int)Math.Round(regionWidth * scale / 8) * 8);
        var height = Math.Max(8, (int)Math.Round(regionHeight * scale / 8) * 8);

        // The picture's pixels are scaled by region ÷ width on the way to the screen, so Android is
        // told a density that much smaller.
        var density = Math.Clamp((int)Math.Round(BaselineDensity * dpiScale * width / regionWidth), 72, 640);

        return new AndroidFit(width, height, density);
    }

    /// <summary>The two ADB shell commands that tell the phone this, for the device with that serial.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AdbCommands(string serial) =>
    [
        ["-s", serial, "shell", "wm", "size", string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}")],
        ["-s", serial, "shell", "wm", "density", Density.ToString(CultureInfo.InvariantCulture)],
    ];

    /// <summary>One line for the hold menu and <c>--shot</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height} at {Density} dpi");
}
