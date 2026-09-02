namespace DashDeck.Host;

/// <summary>
/// The six-band grid, in one place (B2).
/// </summary>
/// <remarks>
/// Derived for the Surface Pro 7's 912 × 1368 portrait screen, and it divides exactly:
/// 90 status strip + six 185 bands + 168 navigation = 1368.
/// <para>
/// Within a band region, widget rows are <b>155</b> with a <b>20</b> gutter above, between
/// and below — so <i>n</i> rows and <i>n</i>+1 gutters fill <i>n</i> bands exactly
/// (2 × 155 + 3 × 20 = 370 = two bands). The bottom gutter is the point: without it the
/// cards sit hard against the navigation strip.
/// </para>
/// <para>
/// These are the numbers the design system is drawn against. If the tablet ever changes,
/// this is the file to re-derive — not a dozen literals scattered through XAML.
/// </para>
/// </remarks>
public static class BandGrid
{
    /// <summary>Portrait width in device-independent pixels.</summary>
    public const double DesignWidth = 912;

    /// <summary>Portrait height in device-independent pixels.</summary>
    public const double DesignHeight = 1368;

    /// <summary>The status strip, above the bands.</summary>
    public const double StatusStripHeight = 90;

    /// <summary>Navigation, below the bands and in the only band reachable while driving.</summary>
    public const double NavigationHeight = 168;

    /// <summary>One band: a widget row plus its gutter.</summary>
    public const double BandHeight = 185;

    /// <summary>How many bands there are. The stage and whatever sits below it share these.</summary>
    public const int BandCount = 6;

    /// <summary>Height of <paramref name="bands"/> bands.</summary>
    public static double Height(int bands) => bands * BandHeight;
}
