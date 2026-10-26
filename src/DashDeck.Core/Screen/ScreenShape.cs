namespace DashDeck.Core.Screen;

/// <summary>A rectangle on the design surface, in device-independent pixels.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>
/// Which way up the tablet is, and where the stage and the region below it go (ADR-0077).
/// </summary>
/// <remarks>
/// Portrait is the dash as designed (B2): the stage on top, four bands tall, and the region —
/// the console, the climate panel, the map — in the two bands under it. Landscape keeps the stage
/// its full portrait width and puts the region beside it as a column, so turning the tablet
/// loses only a little of the picture's height and nothing gets shrunk to fit. A full-screen view
/// (Settings, the card editor, the map or Android at full size) has no stage, and the region is
/// the whole body either way.
/// <para>
/// Pure, so it is tested here; the shell turns it into a panel.
/// </para>
/// </remarks>
public static class ScreenShape
{
    /// <summary>
    /// How much longer one side has to be before the shape counts as changed. A near-square
    /// window, or one caught half way through a rotation, keeps whatever it was.
    /// </summary>
    public const double Hysteresis = 1.1;

    /// <summary>
    /// Whether a screen this size is landscape. Clearly wider is; clearly taller is not; anything
    /// in between stays <paramref name="wasLandscape"/>, so the dash does not flip back and forth.
    /// </summary>
    public static bool IsLandscape(double width, double height, bool wasLandscape)
    {
        if (width <= 0 || height <= 0)
        {
            return wasLandscape;
        }

        if (width > height * Hysteresis)
        {
            return true;
        }

        if (height > width * Hysteresis)
        {
            return false;
        }

        return wasLandscape;
    }

    /// <summary>
    /// Where the stage and the region go in a body <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <param name="landscape">Side by side rather than one above the other.</param>
    /// <param name="stageShown">False for a full-screen view: the region takes the whole body.</param>
    /// <param name="width">The body's width.</param>
    /// <param name="height">The body's height.</param>
    /// <param name="stageHeight">The stage's height in portrait.</param>
    /// <param name="stageWidth">The stage's width in landscape.</param>
    public static (Box Stage, Box Region) Split(bool landscape, bool stageShown, double width, double height, double stageHeight, double stageWidth)
    {
        if (!stageShown)
        {
            return landscape
                ? (new Box(0, 0, 0, height), new Box(0, 0, width, height))
                : (new Box(0, 0, width, 0), new Box(0, 0, width, height));
        }

        if (landscape)
        {
            var stage = Math.Clamp(stageWidth, 0, width);
            return (new Box(0, 0, stage, height), new Box(stage, 0, width - stage, height));
        }

        var top = Math.Clamp(stageHeight, 0, height);
        return (new Box(0, 0, width, top), new Box(0, top, width, height - top));
    }
}
