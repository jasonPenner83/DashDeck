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
/// <b>The trap it exists to avoid, and it has two directions.</b> Each probe is looked for in a
/// window that starts at the moment it was handed over and runs <c>maxLag</c> forward.
/// </para>
/// <list type="number">
/// <item><b>Forward.</b> Probes handed over closer together than the window is long means one
/// probe's window reaches the <i>next</i> probe, which is a perfect match, so it wins and the
/// answer comes back confident and wrong.</item>
/// <item><b>Backward, and this one was found on real hardware rather than reasoned about.</b> An
/// output slower than the window delivers its probes <i>after their own windows have closed</i>.
/// Those read as "not found" — which is honest — but an older probe can then drift into a
/// <i>later</i> window and be reported as a tiny delay. A 2019 F-150's Bluetooth link did exactly
/// this: a reference speaker read a steady 93 ms while the truck produced mostly misses and the
/// occasional impossible 0.6 ms.</item>
/// </list>
/// <para>
/// <see cref="Spacing"/> closes the forward direction. The backward one needs two things the
/// caller must supply: a <c>maxLag</c> genuinely larger than the delay being hunted, and
/// <b>distinguishable probes per output</b> — see <see cref="LatencyProbe.Chirp"/>'s sweep
/// direction. With one probe shape shared between two outputs, no amount of spacing helps, because
/// a late arrival from one output is a perfect match for the other's window.
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
    /// the gap has to exceed the window outright — with room for the probe's own length, since a
    /// match is only found once the whole sweep is in the recording.
    /// <para>
    /// This closes the forward direction only. Nothing here can rescue a delay that exceeds
    /// <paramref name="maxLag"/>: that probe misses its own window whatever the spacing, so
    /// <paramref name="maxLag"/> has to be set generously above the delay being hunted.
    /// </para>
    /// </remarks>
    public static TimeSpan Spacing(TimeSpan maxLag) =>
        maxLag + TimeSpan.FromMilliseconds(300);

    /// <summary>Find every probe and work out the delay.</summary>
    /// <param name="handoffs">Each probe played, in the order played.</param>
    /// <param name="captured">The whole recording, starting at time zero of the handoffs.</param>
    /// <param name="localProbe">The probe played to the reference speaker.</param>
    /// <param name="remoteProbe">
    /// The probe played to the output being measured. <b>Must differ from</b>
    /// <paramref name="localProbe"/> — an opposite sweep direction — or a late arrival from one
    /// output will be found in the other's window.
    /// </param>
    /// <param name="maxLagSamples">
    /// How far past each handover to search. Set this above the delay being hunted: a delay past it
    /// misses its own window and reads as not found.
    /// </param>
    /// <param name="localOutputLatency">The reference speaker's own output delay.</param>
    /// <param name="remoteMetres">Distance from the measured speakers to the microphone.</param>
    /// <param name="localMetres">Distance from the reference speaker to the microphone.</param>
    public static SessionResult Analyse(
        IReadOnlyList<ProbeHandoff> handoffs,
        ReadOnlySpan<float> captured,
        ReadOnlySpan<float> localProbe,
        ReadOnlySpan<float> remoteProbe,
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
            var wanted = handoff.Remote ? remoteProbe : localProbe;
            var match = from >= 0 && from < captured.Length
                ? LatencyProbe.Find(wanted, captured[from..], maxLagSamples)
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
