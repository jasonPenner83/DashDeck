namespace DashDeck.Core.Brightness;

/// <summary>
/// What a run of DashDeck did to the backlight, kept so it can be put back (ADR-0068).
/// </summary>
/// <param name="Original">The level the tablet was at before DashDeck first changed it.</param>
/// <param name="Set">The level DashDeck last left it at.</param>
public readonly record struct BacklightLeft(int Original, int Set);

/// <summary>
/// The rules for touching a backlight that belongs to somebody's tablet (constraint C1).
/// </summary>
/// <remarks>
/// <b>DashDeck borrows the backlight and gives it back.</b> The Surface is a personal device
/// carried in and out of the truck, so the level it was at when the dash opened is the level it
/// returns to when the dash closes.
/// <para>
/// <b>Except when somebody else has moved it since.</b> If the level on exit is not the one
/// DashDeck last set, a person or Windows changed it while the dash ran, and that change is
/// newer than anything remembered here. Putting the old level back would undo it.
/// </para>
/// <para>
/// <b>A crash cannot restore anything</b>, so what was borrowed is written down the moment it is
/// borrowed (<see cref="BacklightLeft"/>). The next launch reads the tablet at the dimmed level
/// it was left at, and the record is what tells it the real original.
/// </para>
/// </remarks>
public sealed class BacklightSession
{
    /// <summary>
    /// How far a read-back may differ from what was written and still be the same level. A panel
    /// with fewer than a hundred steps rounds what it is given.
    /// </summary>
    public const int Tolerance = 1;

    /// <summary>
    /// Start from what the tablet says now and what an earlier run left unrestored, if anything.
    /// </summary>
    /// <param name="found">The level read at launch; null when the screen cannot be read.</param>
    /// <param name="leftOver">An earlier run's unrestored change, from the settings file.</param>
    public BacklightSession(int? found, BacklightLeft? leftOver)
    {
        Found = found;

        // Still at what the last run set: that run never got to put it back, so its original is
        // the real one. Moved since: someone has already chosen a level, and the record is stale.
        if (leftOver is { } left && found is { } now && Same(now, left.Set))
        {
            Original = left.Original;
            LastSet = left.Set;
        }
        else
        {
            Original = found;
        }
    }

    /// <summary>The level read at launch.</summary>
    public int? Found { get; }

    /// <summary>The level to give back: found at launch, or recovered from a run that did not close.</summary>
    public int? Original { get; }

    /// <summary>The level DashDeck last set, or null when it has not touched the backlight.</summary>
    public int? LastSet { get; private set; }

    /// <summary>Whether there is anything to give back.</summary>
    public bool HasChanged => LastSet is not null && Original is { } original && !Same(original, LastSet.Value);

    /// <summary>What to write down so a crash can still be undone, or null when there is nothing.</summary>
    public BacklightLeft? Record => HasChanged ? new BacklightLeft(Original!.Value, LastSet!.Value) : null;

    /// <summary>
    /// Note a level DashDeck has set. Pass what the panel reports after the write when it can be
    /// read, because that is what the restore will be comparing against.
    /// </summary>
    public void Wrote(int level) => LastSet = level;

    /// <summary>
    /// The level to put back on exit, or null to leave the backlight alone.
    /// </summary>
    /// <param name="current">What the tablet reads now; null when it cannot be read.</param>
    public int? RestoreTo(int? current)
    {
        if (!HasChanged)
        {
            return null;
        }

        // Unreadable now: it was readable when it was set, so put it back rather than leave the
        // tablet dimmed on a guess.
        if (current is not { } now)
        {
            return Original;
        }

        return Same(now, LastSet!.Value) ? Original : null;
    }

    private static bool Same(int a, int b) => Math.Abs(a - b) <= Tolerance;
}
