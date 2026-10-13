using DashDeck.Core.Audio;

namespace DashDeck.Tests;

/// <summary>
/// The delay measurement, tested against synthetic recordings. A truck is not needed to prove the
/// arithmetic — only to find out what the number is. These build a recording with a delay put into
/// it on purpose, then check the measurement gets it back out.
/// </summary>
public class AudioDelayTests
{
    private static readonly TimeSpan ProbeLength = TimeSpan.FromMilliseconds(100);

    /// <summary>Place the probe at a known offset in a buffer of the given length.</summary>
    private static float[] Recording(float[] probe, int lagSamples, int totalSamples, float gain = 1f)
    {
        var recorded = new float[totalSamples];
        for (var i = 0; i < probe.Length && lagSamples + i < totalSamples; i++)
        {
            recorded[lagSamples + i] = probe[i] * gain;
        }

        return recorded;
    }

    private static void AddNoise(float[] signal, double amplitude, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < signal.Length; i++)
        {
            // Two uniforms summed: close enough to gaussian for a noise floor.
            signal[i] += (float)((random.NextDouble() + random.NextDouble() - 1) * amplitude);
        }
    }

    [Fact]
    public void Chirp_sweeps_and_fades_in_at_both_ends()
    {
        var probe = LatencyProbe.Chirp(ProbeLength);

        Assert.Equal(1600, probe.Length);

        // Tapered ends: a sweep that starts on a step clicks, and a codec turns that click into
        // correlator noise.
        Assert.True(Math.Abs(probe[0]) < 0.01f);
        Assert.True(Math.Abs(probe[^1]) < 0.01f);
        Assert.True(probe.Max(Math.Abs) > 0.9f);
    }

    [Fact]
    public void Finds_a_clean_probe_exactly()
    {
        var probe = LatencyProbe.Chirp(ProbeLength);
        var recorded = Recording(probe, lagSamples: 3200, totalSamples: 16000);

        var match = LatencyProbe.Find(probe, recorded, maxLag: 8000);

        Assert.True(match.Found);
        Assert.Equal(3200, match.LagSamples);
        Assert.Equal(200, match.Lag.TotalMilliseconds, 1);
    }

    [Fact]
    public void Finds_the_probe_under_noise_louder_than_itself()
    {
        // Road noise, engine, tyres: the cab is not quiet, and the probe is played at a civil
        // volume. The matched filter concentrates 100 ms of sweep into one peak, which is what
        // makes this survivable.
        var probe = LatencyProbe.Chirp(ProbeLength);
        var recorded = Recording(probe, lagSamples: 2800, totalSamples: 16000, gain: 0.25f);
        AddNoise(recorded, amplitude: 0.5, seed: 7);

        var match = LatencyProbe.Find(probe, recorded, maxLag: 8000);

        Assert.True(match.Found);
        Assert.InRange(match.LagSamples, 2795, 2805);
    }

    [Fact]
    public void Finds_the_probe_through_gain_eq_and_clipping()
    {
        // Stands in for what the chain actually does to the probe: the stereo's equalisation and
        // speed-compensated volume, plus a lossy codec. Correlation integrates across the whole
        // sweep, so none of it moves the peak.
        var probe = LatencyProbe.Chirp(ProbeLength);
        var recorded = Recording(probe, lagSamples: 4000, totalSamples: 16000, gain: 3.0f);

        for (var i = 1; i < recorded.Length; i++)
        {
            recorded[i] = Math.Clamp(recorded[i], -0.8f, 0.8f);         // driven into clipping
            recorded[i] = (0.6f * recorded[i]) + (0.4f * recorded[i - 1]); // tilted by a filter
        }

        AddNoise(recorded, amplitude: 0.05, seed: 11);

        var match = LatencyProbe.Find(probe, recorded, maxLag: 8000);

        Assert.True(match.Found);
        Assert.InRange(match.LagSamples, 3995, 4010);
    }

    [Fact]
    public void Reports_failure_when_the_probe_never_arrived()
    {
        // The one outcome that must never be a confident number: the stereo was muted, the link
        // dropped, or the microphone is deaf. A wrong delay is worse than no delay, because the
        // dash would then shift video by a lie.
        var probe = LatencyProbe.Chirp(ProbeLength);
        var recorded = new float[16000];
        AddNoise(recorded, amplitude: 0.3, seed: 3);

        var match = LatencyProbe.Find(probe, recorded, maxLag: 8000);

        Assert.False(match.Found);
        Assert.True(
            match.Confidence < LatencyProbe.MinConfidence,
            $"noise alone scored {match.Confidence:F2}, at or above the {LatencyProbe.MinConfidence} floor");
    }

    [Fact]
    public void Reports_failure_on_silence()
    {
        var probe = LatencyProbe.Chirp(ProbeLength);

        var match = LatencyProbe.Find(probe, new float[16000], maxLag: 8000);

        Assert.False(match.Found);
    }

    [Fact]
    public void Confidence_separates_a_real_arrival_from_noise_by_a_wide_margin()
    {
        // The floor is only useful if the two cases are not close. This pins the gap so that
        // tightening the probe later cannot quietly erase it.
        var probe = LatencyProbe.Chirp(ProbeLength);

        var withProbe = Recording(probe, lagSamples: 2000, totalSamples: 16000, gain: 0.25f);
        AddNoise(withProbe, amplitude: 0.5, seed: 21);

        var withoutProbe = new float[16000];
        AddNoise(withoutProbe, amplitude: 0.5, seed: 21);

        var real = LatencyProbe.Find(probe, withProbe, maxLag: 8000);
        var nothing = LatencyProbe.Find(probe, withoutProbe, maxLag: 8000);

        Assert.True(
            real.Confidence > nothing.Confidence * 2,
            $"real {real.Confidence:F2} vs noise {nothing.Confidence:F2} is too close to call");
    }

    [Fact]
    public void No_amount_of_noise_alone_clears_the_confidence_floor()
    {
        // The floor's whole job is to sit above what noise can reach, so one draw of noise proves
        // nothing. Four hundred of them is what set the floor in the first place: the worst scored
        // 5.3 against a floor of 8. Pinned here so that retuning the probe cannot quietly close
        // that gap and start returning confident wrong delays.
        var probe = LatencyProbe.Chirp(ProbeLength);
        var worst = 0.0;

        // A hundred draws here rather than the four hundred that set the floor: enough to catch a
        // regression, cheap enough to run on every build.
        for (var seed = 0; seed < 100; seed++)
        {
            var recorded = new float[8000];
            AddNoise(recorded, amplitude: 0.5, seed: seed);
            worst = Math.Max(worst, LatencyProbe.Find(probe, recorded, maxLag: 4000).Confidence);
        }

        Assert.True(
            worst < LatencyProbe.MinConfidence,
            $"noise alone reached {worst:F2} against a floor of {LatencyProbe.MinConfidence}");

        // And not merely under it — comfortably under, with room for a draw worse than any of these.
        Assert.True(worst < LatencyProbe.MinConfidence * 0.8, $"only {worst:F2} of margin to the floor");
    }

    [Fact]
    public void A_probe_a_fifth_as_loud_as_the_noise_is_still_found_exactly()
    {
        // The other side of the floor: the weakest probe that must still work. At a fifth of the
        // noise amplitude it scores about 10 against the floor of 8 and lands on the exact sample.
        // Anything fainter is allowed to fail, and does.
        var probe = LatencyProbe.Chirp(ProbeLength);

        for (var seed = 0; seed < 8; seed++)
        {
            var recorded = Recording(probe, lagSamples: 3200, totalSamples: 16000, gain: 0.1f);
            AddNoise(recorded, amplitude: 0.5, seed: seed);

            var match = LatencyProbe.Find(probe, recorded, maxLag: 8000);

            Assert.True(match.Found, $"seed {seed} scored only {match.Confidence:F2}");
            Assert.Equal(3200, match.LagSamples);
        }
    }

    [Fact]
    public void Median_ignores_one_wild_run()
    {
        // A dropped Bluetooth packet or a truck going past ruins one probe. A mean would carry a
        // share of it into the stored number forever.
        var runs = new[]
        {
            new ProbeMatch(3200, 40, true),
            new ProbeMatch(3210, 38, true),
            new ProbeMatch(9000, 12, true),
            new ProbeMatch(3205, 41, true),
            new ProbeMatch(3202, 39, true),
        };

        var summary = LatencyCalibration.Summarise(runs);

        Assert.True(summary.Ok);
        Assert.Equal(3205, (int)Math.Round(summary.Lag.TotalSeconds * LatencyProbe.SampleRate));
    }

    [Fact]
    public void Runs_that_did_not_match_are_not_counted()
    {
        var runs = new[]
        {
            new ProbeMatch(3200, 40, true),
            ProbeMatch.NotFound,
            ProbeMatch.NotFound,
            new ProbeMatch(3204, 38, true),
        };

        var summary = LatencyCalibration.Summarise(runs);

        Assert.False(summary.Ok);
        Assert.Equal(2, summary.Accepted);
    }

    [Fact]
    public void Spread_reports_how_much_the_runs_disagreed()
    {
        var runs = new[]
        {
            new ProbeMatch(3200, 40, true),
            new ProbeMatch(3360, 38, true),
            new ProbeMatch(3280, 41, true),
        };

        var summary = LatencyCalibration.Summarise(runs);

        Assert.True(summary.Ok);
        Assert.Equal(10, summary.Spread.TotalMilliseconds, 1);
    }

    [Fact]
    public void Comparing_against_the_tablet_speaker_cancels_the_microphone_delay()
    {
        // The whole point of measuring twice. Both runs carry the same unknown capture delay, so
        // the subtraction removes it without ever having to know what it was.
        var captureDelay = TimeSpan.FromMilliseconds(23);
        var localOutput = TimeSpan.FromMilliseconds(12);
        var trueRemote = TimeSpan.FromMilliseconds(210);

        var flightLocal = TimeSpan.FromSeconds(0.2 / LatencyCalibration.SpeedOfSound);
        var flightRemote = TimeSpan.FromSeconds(1.0 / LatencyCalibration.SpeedOfSound);

        var heardFromRemote = trueRemote + flightRemote + captureDelay;
        var heardFromLocal = localOutput + flightLocal + captureDelay;

        var resolved = LatencyCalibration.RemoteOutputLatency(heardFromRemote, heardFromLocal, localOutput);

        Assert.NotNull(resolved);
        Assert.Equal(trueRemote.TotalMilliseconds, resolved!.Value.TotalMilliseconds, 1);
    }

    [Fact]
    public void Cancellation_holds_whatever_the_microphone_delay_turns_out_to_be()
    {
        var localOutput = TimeSpan.FromMilliseconds(12);
        var trueRemote = TimeSpan.FromMilliseconds(180);

        foreach (var captureDelay in new[] { 5, 15, 30, 60 })
        {
            var capture = TimeSpan.FromMilliseconds(captureDelay);
            var resolved = LatencyCalibration.RemoteOutputLatency(
                trueRemote + TimeSpan.FromSeconds(1.0 / LatencyCalibration.SpeedOfSound) + capture,
                localOutput + TimeSpan.FromSeconds(0.2 / LatencyCalibration.SpeedOfSound) + capture,
                localOutput);

            Assert.Equal(trueRemote.TotalMilliseconds, resolved!.Value.TotalMilliseconds, 1);
        }
    }

    [Fact]
    public void A_negative_result_is_reported_as_nothing_rather_than_as_a_delay()
    {
        var resolved = LatencyCalibration.RemoteOutputLatency(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(12));

        Assert.Null(resolved);
    }

    [Fact]
    public void A_whole_measurement_recovers_the_delay_it_was_given()
    {
        // End to end through both halves: synthetic recordings of five probes per output, summarised
        // and differenced, against a delay chosen in advance.
        var probe = LatencyProbe.Chirp(ProbeLength);
        var localOutput = TimeSpan.FromMilliseconds(12);
        var captureDelay = TimeSpan.FromMilliseconds(23);
        var trueRemote = TimeSpan.FromMilliseconds(205);

        static int Samples(TimeSpan t) => (int)Math.Round(t.TotalSeconds * LatencyProbe.SampleRate);

        var remoteRuns = new List<ProbeMatch>();
        var localRuns = new List<ProbeMatch>();

        for (var run = 0; run < 5; run++)
        {
            var remoteLag = Samples(trueRemote + captureDelay
                + TimeSpan.FromSeconds(1.0 / LatencyCalibration.SpeedOfSound));
            var localLag = Samples(localOutput + captureDelay
                + TimeSpan.FromSeconds(0.2 / LatencyCalibration.SpeedOfSound));

            var remote = Recording(probe, remoteLag, 16000, gain: 0.3f);
            AddNoise(remote, 0.25, seed: 100 + run);
            remoteRuns.Add(LatencyProbe.Find(probe, remote, 8000));

            var local = Recording(probe, localLag, 16000, gain: 0.5f);
            AddNoise(local, 0.25, seed: 200 + run);
            localRuns.Add(LatencyProbe.Find(probe, local, 8000));
        }

        var remoteSummary = LatencyCalibration.Summarise(remoteRuns);
        var localSummary = LatencyCalibration.Summarise(localRuns);

        Assert.True(remoteSummary.Ok);
        Assert.True(localSummary.Ok);

        var resolved = LatencyCalibration.RemoteOutputLatency(
            remoteSummary.Lag,
            localSummary.Lag,
            localOutput);

        Assert.NotNull(resolved);
        Assert.Equal(trueRemote.TotalMilliseconds, resolved!.Value.TotalMilliseconds, 1);
    }

    [Theory]
    [InlineData(AudioUse.Music)]
    [InlineData(AudioUse.SpokenPrompt)]
    [InlineData(AudioUse.VideoWithPicture)]
    [InlineData(AudioUse.TouchFeedback)]
    public void Nothing_is_shifted_until_a_delay_is_known(AudioUse use)
    {
        var plan = AudioDelayPolicy.For(use, AudioDelay.None);

        Assert.Equal(TimeSpan.Zero, plan.HoldPictureBy);
        Assert.Equal(TimeSpan.Zero, plan.StartAudioEarlyBy);
        Assert.False(plan.PreferLocalOutput);
    }

    [Fact]
    public void Video_waits_for_its_sound_rather_than_the_sound_being_hurried()
    {
        var delay = AudioDelay.FromHand("SYNC 3", TimeSpan.FromMilliseconds(200));

        var plan = AudioDelayPolicy.For(AudioUse.VideoWithPicture, delay);

        Assert.Equal(200, plan.HoldPictureBy.TotalMilliseconds, 1);
        Assert.Equal(TimeSpan.Zero, plan.StartAudioEarlyBy);
    }

    [Fact]
    public void A_turn_instruction_is_spoken_early_so_it_lands_on_time()
    {
        var delay = AudioDelay.FromHand("SYNC 3", TimeSpan.FromMilliseconds(200));

        var plan = AudioDelayPolicy.For(AudioUse.SpokenPrompt, delay);

        Assert.Equal(200, plan.StartAudioEarlyBy.TotalMilliseconds, 1);
        Assert.Equal(TimeSpan.Zero, plan.HoldPictureBy);
    }

    [Fact]
    public void A_tap_click_is_moved_off_the_delayed_output_because_it_cannot_be_fixed()
    {
        var delay = AudioDelay.FromHand("SYNC 3", TimeSpan.FromMilliseconds(200));

        var plan = AudioDelayPolicy.For(AudioUse.TouchFeedback, delay);

        Assert.True(plan.PreferLocalOutput);
        Assert.Equal(TimeSpan.Zero, plan.HoldPictureBy);
        Assert.Equal(TimeSpan.Zero, plan.StartAudioEarlyBy);
    }

    [Fact]
    public void Music_is_left_alone()
    {
        var delay = AudioDelay.FromHand("SYNC 3", TimeSpan.FromMilliseconds(200));

        var plan = AudioDelayPolicy.For(AudioUse.Music, delay);

        Assert.Equal(TimeSpan.Zero, plan.HoldPictureBy);
        Assert.Equal(TimeSpan.Zero, plan.StartAudioEarlyBy);
        Assert.False(plan.PreferLocalOutput);
    }

    [Fact]
    public void A_hand_typed_delay_is_marked_as_such_and_carries_no_spread()
    {
        var delay = AudioDelay.FromHand("SYNC 3", TimeSpan.FromMilliseconds(180));

        Assert.Equal(DelaySource.Manual, delay.Source);
        Assert.Equal(TimeSpan.Zero, delay.Spread);
        Assert.Null(delay.MeasuredAt);
        Assert.True(delay.IsKnown);
    }

    [Fact]
    public void A_zero_delay_counts_as_nothing_to_compensate()
    {
        var delay = AudioDelay.FromHand("AUX", TimeSpan.Zero);

        Assert.False(delay.IsKnown);
    }
}
