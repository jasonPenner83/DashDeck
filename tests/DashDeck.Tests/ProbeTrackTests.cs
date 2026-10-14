using DashDeck.Core.Audio;

namespace DashDeck.Tests;

/// <summary>
/// The continuous track: probes embedded at known offsets in audio that never stops, so the
/// Bluetooth stream is never suspended and the head unit never sees a play/pause transition.
/// </summary>
public class ProbeTrackTests
{
    private static int Samples(TimeSpan t) => (int)Math.Round(t.TotalSeconds * LatencyProbe.SampleRate);

    [Fact]
    public void A_track_is_never_silent()
    {
        // The whole point. Digital silence is what lets Windows suspend the A2DP stream, which a
        // car head unit reads as pause — and the restart afterwards is what was being measured
        // instead of the delay.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var plan = new ProbePlan(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 5);

        var track = ProbeTrack.Build(probe, plan.LocalOffsets, plan.Duration);

        Assert.Equal(Samples(plan.Duration), track.Length);

        // Across the whole track, in windows of 50 ms, nothing is a run of zeroes.
        var window = Samples(TimeSpan.FromMilliseconds(50));
        for (var start = 0; start + window <= track.Length; start += window)
        {
            var energy = 0.0;
            for (var i = start; i < start + window; i++)
            {
                energy += track[i] * track[i];
            }

            Assert.True(energy > 0, $"the track is silent at {start / (double)LatencyProbe.SampleRate:F2} s");
        }
    }

    [Fact]
    public void The_carrier_is_far_below_the_probe()
    {
        // Inaudible in a cab, and nowhere near loud enough to interfere with the correlation.
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var plan = new ProbePlan(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 3);

        var track = ProbeTrack.Build(probe, plan.LocalOffsets, plan.Duration);

        // Sample the carrier a long way from any probe.
        var quiet = Samples(TimeSpan.FromSeconds(1));
        var carrierPeak = 0f;
        for (var i = quiet; i < quiet + Samples(TimeSpan.FromMilliseconds(500)); i++)
        {
            carrierPeak = Math.Max(carrierPeak, Math.Abs(track[i]));
        }

        Assert.True(carrierPeak <= ProbeTrack.CarrierLevel * 1.01f, $"carrier peaked at {carrierPeak}");
        Assert.True(carrierPeak > 0);
        Assert.True(track.Max(Math.Abs) > 0.9f, "the probe itself should still be full scale");
    }

    [Fact]
    public void Probes_sit_exactly_where_the_plan_says()
    {
        var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var plan = new ProbePlan(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 4);

        var track = ProbeTrack.Build(probe, plan.LocalOffsets, plan.Duration);

        foreach (var offset in plan.LocalOffsets)
        {
            var match = LatencyProbe.Find(probe, track.AsSpan(Samples(offset)), maxLag: 100);
            Assert.True(match.Found, $"no probe at {offset.TotalSeconds:F1} s");
            Assert.Equal(0, match.LagSamples);
        }
    }

    [Fact]
    public void The_two_outputs_alternate_and_never_collide()
    {
        var plan = new ProbePlan(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1.3), 5);

        var all = plan.LocalOffsets.Select(o => (o, Remote: false))
            .Concat(plan.RemoteOffsets.Select(o => (o, Remote: true)))
            .OrderBy(x => x.o)
            .ToList();

        Assert.Equal(10, all.Count);

