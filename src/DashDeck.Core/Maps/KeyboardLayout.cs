namespace DashDeck.Core.Maps;

/// <summary>What pressing a key does.</summary>
public enum KeyAction
{
    /// <summary>Add this key's character.</summary>
    Type,

    /// <summary>Remove the last character.</summary>
    Backspace,

    /// <summary>Add a space.</summary>
    Space,

    /// <summary>Empty the whole query.</summary>
    Clear,
}

/// <summary>One key on the dash's own keyboard.</summary>
/// <param name="Caption">What is written on it.</param>
/// <param name="Action">What it does.</param>
/// <param name="Width">How many key-widths it takes. A letter is 1.</param>
public sealed record Key(string Caption, KeyAction Action = KeyAction.Type, double Width = 1);

/// <summary>
/// The dash's own on-screen keyboard, as data (ADR-0065).
/// </summary>
/// <remarks>
/// <b>Why DashDeck draws its own.</b> The tablet is mounted with no keyboard, and this is the first
/// thing in the dash that has ever needed typing in the cab — every Settings field reachable there
/// is a picker. Windows' touch keyboard is in-box and would have cost almost nothing, but over a
/// borderless full-screen window it decides for itself whether to appear and what to cover, and
/// "it may simply not show up" is not a thing to discover in a truck.
/// <para>
/// <b>One page, and no shift.</b> The first attempt had a letters half and a numbers half, which a
/// test about row widths exposed as badly proportioned — and fixing the proportions showed the
/// mode was not worth having. A street number is the most likely thing typed first, so the digits
/// get their own row; addresses are matched case-insensitively, so there is no case; and five rows
/// with nothing hidden behind a mode is fewer taps and nothing to get lost in.
/// </para>
/// <para>
/// The layout is data so the half worth testing — what a press does to a query, that every
/// character is reachable, that the rows are proportioned alike — is tested without a UI thread,
/// and the view is left with nothing but drawing.
/// </para>
/// </remarks>
public static class KeyboardLayout
{
    /// <summary>
    /// The rows, in the order they are drawn.
    /// </summary>
    /// <remarks>
    /// Row widths are kept within about a key of each other on purpose: they are drawn as one
    /// grid, so a row much narrower than its neighbours means wider keys on that row and narrower
    /// ones elsewhere — and the narrow ones are the keys that get missed on a rough road.
    /// <para>
    /// The marks are the ones Canadian addresses and postal codes actually use. Every extra key
    /// makes the others smaller, which is the whole budget.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<Key>> Rows { get; } =
    [
        Row("1234567890"),
        Row("QWERTYUIOP"),
        Row("ASDFGHJKL"),
        [.. Row("ZXCVBNM"), new Key("⌫", KeyAction.Backspace, 1.6)],
        [.. Row("-'.#/"), new Key("SPACE", KeyAction.Space, 3), new Key("CLEAR", KeyAction.Clear, 1.8)],
    ];

    /// <summary>Every key, for checking the whole set at once.</summary>
    public static IEnumerable<Key> All => Rows.SelectMany(row => row);

    /// <summary>
    /// The query after a key is pressed.
    /// </summary>
    /// <remarks>
    /// A pure function of the query and the key, so every case — backspacing an empty query, a
    /// leading space, a query at its limit — is a test rather than something to try in a truck.
    /// </remarks>
    public static string Press(string query, Key key) => key.Action switch
    {
        KeyAction.Backspace => query.Length == 0 ? query : query[..^1],
        KeyAction.Clear => string.Empty,

        // No leading space, and never two in a row: both are invisible on screen and both change
        // what the geocoder is asked.
        KeyAction.Space => query.Length == 0 || query.EndsWith(' ') ? query : query + " ",

        _ => query.Length >= AddressQuery.MaxLength ? query : query + key.Caption,
    };

    /// <summary>How wide a row is, in key-widths.</summary>
    public static double Width(IReadOnlyList<Key> row) => row.Sum(key => key.Width);

    /// <summary>The widest row, which is what the grid is laid out against.</summary>
    public static double Widest => Rows.Max(Width);

    private static List<Key> Row(string characters) => [.. characters.Select(c => new Key(c.ToString()))];
}
