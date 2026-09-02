namespace DashDeck.Host;

/// <summary>
/// The six-band grid, in one place (B2).
/// </summary>
/// <remarks>
/// Derived for the Surface Pro 7's 912 × 1368 portrait screen, and it divides exactly:
/// 90 status strip + six 185 bands + 168 navigation = 1368.
/// <para>
/// Within a band region, widget rows are <b>155</b> with a <b>20</b> gutter above, between
/// and below. At two rows that fills two bands exactly (2 × 155 + 3 × 20 = 370), which is
/// where the numbers came from — but see <see cref="RowsIn"/>, because the identity holds
/// only at two and the widget region is no longer always two bands. The bottom gutter is
/// the point of the arithmetic: without it the cards sit hard against the navigation strip.
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

    // ---- The widget grid inside a band region ----

    /// <summary>Space either side of the widget columns.</summary>
    public const double SideMargin = 31;

    /// <summary>The gap between columns, between rows, and above and below them.</summary>
    public const double Gutter = 20;

    /// <summary>One widget column. Three fit across the design width.</summary>
    public const double ColumnWidth = 270;

    /// <summary>How many columns fit across. A card is one or two of them.</summary>
    public const int ColumnsPerRow = 3;

    /// <summary>One widget row.</summary>
    public const double WidgetRowHeight = 155;

    /// <summary>The width of one page of cards: the design width less both margins.</summary>
    public const double PageWidth = DesignWidth - (2 * SideMargin);

    /// <summary>How wide a card spanning <paramref name="columns"/> columns is, gutters included.</summary>
    public static double CardWidth(int columns) =>
        (columns * ColumnWidth) + ((columns - 1) * Gutter);

    /// <summary>
    /// How many widget rows fit in <paramref name="bands"/> bands.
    /// </summary>
    /// <remarks>
    /// <b>The original comment claimed this divides exactly. It does so only at two rows.</b>
    /// Rows are <c>155</c> with a <c>20</c> gutter above, between and below, so <i>n</i> rows
    /// need <c>175n + 20</c> — which equals <c>185n</c> only when <i>n</i> is 2. That was
    /// fine while the widget region was always two bands; it stops being fine now that a
    /// three-band stage leaves three.
    /// <para>
    /// So the row height stays fixed and the leftover goes to the bottom gutter: three rows
    /// in three bands use 545 of 555 and the cards keep the same size they have everywhere
    /// else. A card that changed height depending on what was on the stage would be worse
    /// than ten spare pixels.
    /// </para>
    /// <para>
    /// Never returns zero. One band cannot actually fit a row, and rendering a clipped card
    /// still says more than rendering nothing — no occupant asks for five bands today, so
    /// this is a floor rather than a case.
    /// </para>
    /// </remarks>
    public static int RowsIn(int bands) =>
        Math.Max(1, (int)Math.Floor((Height(bands) - Gutter) / (WidgetRowHeight + Gutter)));
}
