namespace DashDeck.Core.Audio;

/// <summary>
/// Turns probe matches into a latency the rest of the dash can act on: several runs reduced to one
/// number, and the microphone's own delay cancelled out of it.
/// </summary>
/// <remarks>
/// <b>Why one measurement is not enough.</b> What the microphone hears is not the speaker's delay.
/// It is the speaker's delay <i>plus</i> the sound's flight across the cab <i>plus</i> however long
/// Windows took to hand the capture over — and that last term is 10 to 30 ms on a shared-mode
/// microphone, which is too much to ignore when the answer being measured is around 200 ms and is
/// going to be used to shift video.
/// <para>
/// <b>The fix is to measure twice and subtract.</b> Send the same probe to the tablet's own speaker
/// as well as to the stereo, and record both through the same microphone. The capture delay is
/// identical in both runs, so it <i>cancels in the difference</i>, leaving the one quantity wanted.
/// All that has to be known separately is the tablet speaker's own output delay, which is small and
/// which Windows reports honestly for a built-in device — unlike a Bluetooth endpoint, where the
/// reported figure stops at the radio and omits everything the far end does.
/// </para>
/// </remarks>
public static class LatencyCalibration
{
    /// <summary>Accepted runs needed before a measurement is reported at all.</summary>
    public const int MinimumRuns = 3;

    /// <summary>
    /// How much the reference speaker's readings may disagree before the whole measurement is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <b>The reference is the one path that must be boring.</b> It is a speaker wired into the
    /// machine, a fixed distance from its own microphone, so its readings should sit within a few
    /// milliseconds of each other — a real Surface produced five inside 8 ms. When they scatter
    /// instead, the correlator is picking noise rather than the probe, and the subtraction the whole
    /// method rests on is then being done with a meaningless number.
    /// <para>
    /// This exists because a run on real hardware reported a confident delay built on a reference
    /// whose readings spanned <b>1605 ms</b>. Nothing checked it: the spread was computed, printed,
    /// and only ever gated on for the measured output. A reference this unsteady is not a wide error
    /// bar, it is a broken measurement, and the answer must be withheld rather than widened.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MaxReferenceSpread = TimeSpan.FromMilliseconds(50);

    /// <summary>Metres per second used to subtract the sound's flight across the cab.</summary>
    /// <remarks>
    /// Dry air at room temperature. The whole correction is about 3 ms — small enough not to matter
    /// and cheap enough to do anyway.
    /// </remarks>
    public const double SpeedOfSound = 343.0;

    /// <summary>Reduce repeated probes to one lag, discarding the ones that did not match.</summary>
    /// <param name="matches">One per probe played.</param>
    /// <remarks>
    /// The <b>median</b>, not the mean: a dropped Bluetooth packet or a truck going past produces one
    /// wild reading, and a mean would carry a share of it into the answer forever. The spread between
    /// the best and worst surviving run is reported alongside, because a wide spread is the signal
    /// that this link's delay is not stable enough to compensate with a fixed number.
    /// </remarks>
    public static RunSummary Summarise(IReadOnlyList<ProbeMatch> matches)
    {
        var found = matches.Where(m => m.Found).Select(m => m.LagSamples).OrderBy(l => l).ToArray();
        if (found.Length < MinimumRuns)
        {
            return RunSummary.Failed(found.Length);
        }

        var median = found.Length % 2 == 1
            ? found[found.Length / 2]
            : (found[(found.Length / 2) - 1] + found[found.Length / 2]) / 2;

        return new RunSummary(
            true,
            LatencyProbe.ToTime(median),
            LatencyProbe.ToTime(found[^1] - found[0]),
            found.Length,
            matches.Count);
    }

    /// <summary>
    /// The stereo's true output delay, with the microphone's delay subtracted out by comparison
    /// against the tablet's own speaker.
    /// </summary>
    /// <param name="heardFromRemote">Probe-to-microphone lag measured through the stereo.</param>
    /// <param name="heardFromLocal">Probe-to-microphone lag measured through the tablet speaker.</param>
    /// <param name="localOutputLatency">
    /// The tablet speaker's own output delay, as Windows reports it. The only term that cannot be
    /// cancelled and so the only one that has to be trusted.
    /// </param>
    /// <param name="remoteMetres">Rough distance from the truck's speakers to the tablet.</param>
    /// <param name="localMetres">Rough distance from the tablet's speaker to its own microphone.</param>
    /// <returns>
    /// The delay to compensate for, or <c>null</c> when the arithmetic comes out negative — which
    /// means a run matched the wrong thing, and reporting nothing beats reporting nonsense.
    /// </returns>
    public static TimeSpan? RemoteOutputLatency(
        TimeSpan heardFromRemote,
        TimeSpan heardFromLocal,
        TimeSpan localOutputLatency,
        double remoteMetres = 1.0,
        double localMetres = 0.2)
    {
        var flight = TimeSpan.FromSeconds((remoteMetres - localMetres) / SpeedOfSound);
        var latency = (heardFromRemote - heardFromLocal) + localOutputLatency - flight;

        return latency < TimeSpan.Zero ? null : latency;
    }
}

/// <summary>What several probes agreed on.</summary>
/// <param name="Ok">Whether enough runs matched to report anything.</param>
/// <param name="Lag">Median lag of the runs that matched.</param>
/// <param name="Spread">Best-to-worst gap across those runs.</param>
/// <param name="Accepted">How many runs matched.</param>
/// <param name="Attempted">How many probes were played.</param>
public readonly record struct RunSummary(bool Ok, TimeSpan Lag, TimeSpan Spread, int Accepted, int Attempted)
{
    /// <summary>Not enough runs matched.</summary>
    public static RunSummary Failed(int accepted) => new(false, TimeSpan.Zero, TimeSpan.Zero, accepted, 0);
}
