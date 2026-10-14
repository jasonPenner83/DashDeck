namespace DashDeck.Core.Audio;

/// <summary>
/// The signal processing behind "how late is this speaker?" — generating a probe tone and finding
/// it again in a recording. Platform-neutral and pure, so it is tested against synthetic
/// recordings rather than against a truck.
/// </summary>
/// <remarks>
/// The probe is a <b>swept sine</b> (a chirp), not a click. Bluetooth audio is lossy — SBC or AAC —
/// and a lossy codec smears a click into something a correlator cannot place, while a sweep spreads
/// its energy over time and comes back recognisable. The sweep also survives road noise, engine
/// noise and whatever equalisation the stereo applies, because the match is integrated across the
/// whole band instead of resting on one instant or one frequency.
/// <para>
/// Finding it again is a matched filter: slide the reference over the recording and take the lag
/// with the strongest normalised correlation. The quality of that match is reported as a
/// <b>peak-to-sidelobe ratio</b> — the peak against the spread of every other lag — because that
/// stays high at bad signal-to-noise while a plain correlation coefficient collapses. It is what
/// lets a failed measurement be <i>detected</i> instead of silently returning a confident wrong
/// number, which is the only truly bad outcome here: a wrong delay is worse than no delay.
/// </para>
/// </remarks>
public static class LatencyProbe
{
    /// <summary>Sample rate the probe and capture both use. Mono.</summary>
    /// <remarks>
    /// 16 kHz: above twice the top of the sweep, fine enough for 0.0625 ms of timing resolution
    /// (far finer than this measurement's real accuracy), and a rate every Windows microphone
    /// offers. Higher would only make the correlation slower.
    /// </remarks>
    public const int SampleRate = 16_000;

    /// <summary>Bottom of the sweep, in Hz.</summary>
    /// <remarks>
    /// Door speakers roll off below this and microphones high-pass to kill handling noise, so
    /// energy spent lower is energy thrown away.
    /// </remarks>
    public const double StartHz = 500;

    /// <summary>Top of the sweep, in Hz.</summary>
    /// <remarks>Below where Bluetooth codecs and microphone arrays start band-limiting.</remarks>
    public const double EndHz = 4_000;

    /// <summary>A match at least this strong is believed. Below it, the measurement failed.</summary>
    /// <remarks>
    /// The floor is measured, not guessed. Over a 8,000-lag search, 400 recordings containing
    /// nothing but noise peaked at <b>5.3</b>, while a probe buried at a fifth of the noise
    /// amplitude still scored <b>10.2</b> and landed on the exact sample. This sits between them,
    /// nearer the middle than either, and the tests pin both sides.
    /// <para>
    /// It is deliberately set to fail rather than guess. Refusing a faint probe costs a retry or a
    /// number typed by hand; accepting a noise peak means shifting video by a lie, and nothing
    /// downstream could tell. Repeats are the second line of defence —
    /// <see cref="LatencyCalibration"/> takes the median of several runs, so one false peak is
    /// outvoted even if it clears this bar.
    /// </para>
    /// </remarks>
    public const double MinConfidence = 8.0;

    /// <summary>Lags within this many samples of the peak are not counted as sidelobe.</summary>
    private const int GuardSamples = 64;

    /// <summary>Build the probe: a sine swept between <see cref="StartHz"/> and <see cref="EndHz"/>.</summary>
    /// <param name="duration">How long the sweep lasts. 100 ms is the tested default.</param>
    /// <param name="descending">Sweep down instead of up.</param>
    /// <remarks>
    /// Both ends are tapered by a raised cosine over a tenth of the length. Without it the sweep
    /// starts and stops on a step, which is an audible click and — worse — a transient the codec
    /// mangles into correlator noise.
    /// <para>
    /// <b>Why the direction is a parameter.</b> An upward sweep and a downward one hold the same
    /// energy in the same band — so both survive a lossy codec and a noisy cab equally — but they
    /// barely correlate with each other, because their instantaneous frequencies agree at only one
    /// instant. That makes them usable as two <i>distinguishable</i> probes. When two outputs are
    /// measured in one recording, giving each its own direction means a probe from one output can
    /// never be mistaken for the other's, however late it arrives. Timing alone cannot promise that:
    /// an output slower than the search window delivers its probes out of order, and without
    /// distinguishable probes one output's late arrival is a perfect match for the other's window.
    /// </para>
    /// </remarks>
    public static float[] Chirp(TimeSpan duration, bool descending = false)
    {
        var count = (int)Math.Round(duration.TotalSeconds * SampleRate);
        if (count <= 0)
        {
            return [];
        }

        var samples = new float[count];
        var seconds = count / (double)SampleRate;
        var from = descending ? EndHz : StartHz;
        var to = descending ? StartHz : EndHz;
        var rate = (to - from) / (2 * seconds);
        var taper = Math.Max(1, count / 10);

        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var phase = 2 * Math.PI * ((from * t) + (rate * t * t));
            var window = 1.0;

            if (i < taper)
            {
                window = 0.5 * (1 - Math.Cos(Math.PI * i / taper));
            }
            else if (i >= count - taper)
            {
                window = 0.5 * (1 - Math.Cos(Math.PI * (count - 1 - i) / taper));
            }

            samples[i] = (float)(Math.Sin(phase) * window);
        }

