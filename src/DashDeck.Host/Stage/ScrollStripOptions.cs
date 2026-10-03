namespace DashDeck.Host.Stage;

/// <summary>Which side of a hosted program the scroll strip sits on, if any (ADR-0046).</summary>
public enum ScrollStripSide
{
    Right,
    Left,
    Off,
}

/// <summary>How the strip's scrolling reaches the program (ADR-0046).</summary>
public enum ScrollDelivery
{
    /// <summary>A <c>WM_MOUSEWHEEL</c> posted to the program's window under the point. Moves nothing.</summary>
    Message,

    /// <summary>A real wheel turn, with the pointer moved over the program for it and back again.</summary>
    Input,
}

/// <summary>
/// The scroll strip's settings for one program, read from its launcher entry.
/// </summary>
/// <remarks>
/// <b>Two settings because one guess is not enough.</b> A posted wheel message is the polite
/// way — it moves nothing and needs no focus — and most programs honour it. A program that
/// decides where a wheel goes from where the pointer <em>actually</em> is will ignore it, and for
/// that one a real wheel turn is the only thing that works. Which kind a program is cannot be
/// known from here, so the launcher says.
/// </remarks>
public sealed record ScrollStripOptions(ScrollStripSide Side, ScrollDelivery Delivery)
{
    /// <summary>The values <c>scrollStrip</c> may take.</summary>
    public static readonly IReadOnlyList<string> Sides = ["right", "left", "off"];

    /// <summary>The values <c>scrollBy</c> may take.</summary>
    public static readonly IReadOnlyList<string> Deliveries = ["message", "input"];

    /// <summary>On the right, by message: what a program gets when its entry says nothing.</summary>
    public static ScrollStripOptions Default { get; } = new(ScrollStripSide.Right, ScrollDelivery.Message);

    /// <summary>Read the two launcher fields. Anything unrecognised is the default.</summary>
    public static ScrollStripOptions From(string? side, string? delivery) => new(
        (side ?? "").Trim().ToLowerInvariant() switch
        {
            "left" => ScrollStripSide.Left,
            "off" => ScrollStripSide.Off,
            _ => ScrollStripSide.Right,
        },
        (delivery ?? "").Trim().ToLowerInvariant() switch
        {
            "input" => ScrollDelivery.Input,
            _ => ScrollDelivery.Message,
        });
}

/// <summary>
/// Turns a finger's travel along the strip into wheel notches.
/// </summary>
/// <remarks>
/// <b>Whole notches only.</b> Windows allows a wheel delta smaller than 120 for smooth wheels,
/// and plenty of programs — Java ones among them, on older runtimes — round it to nothing. A
/// whole notch is understood by everything that scrolls at all, so the travel is kept until it
/// adds up to one and the remainder carried, never dropped.
/// <para>
/// The direction is a finger's, not a wheel's: drag down and the page comes down with it, as it
/// would under a finger on a phone — which is a wheel turned <em>up</em>, a positive delta.
/// </para>
/// </remarks>
public sealed class WheelSteps
{
    /// <summary>One notch of a wheel, as Windows counts it.</summary>
    public const int Notch = 120;

    /// <summary>
    /// How far a finger travels, in design pixels, for one notch.
    /// </summary>
    /// <remarks>
    /// A notch scrolls most programs about three lines — roughly a hundred pixels. One for every
    /// 40 px of travel moves the page a little faster than the finger, which on a narrow strip is
    /// what makes a long list reachable in a few strokes; a flick carries on by inertia.
    /// </remarks>
    public const double DefaultTravel = 40;

    private readonly double _travel;
    private double _carried;

    public WheelSteps(double travelPerNotch = DefaultTravel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelPerNotch);
        _travel = travelPerNotch;
    }

    /// <summary>
    /// Add a finger's movement (down is positive) and get the wheel delta to send now — a
    /// multiple of <see cref="Notch"/>, zero until a whole notch has built up.
    /// </summary>
    public int Add(double travel)
    {
        if (double.IsNaN(travel) || double.IsInfinity(travel))
        {
            return 0;
        }

        _carried += travel;
        var notches = (int)Math.Truncate(_carried / _travel);
        _carried -= notches * _travel;
        return notches * Notch;
    }

    /// <summary>Forget the travel carried. A new stroke starts from nothing.</summary>
    public void Reset() => _carried = 0;
}
