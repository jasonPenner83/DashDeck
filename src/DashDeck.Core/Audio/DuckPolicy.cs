namespace DashDeck.Core.Audio;

/// <summary>What else is claiming the speakers right now.</summary>
public enum AudioFocus
{
    /// <summary>Nothing. DashDeck's own playback has the output to itself.</summary>
    None,

    /// <summary>A spoken turn instruction, from projection or from DashDeck's own routing.</summary>
    SpokenInstruction,

    /// <summary>A warning chime.</summary>
    WarningChime,

    /// <summary>The phone's own music, arriving through projection.</summary>
    ProjectedMedia,
}

/// <summary>What DashDeck's own playback should do about it.</summary>
/// <param name="VolumeScale">Multiplier on the Stereo card's volume, 0 to 1.</param>
/// <param name="Pause">Whether to stop rather than lower.</param>
/// <param name="Reason">One line, for the screen and the logs.</param>
public readonly record struct DuckPlan(double VolumeScale, bool Pause, string Reason);

/// <summary>
/// The contract Q14 left owed: what the Stereo card does when something else needs the speakers.
/// </summary>
/// <remarks>
/// <b>Why a contract is needed at all.</b> Projection's audio and DashDeck's own playback land on
/// the same Windows output (ADR-0057), and Windows <i>mixes</i> rather than choosing — so without
/// this a turn instruction arrives underneath a chorus, which is exactly the failure Q14 named.
/// <para>
/// <b>Lowering is not the same as stopping, and the difference matters per case.</b> A spoken
/// instruction lasts a second or two; pausing music for it leaves a hole longer than the
/// instruction and a jarring restart, so music ducks and keeps playing. Two <i>music</i> sources at
/// once is just noise with no useful reading, so projected media stops DashDeck's playback outright
/// rather than burying it at 20 %.
/// </para>
/// </remarks>
public static class DuckPolicy
{
    /// <summary>How far music drops under a spoken instruction.</summary>
    /// <remarks>
    /// Enough to hear words over, not so far that the music seems to have stopped — the
    /// disappearance is what makes a listener reach for the dash, which is the thing to avoid
    /// while driving.
    /// </remarks>
    public const double SpokenScale = 0.25;

    /// <summary>How far music drops under a warning chime.</summary>
    /// <remarks>Harder than speech: a chime is short, and a warning that is missed is the point of it.</remarks>
    public const double ChimeScale = 0.12;

    /// <summary>What to do about <paramref name="focus"/>.</summary>
    public static DuckPlan For(AudioFocus focus) => focus switch
    {
        AudioFocus.None =>
            new DuckPlan(1.0, false, "Nothing else is using the speakers."),

        AudioFocus.SpokenInstruction =>
            new DuckPlan(SpokenScale, false,
                "Lowered under a spoken instruction — pausing would leave a longer hole than the words."),

        AudioFocus.WarningChime =>
            new DuckPlan(ChimeScale, false, "Lowered under a warning chime."),

        AudioFocus.ProjectedMedia =>
            new DuckPlan(0.0, true,
                "Paused: the phone is playing its own music, and two at once is only noise."),

        _ => new DuckPlan(1.0, false, "Unknown claim on the output; left alone."),
    };

    /// <summary>
    /// The strongest claim among several, since more than one can hold at a time.
    /// </summary>
    /// <remarks>
    /// A chime during a turn instruction during projected media is not far-fetched. Taking the
    /// strongest rather than the latest means the music cannot be quietly restored by a weaker
    /// claim ending while a stronger one still holds.
    /// </remarks>
    public static DuckPlan For(IEnumerable<AudioFocus> claims)
    {
        var plan = For(AudioFocus.None);

        foreach (var claim in claims)
        {
            var candidate = For(claim);
            if (candidate.Pause || candidate.VolumeScale < plan.VolumeScale)
            {
                plan = candidate;
            }
        }

        return plan;
    }
}
