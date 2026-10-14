namespace DashDeck.Core.Audio;

/// <summary>
/// The audio/video sync offset for a picture on the tablet, and how it is nudged.
/// </summary>
/// <remarks>
/// <b>This is the case the delay actually matters for.</b> Music over Bluetooth has nothing to be
/// in sync with, so its 150 ms or more is inaudible — but a face on the screen whose lips move
/// ahead of the words is obvious within one sentence, and the only fix is to shift the two apart
/// by the same amount the link added.
/// <para>
/// <b>Why it is nudged rather than only measured.</b> The measured link delay (ADR-0059) is the
/// right <i>starting point</i> and the wrong final answer: the total also carries the file's own
/// container offset, the decoder's buffering and whatever the sink is doing on that connection. So
/// the measurement seeds it and the person tunes it by eye while watching, which is what every
/// media player does and what makes the control worth having at all. Fifty milliseconds a step —
/// smaller than that is below what anyone can see, larger overshoots.
/// </para>
/// </remarks>
public static class VideoSync
{
    /// <summary>One nudge.</summary>
    /// <remarks>
    /// The step every player seems to settle on, and for good reason: sync errors under about
    /// 40 ms are not visible, so a finer step costs taps and buys nothing.
    /// </remarks>
    public const int StepMilliseconds = 50;

    /// <summary>How far the offset may be pushed either way.</summary>
    /// <remarks>
    /// Two seconds is far past any real Bluetooth link — a truck measured near 1.2 s at its worst
    /// — and a limit stops a stuck finger walking the offset somewhere it cannot be recovered from
    /// without knowing this control exists.
    /// </remarks>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Where to start, given what the output's delay was measured or typed as.
    /// </summary>
    /// <remarks>
    /// <b>Negative, and that is the whole point.</b> The link makes sound arrive late, so the
    /// correction has to move sound <i>earlier</i> relative to the picture — which a player
    /// achieves by holding the picture back, since sound cannot be produced before it exists.
    /// A positive offset would make the problem exactly twice as bad.
    /// </remarks>
    public static TimeSpan DefaultFor(TimeSpan linkDelay) => Clamp(-linkDelay);

    /// <summary>Move the offset by whole steps, clamped.</summary>
    public static TimeSpan Nudge(TimeSpan current, int steps) =>
        Clamp(current + TimeSpan.FromMilliseconds(steps * (double)StepMilliseconds));

    /// <summary>Hold an offset inside <see cref="Limit"/>.</summary>
    /// <remarks>
    /// No check for a non-finite value here, deliberately: a <see cref="TimeSpan"/> cannot hold
    /// one — <c>TimeSpan.FromMilliseconds(double.NaN)</c> throws rather than producing one — so a
    /// guard at this boundary would be unreachable code pretending to be safety. The place a
    /// nonsense number can actually arrive is <see cref="FromStored"/>, where it comes in as a
    /// double out of a settings file someone may have edited.
    /// </remarks>
    public static TimeSpan Clamp(TimeSpan offset) =>
        TimeSpan.FromMilliseconds(
            Math.Clamp(offset.TotalMilliseconds, -Limit.TotalMilliseconds, Limit.TotalMilliseconds));

    /// <summary>
    /// An offset read back from settings, where the number arrived from outside.
    /// </summary>
    /// <remarks>
    /// This is the real untrusted boundary. The settings file is editable by hand and a
    /// <c>NaN</c> or an infinity in it would throw on the way into a <see cref="TimeSpan"/>, so it
    /// is turned into "in step" rather than allowed to take the video occupant down with it.
    /// </remarks>
    public static TimeSpan FromStored(double milliseconds) =>
        double.IsFinite(milliseconds)
            ? Clamp(TimeSpan.FromMilliseconds(milliseconds))
            : TimeSpan.Zero;

    /// <summary>
    /// The offset as LibVLC wants it: microseconds, negative to bring sound forward.
    /// </summary>
    /// <remarks>
    /// <c>MediaPlayer.SetAudioDelay</c> takes microseconds, and <b>resets to zero every time the
    /// media changes</b> — its own documentation says so, which makes "set it once at startup" a
    /// bug that only shows up on the second file.
    /// </remarks>
    public static long ToMicroseconds(TimeSpan offset) =>
        (long)Math.Round(Clamp(offset).TotalMilliseconds * 1000);

    /// <summary>
    /// The offset in words, by its effect rather than its sign.
    /// </summary>
    /// <remarks>
    /// "−150 ms" tells nobody which way it went. What a person can check against the screen is
    /// whether the picture is being held back or the sound is, so that is what it says.
    /// </remarks>
    public static string Describe(TimeSpan offset)
    {
        var ms = Clamp(offset).TotalMilliseconds;

        return ms switch
        {
            0 => "in step",
            < 0 => $"picture held back {-ms:0} ms",
            _ => $"sound held back {ms:0} ms",
        };
    }
}
