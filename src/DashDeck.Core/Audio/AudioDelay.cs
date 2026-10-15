namespace DashDeck.Core.Audio;

/// <summary>How a link's delay came to be known.</summary>
public enum DelaySource
{
    /// <summary>Never set. Nothing is compensated.</summary>
    Unknown,

    /// <summary>Typed in by hand.</summary>
    Manual,

    /// <summary>Measured with the microphone.</summary>
    Measured,
}

/// <summary>
/// One audio output's delay — the single measured property of the link, kept against the endpoint
/// it was measured on.
/// </summary>
/// <param name="Endpoint">Which output this is about. A Bluetooth stereo and a pair of
/// headphones have nothing to do with each other and must not share a number.</param>
/// <param name="Value">How late sound comes out after being handed over.</param>
/// <param name="Spread">Best-to-worst gap across the runs behind it; zero when set by hand.</param>
/// <param name="Source">Where <paramref name="Value"/> came from.</param>
/// <param name="MeasuredAt">When, so the UI can say how stale it is.</param>
/// <remarks>
/// <b>This is not a constant and must not be treated as one.</b> Bluetooth delay depends on how
/// full the receiving end's buffer is, so it moves when the link reconnects, when the stream has
/// been idle long enough for the stereo to re-buffer, and under radio interference. The number is a
/// good approximation of one connection, which is why it is stamped and re-measurable rather than
/// configured once and trusted forever.
/// </remarks>
public readonly record struct AudioDelay(
    string Endpoint,
    TimeSpan Value,
    TimeSpan Spread,
    DelaySource Source,
    DateTimeOffset? MeasuredAt)
{
    /// <summary>Nothing known about this output, so nothing is shifted.</summary>
    public static AudioDelay None { get; } = new(string.Empty, TimeSpan.Zero, TimeSpan.Zero, DelaySource.Unknown, null);

    /// <summary>A delay the person typed in.</summary>
    public static AudioDelay FromHand(string endpoint, TimeSpan value) =>
        new(endpoint, value, TimeSpan.Zero, DelaySource.Manual, null);

    /// <summary>Whether there is anything worth compensating.</summary>
    public bool IsKnown => Source != DelaySource.Unknown && Value > TimeSpan.Zero;
}

/// <summary>What the dash is about to play, which decides what can be done about the delay.</summary>
public enum AudioUse
{
    /// <summary>Music, radio, podcasts. Nothing to be in sync with.</summary>
    Music,

    /// <summary>A turn instruction or a warning chime: audio that has to land at a known moment.</summary>
    SpokenPrompt,

    /// <summary>Sound belonging to a picture on the tablet's screen.</summary>
    VideoWithPicture,

    /// <summary>A click or a beep answering a finger that has already touched the glass.</summary>
    TouchFeedback,
}

/// <summary>What to do about the delay for one kind of audio.</summary>
/// <param name="HoldPictureBy">Hold video frames this long so the picture waits for its sound.</param>
/// <param name="StartAudioEarlyBy">
/// Begin playing this far ahead of the moment the sound should land. The caller must clamp this to
/// the lead time it actually has — a turn discovered 100 ms in advance cannot be announced 200 ms
/// early.
/// </param>
/// <param name="PreferLocalOutput">Play on the tablet's own speaker instead; the delay cannot be
/// fixed for this kind of sound, only avoided.</param>
/// <param name="Reason">Why, in one line, for the settings screen and the logs.</param>
public readonly record struct AudioCompensation(
    TimeSpan HoldPictureBy,
    TimeSpan StartAudioEarlyBy,
    bool PreferLocalOutput,
    string Reason)
{
    /// <summary>Leave it alone.</summary>
    public static AudioCompensation Nothing(string reason) =>
        new(TimeSpan.Zero, TimeSpan.Zero, false, reason);
}

/// <summary>
/// One measured delay, three different answers.
/// </summary>
/// <remarks>
/// <b>The delay cannot be removed, only worked around.</b> Bluetooth adds its 150-250 ms between
/// the dash handing audio over and the speakers making a sound, and nothing on the tablet can
/// subtract from that. So a single "audio delay" slider is a trap: the useful question is never
/// "how much delay" but "late relative to <i>what</i>" — and the answer differs per kind of sound.
/// <list type="bullet">
/// <item><b>Against a picture:</b> delay the <i>picture</i>. The sound cannot be hurried, so the
/// video waits for it.</item>
/// <item><b>Against a known moment</b> (a turn, a warning): start the sound <i>early</i>. This one
/// is a genuine fix rather than a compromise — the dash knows the turn is coming, so it can speak
/// ahead and have the words land on time. At highway speed 200 ms is about five and a half metres
/// of road, which is the difference between "turn right now" and having passed it.</item>
/// <item><b>Against a finger that already tapped:</b> nothing. The event is in the past; no amount
/// of scheduling brings the click back. The only escape is to not use that output for it.</item>
/// <item><b>Against nothing at all</b> (music): nothing, and no one can tell.</item>
/// </list>
/// </remarks>
public static class AudioDelayPolicy
{
    /// <summary>Decide what to do about <paramref name="delay"/> for one kind of audio.</summary>
    public static AudioCompensation For(AudioUse use, AudioDelay delay)
    {
        if (!delay.IsKnown)
        {
            return AudioCompensation.Nothing("No delay measured for this output yet.");
        }

        return use switch
        {
            AudioUse.Music =>
                AudioCompensation.Nothing("Music has nothing to be in sync with."),

            AudioUse.VideoWithPicture =>
                new AudioCompensation(
                    delay.Value,
                    TimeSpan.Zero,
                    false,
                    $"Holding the picture {delay.Value.TotalMilliseconds:F0} ms so it waits for its sound."),

            AudioUse.SpokenPrompt =>
                new AudioCompensation(
                    TimeSpan.Zero,
                    delay.Value,
                    false,
                    $"Speaking {delay.Value.TotalMilliseconds:F0} ms early so the words land on time."),

            AudioUse.TouchFeedback =>
                new AudioCompensation(
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    true,
                    "A tap has already happened, so its click cannot be scheduled early — "
                        + "playing it on the tablet instead."),

            _ => AudioCompensation.Nothing("Unknown use."),
        };
    }
}
