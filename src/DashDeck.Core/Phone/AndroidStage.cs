namespace DashDeck.Core.Phone;

/// <summary>Where Android is drawn when it is showing (ADR-0075).</summary>
public enum AndroidPlacement
{
    /// <summary>All six bands, from the nav's ANDROID cell — the stage collapsed behind it.</summary>
    Full,

    /// <summary>Handed to the stage as an occupant, with the dash below it.</summary>
    Stage,
}

/// <summary>
/// Which placement Android is in, and what the ANDROID cell's three presses do to it (ADR-0075).
/// </summary>
/// <remarks>
/// Pure, like <see cref="Maps.MapView"/>, so the rules are tested without a UI thread. Two
/// placements rather than the map's three: an emulated device in the bottom third would be a
/// picture too small to touch.
/// </remarks>
public readonly record struct AndroidView(AndroidPlacement Placement)
{
    /// <summary>Where Android opens: full screen.</summary>
    public static readonly AndroidView Default = new(AndroidPlacement.Full);

    public bool IsFull => Placement is AndroidPlacement.Full;

    public bool IsOnStage => Placement is AndroidPlacement.Stage;

    /// <summary>A tap on the cell: full screen, from wherever it was — so a tap brings it back off the stage.</summary>
    public AndroidView Tapped() => new(AndroidPlacement.Full);

    /// <summary>A double tap: onto the stage. Idempotent.</summary>
    public AndroidView DoubleTapped() => new(AndroidPlacement.Stage);
}

/// <summary>What a press on a nav cell was.</summary>
public enum PressKind
{
    /// <summary>Down and up, quickly.</summary>
    Tap,

    /// <summary>A second tap soon after the first.</summary>
    DoubleTap,

    /// <summary>Held down — acted on while still held, so a lift after it means nothing.</summary>
    Hold,
}

/// <summary>
/// Telling a tap from a double tap from a hold on one cell (ADR-0075).
/// </summary>
/// <remarks>
/// A tap is acted on at once, never held back to see whether a second follows: the first tap of a
/// double tap opens Android full screen and the second sends it on to the stage, so acting at once
/// costs nothing and a single tap is never slow. A hold is acted on when the time is up, with the
/// finger still down, because a menu that waits for the lift feels broken.
/// </remarks>
public static class PressGesture
{
    /// <summary>How long a press is held before it is a hold.</summary>
    public static readonly TimeSpan HoldAfter = TimeSpan.FromMilliseconds(550);

    /// <summary>How soon a second tap must follow the first to make a double tap.</summary>
    public static readonly TimeSpan DoubleTapWithin = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// What a lift means, or null when it means nothing — the hold already happened.
    /// </summary>
    /// <param name="down">When the finger went down.</param>
    /// <param name="up">When it lifted.</param>
    /// <param name="previousTap">When the last press that counted as a tap lifted, or null.</param>
    /// <param name="holdFired">True when this press already opened the hold's menu.</param>
    public static PressKind? Released(TimeSpan down, TimeSpan up, TimeSpan? previousTap, bool holdFired)
    {
        if (holdFired)
        {
            return null;
        }

        if (up - down >= HoldAfter)
        {
            return PressKind.Hold;
        }

        return previousTap is { } last && up - last <= DoubleTapWithin && up >= last ? PressKind.DoubleTap : PressKind.Tap;
    }
}

/// <summary>Android's own keys, as the dash's key bar beside the picture presses them (ADR-0076).</summary>
public enum AndroidKey
{
    Back,
    Home,
    Recents,
}
