using DashDeck.Core.Audio;

namespace DashDeck.Tests;

/// <summary>
/// A whole measurement session, read out of a synthetic recording. These build the recording the
/// microphone would have produced — probes alternating between two outputs, each arriving late by
/// a chosen amount — and check the session comes back with the delay that was put in.
/// </summary>
public class ProbeSessionTests
{
    private const int Rate = LatencyProbe.SampleRate;

    private static readonly TimeSpan LocalOutput = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(23);

    private static readonly TimeSpan FlightLocal =
        TimeSpan.FromSeconds(0.2 / LatencyCalibration.SpeedOfSound);

    private static readonly TimeSpan FlightRemote =
        TimeSpan.FromSeconds(1.0 / LatencyCalibration.SpeedOfSound);

    private static int Samples(TimeSpan t) => (int)Math.Round(t.TotalSeconds * Rate);

    /// <summary>
    /// Build what the microphone would have heard: each probe laid into the recording at its
    /// handover plus the delay of whichever output it went to.
    /// </summary>
    private static float[] Record(
        IReadOnlyList<ProbeHandoff> handoffs,
        float[] probe,
        TimeSpan remoteDelay,
        TimeSpan sessionLength,
        double noise = 0.15,
        int seed = 5,
        float[]? remoteProbe = null)
    {
        var captured = new float[Samples(sessionLength)];
        var random = new Random(seed);
        remoteProbe ??= LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);

        foreach (var handoff in handoffs)
        {
            var arrival = handoff.Remote
                ? handoff.At + remoteDelay + FlightRemote + CaptureDelay
                : handoff.At + LocalOutput + FlightLocal + CaptureDelay;

            var played = handoff.Remote ? remoteProbe : probe;
            var at = Samples(arrival);
            for (var i = 0; i < played.Length && at + i < captured.Length; i++)
            {
                captured[at + i] += played[i] * 0.3f;
            }
        }

        for (var i = 0; i < captured.Length; i++)
        {
            captured[i] += (float)((random.NextDouble() + random.NextDouble() - 1) * noise);
        }

