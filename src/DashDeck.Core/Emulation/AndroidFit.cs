using System.Globalization;

namespace DashDeck.Core.Emulation;

/// <summary>
/// What Android is told its screen is, so its picture fills a region of the dash exactly
/// (ADR-0075 addendum): a size and a density for <c>wm size</c> and <c>wm density</c>, and where that
/// picture sits in the emulator's frame.
/// </summary>
/// <remarks>
/// The emulator's own screen is fixed when it starts and gRPC cannot resize it, but Android can be
/// told to use part of it. It then draws a picture of the region's shape in the middle of its screen,
/// black either side, and maps touch into it the same way, so the frame is cropped to that part and
/// a touch on the crop goes back through it.
/// <para>
/// The density makes <b>Android's units the dash's own</b>: one dp is drawn the size of one of the
/// dash's units, whatever the placement. So switching between full screen and the stage changes how
/// much Android shows, never how big it is.
/// </para>
/// </remarks>
/// <param name="Width">The size for <c>wm size</c>, in the device's natural orientation — unturned.</param>
/// <param name="Height">See <paramref name="Width"/>.</param>
/// <param name="Density">For <c>wm density</c>.</param>
/// <param name="UprightAspect">The picture's width over its height as it is drawn — turned upright.</param>
/// <param name="Left">Where the picture starts, as fractions of the upright frame.</param>
/// <param name="Top">See <paramref name="Left"/>.</param>
/// <param name="Across">How much of the upright frame it takes, as fractions.</param>
/// <param name="Down">See <paramref name="Across"/>.</param>
public sealed record AndroidFit(int Width, int Height, int Density, double UprightAspect, double Left, double Top, double Across, double Down)
{
    /// <summary>Android's density for one dp to one of WPF's units at 100 %: Android's own baseline.</summary>
    private const double BaselineDensity = 160;

    /// <summary>
    /// The fit for a region, or null when the device's size or the region's is not known yet.
    /// </summary>
    /// <param name="deviceWidth">The emulator's own screen, unturned (what touch is given in).</param>
    /// <param name="deviceHeight">See <paramref name="deviceWidth"/>.</param>
    /// <param name="turned">True when the device is on its side (<see cref="AndroidChoice.TurnFor"/> said 90).</param>
    /// <param name="regionWidth">The region the picture is drawn in, in screen pixels.</param>
    /// <param name="regionHeight">See <paramref name="regionWidth"/>.</param>
    /// <param name="dpiScale">Screen pixels to one of WPF's units: 2 on the Surface at 200 %.</param>
    public static AndroidFit? For(int deviceWidth, int deviceHeight, bool turned, int regionWidth, int regionHeight, double dpiScale)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0 || regionWidth <= 0 || regionHeight <= 0 || !(dpiScale > 0))
        {
            return null;
        }

        var (uprightWidth, uprightHeight) = turned ? (deviceHeight, deviceWidth) : (deviceWidth, deviceHeight);
        var region = (double)regionWidth / regionHeight;

        // The largest picture of the region's shape that fits on the device's screen, in its pixels.
        int width, height;
        if ((double)uprightWidth / uprightHeight > region)
        {
            height = uprightHeight;
            width = Math.Min(uprightWidth, (int)Math.Round(uprightHeight * region));
        }
        else
        {
            width = uprightWidth;
            height = Math.Min(uprightHeight, (int)Math.Round(uprightWidth / region));
        }

        // One dp drawn as one of the dash's units: the picture's pixels are scaled by region ÷ width
        // on the way to the screen, so Android is told a density that much smaller.
        var density = (int)Math.Round(BaselineDensity * dpiScale * width / regionWidth);
        density = Math.Clamp(density, 72, 640);

        return new AndroidFit(
            turned ? height : width,
            turned ? width : height,
            density,
            (double)width / height,
            (uprightWidth - width) / 2.0 / uprightWidth,
            (uprightHeight - height) / 2.0 / uprightHeight,
            (double)width / uprightWidth,
            (double)height / uprightHeight);
    }

    /// <summary>True when the picture is the whole frame, so there is nothing to crop.</summary>
    public bool IsWhole => Across > 0.999 && Down > 0.999;

    /// <summary>A point on the picture, as fractions of it, as fractions of the whole upright frame.</summary>
    public (double X, double Y) ToFrame(double x, double y) => (Left + (x * Across), Top + (y * Down));

    /// <summary>
    /// The picture's pixels in a frame of this size: the largest of the picture's shape, centred —
    /// which is where Android put it, scaled with the frame.
    /// </summary>
    public (int X, int Y, int Width, int Height) Crop(int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        int width, height;
        if ((double)frameWidth / frameHeight > UprightAspect)
        {
            height = frameHeight;
            width = Math.Min(frameWidth, (int)Math.Round(frameHeight * UprightAspect));
        }
        else
        {
            width = frameWidth;
            height = Math.Min(frameHeight, (int)Math.Round(frameWidth / UprightAspect));
        }

        return ((frameWidth - width) / 2, (frameHeight - height) / 2, width, height);
    }

    /// <summary>The two ADB shell commands that tell Android this, for the device with that serial.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AdbCommands(string serial) =>
    [
        ["-s", serial, "shell", "wm", "size", string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}")],
        ["-s", serial, "shell", "wm", "density", Density.ToString(CultureInfo.InvariantCulture)],
    ];

    /// <summary>One line for <c>--shot</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height} at {Density} dpi");
}
