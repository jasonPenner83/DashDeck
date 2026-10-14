namespace DashDeck.Core.Audio;

/// <summary>
/// Builds one continuous stretch of audio per output, with the probes embedded in it at known
/// offsets, so the stream never goes quiet.
/// </summary>
/// <remarks>
/// <b>Why this exists, and it is not an optimisation.</b> The first version played each probe as
/// an isolated burst with seconds of silence either side. That is actively hostile to Bluetooth
/// audio: when a Windows endpoint goes idle the A2DP stream is <b>suspended</b>, and when audio
/// arrives again it is <b>started</b> — and a car head unit reads those transitions as media play
/// and pause. The owner of a 2019 F-150 watched its screen toggle between play and pause through
/// the whole measurement, which is what gave this away.
/// <para>
/// So every reading that design produced was <b>a cold stream start, not a delay</b>: how long
/// SYNC 3 takes to wake its audio pipeline, measured ten times and reported as latency. It
/// explains everything that was wrong with those runs — readings near 1165 ms, spreads of 134 ms,
/// and earlier ones near 440 ms. No wider search window or tighter spread gate could have fixed
/// it, because the number being measured was the wrong number.
/// </para>
/// <para>
/// A single continuous track per output fixes all of it at once. The stream starts once and stays
/// up, so the sink buffers once and settles; the head unit sees one play rather than ten; and
/// because the probes sit at exact sample offsets inside one buffer, every probe on an output
/// shares the same start and the per-probe handover jitter disappears. That last part matters for
/// more than tidiness: readings on one output should now be <i>identical</i> but for correlation
/// noise, so a scattered reading means something is genuinely wrong rather than merely jittery.
/// </para>
/// </remarks>
public static class ProbeTrack
{
    /// <summary>Level of the carrier that keeps the stream from idling.</summary>
    /// <remarks>
    /// About -54 dBFS: inaudible against any cab, and far under the probe, but it is <i>something</i>
    /// rather than digital silence. Some sinks watch for silence and suspend anyway, and the cost of
    /// not being silent is nothing.
    /// </remarks>
    public const float CarrierLevel = 0.002f;

    /// <summary>Build one output's track: carrier throughout, probes laid in at their offsets.</summary>
    /// <param name="probe">The probe for this output.</param>
    /// <param name="offsets">Where each probe starts, measured from the track's beginning.</param>
    /// <param name="duration">Total length of the track.</param>
    /// <param name="seed">Seed for the carrier noise, so a track is reproducible.</param>
    public static float[] Build(
        ReadOnlySpan<float> probe,
        IReadOnlyList<TimeSpan> offsets,
        TimeSpan duration,
        int seed = 1)
    {
        var total = (int)Math.Round(duration.TotalSeconds * LatencyProbe.SampleRate);
        var track = new float[Math.Max(0, total)];
        var random = new Random(seed);

        for (var i = 0; i < track.Length; i++)
        {
            track[i] = (float)((random.NextDouble() - 0.5) * 2 * CarrierLevel);
        }

        foreach (var offset in offsets)
        {
            var at = (int)Math.Round(offset.TotalSeconds * LatencyProbe.SampleRate);
            for (var i = 0; i < probe.Length && at + i < track.Length; i++)
            {
                track[at + i] = probe[i];
            }
        }

        return track;
    }
}

/// <summary>
/// When each probe plays, for two outputs sharing one recording.
/// </summary>
/// <param name="LeadIn">
/// Carrier before the first probe. The stream's one cold start happens here, and the sink's buffer
/// settles, so no probe pays for it.
/// </param>
/// <param name="Gap">Spacing between consecutive probes, alternating between the outputs.</param>
/// <param name="Rounds">Probes per output.</param>
public sealed record ProbePlan(TimeSpan LeadIn, TimeSpan Gap, int Rounds)
{
    /// <summary>Where the reference output's probes sit in its track.</summary>
    public IReadOnlyList<TimeSpan> LocalOffsets =>
        [.. Enumerable.Range(0, Rounds).Select(r => LeadIn + (Gap * (r * 2)))];

    /// <summary>Where the measured output's probes sit in its track.</summary>
    public IReadOnlyList<TimeSpan> RemoteOffsets =>
        [.. Enumerable.Range(0, Rounds).Select(r => LeadIn + (Gap * ((r * 2) + 1)))];

    /// <summary>How long each track runs: the last probe, plus a tail for it to arrive in.</summary>
    public TimeSpan Duration => LeadIn + (Gap * (Rounds * 2)) + Gap;

    /// <summary>
    /// Every probe as a handover, given when each track was handed to its device.
    /// </summary>
    /// <remarks>
    /// A probe's handover is its track's handover plus its offset. Exact, because the offset is a
    /// sample position inside a buffer rather than a moment a thread woke up.
    /// </remarks>
    public IReadOnlyList<ProbeHandoff> Handoffs(TimeSpan localHandedOver, TimeSpan remoteHandedOver) =>
    [
        .. LocalOffsets.Select(o => new ProbeHandoff(false, localHandedOver + o))
            .Concat(RemoteOffsets.Select(o => new ProbeHandoff(true, remoteHandedOver + o)))
            .OrderBy(h => h.At),
    ];
}