        return captured;
    }

    private static List<ProbeHandoff> Alternating(int rounds, TimeSpan gap)
    {
        var handoffs = new List<ProbeHandoff>();
        var at = gap;

        for (var round = 0; round < rounds; round++)
        {
            handoffs.Add(new ProbeHandoff(false, at));
            at += gap;
            handoffs.Add(new ProbeHandoff(true, at));
            at += gap;
        }

        return handoffs;
    }

    [Fact]
    public void A_whole_session_recovers_the_delay_it_was_given()
    {
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(5, gap);
        var remoteDelay = TimeSpan.FromMilliseconds(205);

        var captured = Record(handoffs, probe, remoteDelay, gap * ((5 * 2) + 2));

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.True(result.Ok);
        Assert.Equal(10, result.Probes.Count);
        Assert.Equal(5, result.Local.Accepted);
        Assert.Equal(5, result.Remote.Accepted);
        Assert.Equal(remoteDelay.TotalMilliseconds, result.Delay!.Value.TotalMilliseconds, 1);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(150)]
    [InlineData(205)]
    [InlineData(330)]
    [InlineData(500)]
    public void It_recovers_delays_across_the_range_a_stereo_might_have(int delayMs)
    {
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(3, gap);

        var captured = Record(handoffs, probe, TimeSpan.FromMilliseconds(delayMs), gap * ((3 * 2) + 2));

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.True(result.Ok, $"{delayMs} ms was not established");
        Assert.Equal(delayMs, result.Delay!.Value.TotalMilliseconds, 1);
    }

    [Fact]
    public void Probes_spaced_by_the_stated_rule_do_not_catch_the_next_probe()
    {
        // The trap this guards. Each probe is searched for in a window running maxLag forward from
        // its handover; the next probe is a perfect match for the reference, so if it falls inside
        // that window it wins and the answer comes back confident and wrong. Spacing() is the rule
        // that prevents it, and this holds it to the boundary.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(400);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(4, gap);
        var remoteDelay = TimeSpan.FromMilliseconds(120);

        var captured = Record(handoffs, probe, remoteDelay, gap * ((4 * 2) + 2));

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.True(result.Ok);
        Assert.Equal(remoteDelay.TotalMilliseconds, result.Delay!.Value.TotalMilliseconds, 1);

        // Every probe landed in its own window, not a neighbour's: no reading is anywhere near a
        // whole gap out.
        foreach (var (_, match) in result.Probes)
        {
            Assert.True(match.Found);
            Assert.True(
                match.Lag < gap,
                $"a probe was found {match.Lag.TotalMilliseconds:F0} ms out, past the {gap.TotalMilliseconds:F0} ms gap — "
                + "that is a neighbour's probe, not its own");
        }
    }

    [Fact]
    public void Probes_spaced_too_closely_are_caught_by_the_next_probe()
    {
        // The same arrangement with the rule broken, to show the rule is load-bearing rather than
        // decorative. With the gap well inside the search window, the reference probe's window
        // swallows the one after it.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var tooClose = TimeSpan.FromMilliseconds(200);   // far less than Spacing(600 ms)
        var handoffs = Alternating(4, tooClose);

        // One probe shape on both paths, which is what makes the forward collision possible.
        var captured = Record(handoffs, probe, TimeSpan.FromMilliseconds(450),
            tooClose * ((4 * 2) + 2), remoteProbe: probe);

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, probe, Samples(maxLag), LocalOutput);

        // It finds probes — they are real probes — but at least one is a neighbour's, so the
        // reading is not the delay that was put in.
        var wrong = result.Delay is null
            || Math.Abs(result.Delay.Value.TotalMilliseconds - 450) > 10;

        Assert.True(wrong,
            "spacing probes inside the search window produced the right answer by luck; "
            + "the rule in ProbeSession.Spacing would then be untested rather than load-bearing");
    }

    [Fact]
    public void A_session_where_nothing_arrived_establishes_nothing()
    {
        // The stereo muted, the link dropped, or the microphone's echo cancellation ate the probe
        // (Q35). Must refuse, not guess.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(5, gap);

        var captured = new float[Samples(gap * 12)];
        var random = new Random(9);
        for (var i = 0; i < captured.Length; i++)
        {
            captured[i] = (float)((random.NextDouble() + random.NextDouble() - 1) * 0.3);
        }

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.False(result.Ok);
        Assert.Null(result.Delay);
        Assert.Equal(0, result.Local.Accepted);
        Assert.Equal(0, result.Remote.Accepted);
    }

    [Fact]
    public void A_session_where_only_the_stereo_is_silent_establishes_nothing()
    {
        // Half a session is worse than none, because it looks like it worked. The reference probes
        // are found, the stereo's are not, and no delay may be reported from that.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(5, gap);

        var local = handoffs.Where(h => !h.Remote).ToList();
        var captured = Record(local, probe, TimeSpan.Zero, gap * 12);

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.False(result.Ok);
        Assert.Null(result.Delay);
        Assert.Equal(5, result.Local.Accepted);
        Assert.Equal(0, result.Remote.Accepted);
    }

    [Fact]
    public void A_handoff_past_the_end_of_the_recording_is_not_counted()
    {
        // The recording stopped early. Those probes are missing, not zero.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(5, gap);

        var truncated = Record(handoffs, probe, TimeSpan.FromMilliseconds(200), gap * 12)
            .Take(Samples(gap * 4))
            .ToArray();

        var result = ProbeSession.Analyse(
            handoffs, truncated, probe, remoteProbe, Samples(maxLag), LocalOutput);

        Assert.Equal(10, result.Probes.Count);
        Assert.True(result.Local.Accepted < 5);
        Assert.True(result.Remote.Accepted < 5);
    }

    [Fact]
    public void Spacing_is_longer_than_the_window_it_protects()
    {
        foreach (var ms in new[] { 100, 400, 600, 1000 })
        {
            var maxLag = TimeSpan.FromMilliseconds(ms);
            Assert.True(ProbeSession.Spacing(maxLag) > maxLag);
        }
    }

    [Fact]
    public void An_up_sweep_and_a_down_sweep_do_not_match_each_other()
    {
        // The property the two-output measurement rests on. Both carry the same energy in the same
        // band, so neither is favoured by the codec or the cab, but neither is found in the other's
        // window however it lines up.
        var up = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var down = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);

        var recording = Recording(down, lagSamples: 3200, totalSamples: 16000);

        var itself = LatencyProbe.Find(down, recording, maxLag: 8000);
        var theOther = LatencyProbe.Find(up, recording, maxLag: 8000);

        Assert.True(itself.Found);
        Assert.Equal(3200, itself.LagSamples);

        Assert.False(
            theOther.Found,
            $"the opposite sweep matched at {theOther.Confidence:F1}, so the two probes are not "
            + "distinguishable and one output's late arrival can pose as the other's");
    }

    [Fact]
    public void A_delay_past_the_search_window_reads_as_not_found_not_as_a_tiny_delay()
    {
        // The failure a 2019 F-150 produced. Its Bluetooth link ran about one slot behind, so every
        // stereo probe arrived after its own window closed, and older ones drifted into later
        // windows and were reported as impossible delays of 0.6 ms. With a probe per output, a late
        // arrival can no longer pose as the other path's — it is simply absent, which is the truth.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remote = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);

        var maxLag = TimeSpan.FromMilliseconds(600);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(5, gap);

        // Past the window on purpose, and close to one slot behind, as the truck was.
        var tooSlow = TimeSpan.FromMilliseconds(900);
        var captured = Record(handoffs, probe, tooSlow, gap * 14, remoteProbe: remote);

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remote, Samples(maxLag), LocalOutput);

        // The reference is fine, which is what tells you the rig works and the window is too small.
        Assert.Equal(5, result.Local.Accepted);

        // No delay is reported, and critically no small one.
        Assert.False(result.Ok);
        foreach (var (isRemote, match) in result.Probes.Where(p => p.Remote))
        {
            Assert.True(
                !match.Found || match.Lag > TimeSpan.FromMilliseconds(50),
                $"a stereo probe was reported at {match.Lag.TotalMilliseconds:F1} ms, which is the "
                + "impossible reading the truck produced");
            _ = isRemote;
        }
    }

    [Fact]
    public void Widening_the_window_recovers_the_delay_that_was_out_of_range()
    {
        // The other half: the same slow output measured with a window that actually covers it.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remote = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);

        var slow = TimeSpan.FromMilliseconds(900);
        var maxLag = TimeSpan.FromMilliseconds(2000);
        var gap = ProbeSession.Spacing(maxLag);
        var handoffs = Alternating(4, gap);

        var captured = Record(handoffs, probe, slow, gap * 12, remoteProbe: remote);

        var result = ProbeSession.Analyse(
            handoffs, captured, probe, remote, Samples(maxLag), LocalOutput);

        Assert.True(result.Ok);
        Assert.Equal(slow.TotalMilliseconds, result.Delay!.Value.TotalMilliseconds, 1);
    }

    /// <summary>Place a probe at a known offset, for the probe-distinguishability test.</summary>
    private static float[] Recording(float[] probe, int lagSamples, int totalSamples)
    {
        var recorded = new float[totalSamples];
        for (var i = 0; i < probe.Length && lagSamples + i < totalSamples; i++)
        {
            recorded[lagSamples + i] = probe[i];
        }

        return recorded;
    }
}
