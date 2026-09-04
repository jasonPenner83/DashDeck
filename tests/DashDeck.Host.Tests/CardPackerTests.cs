using DashDeck.Host;
using DashDeck.Host.Dash;

namespace DashDeck.Host.Tests;

/// <summary>
/// How cards flow into rows and rows into pages.
/// </summary>
/// <remarks>
/// Worth testing on its own because nothing stores a row or a page number: the arrangement is
/// one ordered list plus the space available, so every question about where a card ends up is
/// answered here. It also needs no dispatcher, no vehicle and no window, which is the whole
/// reason it was written as a static function rather than folded into the view-model.
/// </remarks>
public sealed class CardPackerTests
{
    private sealed class Slot(int columns) : IDashSlot
    {
        public int Columns => columns;

        public double Width => BandGrid.CardWidth(columns);
    }

    private static IReadOnlyList<IDashSlot> Singles(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => new Slot(1))];

    [Fact]
    public void Five_single_cards_fill_one_row()
    {
        var pages = CardPacker.Pack(Singles(5), rowsPerPage: 2);

        Assert.Single(pages);
        Assert.Single(pages[0].Rows);
        Assert.Equal(5, pages[0].Rows[0].Slots.Count);
    }

    [Fact]
    public void The_shipped_six_fit_one_page_behind_a_four_band_stage()
    {
        // The dash as it ships, in the space it ships in. If this ever needs two pages,
        // something has changed that the user will notice immediately.
        var pages = CardPacker.Pack(Singles(6), BandGrid.RowsIn(2));

        Assert.Single(pages);
        Assert.Equal(2, pages[0].Rows.Count);
    }

    [Fact]
    public void A_wide_card_takes_two_columns_so_three_singles_finish_the_row()
    {
        // Five columns: a two-column card leaves three, so it plus three singles is a full row
        // and a fourth single wraps.
        var pages = CardPacker.Pack(
            [new Slot(2), new Slot(1), new Slot(1), new Slot(1), new Slot(1)], rowsPerPage: 2);

        Assert.Equal(4, pages[0].Rows[0].Slots.Count);
        Assert.Single(pages[0].Rows[1].Slots);
    }

    /// <summary>
    /// The rule that keeps the order you set the order you see. A wide card that does not
    /// fit starts a new row; it must not be swapped with a later card that would have.
    /// </summary>
    [Fact]
    public void A_wide_card_that_does_not_fit_starts_a_new_row_rather_than_being_reordered()
    {
        var wide = new Slot(2);
        // Four singles fill four of the five columns; the wide card needs two and cannot take
        // the one that is left, so it starts the next row rather than letting the later single
        // jump ahead of it.
        var pages = CardPacker.Pack(
            [new Slot(1), new Slot(1), new Slot(1), new Slot(1), wide, new Slot(1)], rowsPerPage: 4);

        Assert.Equal(4, pages[0].Rows[0].Slots.Count);
        Assert.Same(wide, pages[0].Rows[1].Slots[0]);
    }

    [Fact]
    public void Rows_beyond_the_page_start_another_page()
    {
        var pages = CardPacker.Pack(Singles(12), rowsPerPage: 2);

        Assert.Equal(2, pages.Count);
        Assert.Equal(0, pages[0].Index);
        Assert.Equal(1, pages[1].Index);
    }

    /// <summary>
    /// An empty dash is still one page. Nothing else in the shell copes with zero pages, and
    /// "you have removed every card" is a state that deserves saying rather than a void.
    /// </summary>
    [Fact]
    public void An_empty_dash_is_one_empty_page()
    {
        var pages = CardPacker.Pack([], rowsPerPage: 2);

        Assert.Single(pages);
        Assert.Empty(pages[0].Rows);
    }

    /// <summary>
    /// Rows grow with the bands available, and not by the old "n rows fill n bands" identity —
    /// that only ever held while a row was 165 tall. At 103, the two bands the dash gets behind
    /// the fixed four-band stage take three rows, which is the whole point of the 5×3 grid.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 6)]
    [InlineData(6, 9)]
    public void Rows_grow_with_the_bands_the_stage_leaves(int bands, int expectedRows) =>
        Assert.Equal(expectedRows, BandGrid.RowsIn(bands));

    [Fact]
    public void Rows_never_overflow_the_bands_they_were_given()
    {
        for (var bands = 2; bands <= BandGrid.BandCount; bands++)
        {
            var rows = BandGrid.RowsIn(bands);
            var used = (rows * BandGrid.WidgetRowHeight) + ((rows + 1) * BandGrid.Gutter);

            Assert.True(
                used <= BandGrid.Height(bands),
                $"{rows} rows need {used} but {bands} bands are only {BandGrid.Height(bands)}");
        }
    }
}
