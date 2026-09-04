namespace DashDeck.Host;

/// <summary>
/// The six-band grid, in one place (B2).
/// </summary>
/// <remarks>
/// Derived for the Surface Pro 7's 912 × 1368 portrait screen, and it divides exactly:
/// 90 status strip + six 195 bands + 108 navigation = 1368.
/// <para>
/// Within a band region, widget rows are <b>165</b> with a <b>20</b> gutter above, between
/// and below. At two rows that fills two bands exactly (2 × 165 + 3 × 20 = 390), which is
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

    /// <summary>
    /// Navigation, below the bands and in the only band reachable while driving.
    /// </summary>
    /// <remarks>
    /// Was 168, which measured fine on a desk and looked enormous in the truck — four buttons
    /// 228 wide and 168 tall is a target roughly the size of a hand. 108 with 130-wide cells
    /// leaves each button comfortably above the touch floor and fits <b>seven</b> across
    /// instead of four, which pushes B3's unsolved overflow problem from about five
    /// destinations to about seven.
    /// <para>
    /// The 60 pixels it gave back had to go somewhere: the design height is fixed at 1368 and
    /// the status strip is already as small as it reads well at. So the bands took it.
    /// </para>
    /// </remarks>
    public const double NavigationHeight = 108;

    /// <summary>
    /// One band: a widget row plus its gutter.
    /// </summary>
    /// <remarks>
    /// 195, up from 185, absorbing what the navigation strip gave back:
    /// 90 + 6 × 195 + 108 = 1368, still exactly.
    /// </remarks>
    public const double BandHeight = 195;

    /// <summary>How many bands there are. The stage and whatever sits below it share these.</summary>
    public const int BandCount = 6;

    /// <summary>
    /// How many bands the stage takes. Always four.
    /// </summary>
    /// <remarks>
    /// It used to be whatever the occupant asked for — three for video, four for a map — and
    /// on the road that was the single most distracting thing about the dash: the cards below
    /// jumped between two rows and three every time the stage changed, so the thing you were
    /// reading moved. An occupant that wants less picture takes an action bar instead, which
    /// is a fixed band and leaves the layout still.
    /// </remarks>
    public const int StageBands = 4;


    /// <summary>Height of <paramref name="bands"/> bands.</summary>
    public static double Height(int bands) => bands * BandHeight;

    // ---- The widget grid inside a band region ----

    /// <summary>Space either side of the widget columns.</summary>
    public const double SideMargin = 31;

    /// <summary>The gap between columns, between rows, and above and below them.</summary>
    public const double Gutter = 20;

    /// <summary>One widget column. Five fit across the design width.</summary>
    /// <remarks>
    /// 154, down from 270 when three columns fit: <c>31 + 5 × 154 + 4 × 20 + 31 = 912</c>,
    /// still dividing the design width exactly with the margins and gutter unchanged. The
    /// denser grid was a deliberate call to see more cards at once (a driver who has built a
    /// 5 × 3 dash wants the readings in front of them, not a page-flip away), traded against a
    /// narrower card — which is why the card face shrank its value text to match.
    /// </remarks>
    public const double ColumnWidth = 154;

    /// <summary>How many columns fit across. A card is one or two of them.</summary>
    public const int ColumnsPerRow = 5;

    /// <summary>
    /// One widget row.
    /// </summary>
    /// <remarks>
    /// 103, down from 165, so <b>three</b> rows fit the two bands the dash gets behind the
    /// fixed four-band stage (ADR-0018): <c>3 × 103 + 4 × 20 = 389</c> of the 390, the spare
    /// pixel falling into the bottom gutter. The stage stays its full four bands by choice —
    /// the third row was bought from the cards' own height, not from the picture — so the card
    /// is shorter than it was and the face is drawn tighter to suit.
    /// </remarks>
    public const double WidgetRowHeight = 103;

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
    /// Rows are <c>165</c> with a <c>20</c> gutter above, between and below, so <i>n</i> rows
    /// need <c>185n + 20</c> — which equals <c>195n</c> only when <i>n</i> is 2. That was
    /// fine while the widget region was always two bands; it stops being fine now that a
    /// three-band stage leaves three.
    /// <para>
    /// So the row height stays fixed and the leftover goes to the bottom gutter: three rows
    /// in three bands use 575 of 585 and the cards keep the same size they have everywhere
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