        for (var i = 1; i < all.Count; i++)
        {
            Assert.NotEqual(all[i].Remote, all[i - 1].Remote);
            Assert.Equal(plan.Gap.TotalMilliseconds, (all[i].o - all[i - 1].o).TotalMilliseconds, 1);
        }
    }

    [Fact]
    public void Nothing_plays_during_the_lead_in()
    {
        // The stream's one cold start belongs here, where no probe can pay for it.
        var plan = new ProbePlan(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 5);

        Assert.True(plan.LocalOffsets.Min() >= plan.LeadIn);
        Assert.True(plan.RemoteOffsets.Min() >= plan.LeadIn);
    }

    [Fact]
    public void Handoffs_are_the_track_handover_plus_the_offset()
    {
        // Each probe's handover is exact, because the offset is a sample position in a buffer
        // rather than a moment a thread happened to wake up. Every probe on one output therefore
        // shares one start, and their readings should come out identical but for correlation noise.
        var plan = new ProbePlan(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 3);

        var localAt = TimeSpan.FromMilliseconds(120);
        var remoteAt = TimeSpan.FromMilliseconds(135);

        var handoffs = plan.Handoffs(localAt, remoteAt);

        Assert.Equal(6, handoffs.Count);
        Assert.Equal(3, handoffs.Count(h => h.Remote));

        foreach (var (offset, handoff) in plan.LocalOffsets.Zip(handoffs.Where(h => !h.Remote)))
        {
            Assert.Equal((localAt + offset).TotalMilliseconds, handoff.At.TotalMilliseconds, 1);
        }

        foreach (var (offset, handoff) in plan.RemoteOffsets.Zip(handoffs.Where(h => h.Remote)))
        {
            Assert.Equal((remoteAt + offset).TotalMilliseconds, handoff.At.TotalMilliseconds, 1);
        }

        // In play order.
        for (var i = 1; i < handoffs.Count; i++)
        {
            Assert.True(handoffs[i].At >= handoffs[i - 1].At);
        }
    }

    [Fact]
    public void A_whole_continuous_session_recovers_the_delay_it_was_given()
    {
        // End to end on the new design: two tracks, each handed over once, probes read back out of
        // one recording. Because the tracks are continuous, every probe on an output carries the
        // same delay, so the spread should be near zero rather than merely tolerable.
        var localProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);

        var maxLag = TimeSpan.FromMilliseconds(600);
        var plan = new ProbePlan(TimeSpan.FromSeconds(2), ProbeSession.Spacing(maxLag), 5);

        var localOut = TimeSpan.FromMilliseconds(40);
        var remoteOut = TimeSpan.FromMilliseconds(220);
        var capture = TimeSpan.FromMilliseconds(23);
        var flightLocal = TimeSpan.FromSeconds(0.2 / LatencyCalibration.SpeedOfSound);
        var flightRemote = TimeSpan.FromSeconds(1.0 / LatencyCalibration.SpeedOfSound);

        var localAt = TimeSpan.FromMilliseconds(100);
        var remoteAt = TimeSpan.FromMilliseconds(118);

        var localTrack = ProbeTrack.Build(localProbe, plan.LocalOffsets, plan.Duration, seed: 2);
        var remoteTrack = ProbeTrack.Build(remoteProbe, plan.RemoteOffsets, plan.Duration, seed: 3);

        // The recording: each track arriving late by its output's delay plus flight plus capture.
        var total = Samples(plan.Duration + TimeSpan.FromSeconds(3));
        var captured = new float[total];

        void Lay(float[] track, TimeSpan handedOver, TimeSpan output, TimeSpan flight, float gain)
        {
            var at = Samples(handedOver + output + flight + capture);
            for (var i = 0; i < track.Length && at + i < captured.Length; i++)
            {
                captured[at + i] += track[i] * gain;
            }
        }

        Lay(localTrack, localAt, localOut, flightLocal, 0.5f);
        Lay(remoteTrack, remoteAt, remoteOut, flightRemote, 0.4f);

        var random = new Random(11);
        for (var i = 0; i < captured.Length; i++)
        {
            captured[i] += (float)((random.NextDouble() + random.NextDouble() - 1) * 0.1);
        }

        var result = ProbeSession.Analyse(
            plan.Handoffs(localAt, remoteAt),
            captured,
            localProbe,
            remoteProbe,
            Samples(maxLag),
            localOut);

        Assert.True(result.Ok, $"problem was {result.Problem}");
        Assert.Equal(5, result.Local.Accepted);
        Assert.Equal(5, result.Remote.Accepted);
        Assert.Equal(remoteOut.TotalMilliseconds, result.Delay!.Value.TotalMilliseconds, 1);

        // The payoff: no per-probe handover jitter left to muddy either path.
        Assert.True(
            result.Local.Spread < TimeSpan.FromMilliseconds(2),
            $"reference spread was {result.Local.Spread.TotalMilliseconds:F1} ms, which a continuous "
            + "track should have removed");
        Assert.True(
            result.Remote.Spread < TimeSpan.FromMilliseconds(2),
            $"measured spread was {result.Remote.Spread.TotalMilliseconds:F1} ms");
    }
}
