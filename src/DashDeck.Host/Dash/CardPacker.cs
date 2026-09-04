using System.Collections.ObjectModel;

namespace DashDeck.Host.Dash;

/// <summary>Anything that occupies grid columns on the dash.</summary>
/// <remarks>
/// Exists so the add-a-card tile flows through the same packer as a real card and lands
/// exactly where the next card would. Showing someone the empty slot is a better answer to
/// "where will this go?" than any amount of explaining.
/// </remarks>
public interface IDashSlot
{
    /// <summary>Grid columns occupied: 1 or 2.</summary>
    int Columns { get; }

    /// <summary>Rendered width in design pixels, inner gutters included.</summary>
    double Width { get; }
}

/// <summary>
/// A slot that is a real card in the arrangement — as opposed to the add tile.
/// </summary>
/// <remarks>
/// The abstraction that lets a signal card and a component's widget live in the same ordered
/// list (ADR-0015). Both carry a <see cref="Spec"/> so the arrangement serialises uniformly,
/// and both <see cref="Activate"/>/<see cref="Deactivate"/> so the page rule — only what you
/// can see asks for anything (ADR-0004, ADR-0015) — applies to a component exactly as it does
/// to a signal card. The add tile is an <see cref="IDashSlot"/> but not one of these: it holds
/// no spec and declares nothing.
/// </remarks>
public interface IDashCard : IDashSlot, IDisposable
{
    /// <summary>How this card is stored — its place in the one ordered arrangement.</summary>
    CardSpec Spec { get; }

    /// <summary>Came onto the visible page. Declare what it needs.</summary>
    void Activate();

    /// <summary>Left the visible page. Withdraw what it declared.</summary>
    void Deactivate();
}

/// <summary>The tile that adds a card. Only present in edit mode.</summary>
public sealed class AddCardSlot : IDashSlot
{
    /// <inheritdoc />
    public int Columns => 1;

    /// <inheritdoc />
    public double Width => BandGrid.CardWidth(1);
}

/// <summary>One row of cards, left to right.</summary>
public sealed class CardRow
{
    public CardRow(IReadOnlyList<IDashSlot> slots) => Slots = slots;

    /// <summary>The cards in this row.</summary>
    public IReadOnlyList<IDashSlot> Slots { get; }
}

/// <summary>One screenful of rows.</summary>
public sealed class CardPage
{
    public CardPage(int index, IReadOnlyList<CardRow> rows)
    {
        Index = index;
        Rows = rows;
    }

    /// <summary>Zero-based page number.</summary>
    public int Index { get; }

    /// <summary>The rows on this page, top to bottom.</summary>
    public IReadOnlyList<CardRow> Rows { get; }

    /// <summary>Every slot on this page, in order. What activation is decided from.</summary>
    public IEnumerable<IDashSlot> Slots => Rows.SelectMany(r => r.Slots);
}

/// <summary>
/// Flows cards into rows, and rows into pages.
/// </summary>
/// <remarks>
/// <b>Nothing stores a row or a page number.</b> A card knows its width and its position in
/// one ordered list; where it lands is computed from the space available at the time. That
/// is what lets the same saved dashboard render as two rows behind a four-band stage and
/// three behind a three-band one, and it is why reordering is a list operation rather than a
/// grid operation.
/// <para>
/// Pure and static on purpose — this is the part most worth testing, and it needs no
/// dispatcher, no vehicle and no window to test.
/// </para>
/// </remarks>
public static class CardPacker
{
    /// <summary>
    /// Pack <paramref name="slots"/> into pages of at most <paramref name="rowsPerPage"/> rows.
    /// </summary>
    /// <remarks>
    /// Greedy and left-to-right: a card that does not fit in the remaining columns starts the
    /// next row rather than being split or reordered around. A two-column card after a
    /// single therefore leaves one column empty, which is visible and predictable — the
    /// alternative, quietly promoting a later card into the gap, would mean the order you
    /// see is not the order you set.
    /// </remarks>
    public static IReadOnlyList<CardPage> Pack(IReadOnlyList<IDashSlot> slots, int rowsPerPage)
    {
        ArgumentNullException.ThrowIfNull(slots);

        rowsPerPage = Math.Max(1, rowsPerPage);

        var rows = new List<CardRow>();
        var current = new List<IDashSlot>();
        var used = 0;

        foreach (var slot in slots)
        {
            var columns = Math.Clamp(slot.Columns, 1, BandGrid.ColumnsPerRow);

            if (used + columns > BandGrid.ColumnsPerRow && current.Count > 0)
            {
                rows.Add(new CardRow(current));
                current = [];
                used = 0;
            }

            current.Add(slot);
            used += columns;
        }

        if (current.Count > 0)
        {
            rows.Add(new CardRow(current));
        }

        var pages = new List<CardPage>();

        for (var i = 0; i < rows.Count; i += rowsPerPage)
        {
            pages.Add(new CardPage(pages.Count, rows.GetRange(i, Math.Min(rowsPerPage, rows.Count - i))));
        }

        // An empty dash is still one page. Nothing else in the shell copes with zero pages,
        // and "you have removed every card" deserves saying rather than rendering as a void.
        if (pages.Count == 0)
        {
            pages.Add(new CardPage(0, new ReadOnlyCollection<CardRow>([])));
        }

        return pages;
    }
}
