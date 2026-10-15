using DashDeck.Abstractions;

namespace DashDeck.Core.Location;

/// <summary>
/// What the phone's position <em>is</em>, built from the sentences as they arrive rather than
/// from whichever one came last.
/// </summary>
/// <remarks>
/// A read loop that keeps the newest parsed sentence looks right and flickers in the cab, for two
/// reasons that have nothing to do with the link quality:
/// <list type="bullet">
/// <item><description><b>Not every sentence carries every field.</b> RMC has speed and course,
/// GGA has neither. An app sending both — most do — therefore alternates between a fix with a
/// heading and a fix without one, once a second, and the compass hands back to the magnetometer
/// on every other sentence.</description></item>
/// <item><description><b>A receiver says "no fix" out loud.</b> A void RMC or a GGA with quality
/// zero parses fine and carries no coordinates, so keeping the newest sentence throws away a
/// perfectly good position because of one blip.</description></item>
/// </list>
/// So: fields <b>merge</b> across sentences inside <see cref="MergeWindow"/>, and a position is
/// <b>held</b> — a sentence that lacks one means "nothing new", not "nowhere".
/// <para>
/// Held is not frozen, which is the line that matters for anything drawing a map. The held fix
/// keeps the timestamp it was taken at, so its age is always on screen; it demotes to Stale after
/// <see cref="HoldFor"/> and stops being offered as a position at all after
/// <see cref="LostAfter"/>. A phone that has genuinely lost its fix still reports NO FIX within
/// ten seconds — it just no longer does so because of a single sentence.
/// </para>
/// </remarks>
public sealed class LocationTracker
{
    /// <summary>
    /// How long a speed or course carries forward onto a sentence that does not have one. Short,
    /// because the point is to bridge the gap between an RMC and the GGA beside it, not to keep
    /// claiming the last speed as the truck slows down.
    /// </summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long a position stays Live before it demotes to Stale.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a position is offered at all. Past this the fix reads Unavailable, so the dash
    /// blanks the position rather than drawing a dot that stopped being true.
    /// </summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(10);

    private LocationFix? _positioned;
    private LocationFix? _last;

    /// <summary>Feed one decoded sentence.</summary>
    public void Accept(LocationFix sentence)
    {
        _last = sentence;

        if (!sentence.HasFix)
        {
            // The receiver is talking and says it has nothing. Worth knowing, not worth throwing
            // the last position away for: the age and the demotions below report it honestly.
            return;
        }

        _positioned = sentence with
        {
            GroundSpeedKmh = Carried(sentence.GroundSpeedKmh, sentence.TakenUtc, static f => f.GroundSpeedKmh),
            CourseDegrees = Carried(sentence.CourseDegrees, sentence.TakenUtc, static f => f.CourseDegrees),
        };
    }

    /// <summary>
    /// The fix to act on, as of <paramref name="nowUtc"/>. Null only when nothing has decoded at
    /// all; a receiver that has never had a fix reports its own no-fix sentence rather than
    /// nothing, so the difference between "not connected" and "no satellites" survives.
    /// </summary>
    public LocationFix? Current(DateTimeOffset nowUtc)
    {
        if (_positioned is not { } fix)
        {
            return _last;
        }

        var age = nowUtc - fix.TakenUtc;

        var byAge = age > LostAfter
            ? SignalQuality.Unavailable
            : age > HoldFor
                ? SignalQuality.Stale
                : fix.Quality;

        // The worse of the two, never the better: a fix a dropped link already marked down is
        // not promoted back up by being young, and a young fix is not promoted past what the
        // link says about it.
        return fix with { Quality = Worse(fix.Quality, byAge) };
    }

    /// <summary>
    /// The link dropped — nothing is being told any more. The position is kept and marked down to
    /// Stale rather than written off, because the two cases that reach here look nothing alike:
    /// a Bluetooth hiccup that reconnects in two seconds, and a phone left at home. Stale says
    /// "not current" without blanking a position that is still almost certainly right, and the
    /// age rule still writes it off at <see cref="LostAfter"/> if the link really is gone.
    /// </summary>
    public void LinkLost()
    {
        if (_positioned is { } fix)
        {
            _positioned = fix with { Quality = Worse(fix.Quality, SignalQuality.Stale) };
        }

        if (_last is { } last)
        {
            _last = last with { Quality = Worse(last.Quality, SignalQuality.Stale) };
        }
    }

    /// <summary>
    /// The lower of two qualities on this dash's scale. Simulated ranks with Live — it is a
    /// confident reading, just not of anything real — and ties keep the first, so a synthetic
    /// fix stays labelled as one.
    /// </summary>
    private static SignalQuality Worse(SignalQuality one, SignalQuality other) =>
        Rank(one) <= Rank(other) ? one : other;

    private static int Rank(SignalQuality quality) => quality switch
    {
        SignalQuality.Unavailable => 0,
        SignalQuality.Stale => 1,
        _ => 2,
    };

    /// <summary>A missing value, filled from the last positioned fix if that is recent enough.</summary>
    private double Carried(double value, DateTimeOffset takenUtc, Func<LocationFix, double> field)
    {
        if (!double.IsNaN(value))
        {
            return value;
        }

        return _positioned is { } previous
            && takenUtc - previous.TakenUtc <= MergeWindow
            && !double.IsNaN(field(previous))
                ? field(previous)
                : double.NaN;
    }
}
