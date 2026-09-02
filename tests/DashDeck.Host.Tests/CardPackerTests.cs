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
    public void Three_single_cards_fill_one_row()
    {
        var pages = CardPacker.Pack(Singles(3), rowsPerPage: 2);

        Assert.Single(pages);
        Assert.Single(pages[0].Rows);
        Assert.Equal(3, pages[0].Rows[0].Slots.Count);
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
    public void A_wide_card_takes_two_columns_and_leaves_room_for_one_more()
    {
        var pages = CardPacker.Pack([new Slot(2), new Slot(1), new Slot(1)], rowsPerPage: 2);

        Assert.Equal(2, pages[0].Rows[0].Slots.Count);
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
        var pages = CardPacker.Pack([new Slot(1), new Slot(1), wide, new Slot(1)], rowsPerPage: 4);

        Assert.Equal(2, pages[0].Rows[0].Slots.Count);
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
    /// A three-band stage leaves three rows, not two and a spare band. This is the arithmetic
    /// that resolves F9, and it is not the "n rows fill n bands exactly" identity the band
    /// grid used to claim — that only ever held at two.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
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
