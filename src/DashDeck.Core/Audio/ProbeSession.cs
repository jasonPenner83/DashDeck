namespace DashDeck.Core.Audio;

/// <summary>One probe handed to an output, and when.</summary>
/// <param name="Remote">True for the output being measured, false for the reference speaker.</param>
/// <param name="At">When the probe was handed over, measured from the start of the recording.</param>
public readonly record struct ProbeHandoff(bool Remote, TimeSpan At);

/// <summary>
/// Reads a whole measurement session out of one recording: every probe found, the two outputs
/// summarised, and the delay that falls out of the difference.
/// </summary>
/// <remarks>
/// This is the part of the tool worth testing, so it lives here rather than in the tool. It is
/// pure: a recording in, numbers out, no audio device anywhere near it.
/// <para>
/// <b>The trap it exists to avoid.</b> Each probe is looked for in a window that starts at the
/// moment it was handed over and runs <c>maxLag</c> forward. If probes are handed over closer
/// together than that window is long, one probe's window reaches the <i>next</i> probe — and
/// because the next probe is a perfect match for the reference, it wins, and the measurement comes
/// back confident and wrong. <see cref="Spacing"/> states the rule, and the caller has to respect
/// it; the tests hold it to both sides of the boundary.
/// </para>
/// </remarks>
public static class ProbeSession
{
    /// <summary>
    /// The smallest safe gap between consecutive probes, for a given search window.
    /// </summary>
    /// <remarks>
    /// A window of <paramref name="maxLag"/> starting at one handover must end before the next
    /// probe can arrive. The arrival of the next probe is itself delayed by at least nothing, so
    /// the gap has to exceed the window outright.
    /// </remarks>
    public static TimeSpan Spacing(TimeSpan maxLag) => maxLag + TimeSpan.FromMilliseconds(100);

    /// <summary>Find every probe and work out the delay.</summary>
    /// <param name="handoffs">Each probe played, in the order played.</param>
    /// <param name="captured">The whole recording, starting at time zero of the handoffs.</param>
    /// <param name="probe">The probe that was played.</param>
    /// <param name="maxLagSamples">How far past each handover to search.</param>
    /// <param name="localOutputLatency">The reference speaker's own output delay.</param>
    /// <param name="remoteMetres">Distance from the measured speakers to the microphone.</param>
    /// <param name="localMetres">Distance from the reference speaker to the microphone.</param>
    public static SessionResult Analyse(
        IReadOnlyList<ProbeHandoff> handoffs,
        ReadOnlySpan<float> captured,
        ReadOnlySpan<float> probe,
        int maxLagSamples,
        TimeSpan localOutputLatency,
        double remoteMetres = 1.0,
        double localMetres = 0.2)
    {
        var local = new List<ProbeMatch>();
        var remote = new List<ProbeMatch>();
        var found = new List<(bool Remote, ProbeMatch Match)>();

        foreach (var handoff in handoffs)
        {
            var from = (int)Math.Round(handoff.At.TotalSeconds * LatencyProbe.SampleRate);
            var match = from >= 0 && from < captured.Length
                ? LatencyProbe.Find(probe, captured[from..], maxLagSamples)
                : ProbeMatch.NotFound;

            (handoff.Remote ? remote : local).Add(match);
            found.Add((handoff.Remote, match));
        }

        var localSummary = LatencyCalibration.Summarise(local);
        var remoteSummary = LatencyCalibration.Summarise(remote);

        var resolved = localSummary.Ok && remoteSummary.Ok
            ? LatencyCalibration.RemoteOutputLatency(
                remoteSummary.Lag, localSummary.Lag, localOutputLatency, remoteMetres, localMetres)
            : null;

        return new SessionResult(localSummary, remoteSummary, resolved, found);
    }
}

/// <summary>Everything one session produced.</summary>
/// <param name="Local">The reference speaker's runs.</param>
/// <param name="Remote">The measured output's runs.</param>
/// <param name="Delay">The delay to compensate for, or null when it could not be established.</param>
/// <param name="Probes">Every probe, in the order played, for showing the person.</param>
public readonly record struct SessionResult(
    RunSummary Local,
    RunSummary Remote,
    TimeSpan? Delay,
    IReadOnlyList<(bool Remote, ProbeMatch Match)> Probes)
{
    /// <summary>Whether a delay was established.</summary>
    public bool Ok => Delay is not null;
}