        return samples;
    }

    /// <summary>
    /// Find <paramref name="reference"/> inside <paramref name="recorded"/>, or report that it
    /// could not be found.
    /// </summary>
    /// <param name="reference">The probe that was played.</param>
    /// <param name="recorded">What the microphone heard.</param>
    /// <param name="maxLag">
    /// How far to search, in samples. Searching past the plausible delay only invites a stronger
    /// false peak, so this should be set from the longest delay worth believing.
    /// </param>
    /// <returns>
    /// The lag in samples and how strong the match was. <c>Found</c> is false when nothing in the
    /// recording matched well enough to act on.
    /// </returns>
    public static ProbeMatch Find(ReadOnlySpan<float> reference, ReadOnlySpan<float> recorded, int maxLag)
    {
        if (reference.IsEmpty || recorded.Length < reference.Length)
        {
            return ProbeMatch.NotFound;
        }

        var lastLag = Math.Min(maxLag, recorded.Length - reference.Length);
        if (lastLag < 0)
        {
            return ProbeMatch.NotFound;
        }

        var referenceEnergy = 0.0;
        foreach (var sample in reference)
        {
            referenceEnergy += sample * sample;
        }

        if (referenceEnergy <= 0)
        {
            return ProbeMatch.NotFound;
        }

        var referenceNorm = Math.Sqrt(referenceEnergy);
        var scores = new double[lastLag + 1];

        for (var lag = 0; lag <= lastLag; lag++)
        {
            var dot = 0.0;
            var energy = 0.0;

            for (var i = 0; i < reference.Length; i++)
            {
                var heard = recorded[lag + i];
                dot += reference[i] * heard;
                energy += heard * heard;
            }

            // Normalising by the window's own energy makes the score indifferent to how loud the
            // stereo is set, which is not a setting this measurement gets to control.
            scores[lag] = energy > 0 ? Math.Abs(dot) / (referenceNorm * Math.Sqrt(energy)) : 0;
        }

        var peak = 0;
        for (var lag = 1; lag <= lastLag; lag++)
        {
            if (scores[lag] > scores[peak])
            {
                peak = lag;
            }
        }

        // Peak against the spread of everything else. A real arrival is a spike on a flat field;
        // noise alone is a field with no spike, and the ratio is what tells them apart.
        var sidelobeEnergy = 0.0;
        var sidelobeCount = 0;
        for (var lag = 0; lag <= lastLag; lag++)
        {
            if (Math.Abs(lag - peak) <= GuardSamples)
            {
                continue;
            }

            sidelobeEnergy += scores[lag] * scores[lag];
            sidelobeCount++;
        }

        if (sidelobeCount == 0)
        {
            return ProbeMatch.NotFound;
        }

        var sidelobe = Math.Sqrt(sidelobeEnergy / sidelobeCount);
        var confidence = sidelobe > 0 ? scores[peak] / sidelobe : 0;

        return new ProbeMatch(peak, confidence, confidence >= MinConfidence);
    }

    /// <summary>Turn a lag in samples into a duration.</summary>
    public static TimeSpan ToTime(int samples) =>
        TimeSpan.FromSeconds(samples / (double)SampleRate);
}

/// <summary>Where the probe was found in a recording, and whether to believe it.</summary>
/// <param name="LagSamples">Offset of the match from the start of the recording.</param>
/// <param name="Confidence">Peak-to-sidelobe ratio of the match.</param>
/// <param name="Found">Whether the match cleared <see cref="LatencyProbe.MinConfidence"/>.</param>
public readonly record struct ProbeMatch(int LagSamples, double Confidence, bool Found)
{
    /// <summary>Nothing usable in the recording.</summary>
    public static ProbeMatch NotFound { get; } = new(0, 0, false);

    /// <summary>The lag as a duration.</summary>
    public TimeSpan Lag => LatencyProbe.ToTime(LagSamples);
}
