using DashDeck.Core.Audio;

namespace DashDeck.WinAudio;

/// <summary>What to measure, and how carefully.</summary>
/// <param name="LocalDevice">The reference speaker — built into the machine.</param>
/// <param name="RemoteDevice">The output being measured.</param>
/// <param name="MicDevice">Which input to listen on, or <see cref="WaveDevices.Mapper"/>.</param>
/// <param name="Rounds">Probes per output.</param>
/// <param name="MaxDelay">How far past each probe to search.</param>
/// <param name="LeadIn">Carrier before the first probe, covering the stream's one cold start.</param>
/// <param name="LocalOutputLatency">
/// The reference speaker's own output delay. The one term that cannot be cancelled, so it is
/// supplied rather than assumed silently.
/// </param>
/// <param name="RemoteMetres">Measured speakers to microphone.</param>
/// <param name="LocalMetres">Reference speaker to microphone.</param>
public sealed record MeasurementRequest(
    int LocalDevice,
    int RemoteDevice,
    int MicDevice = WaveDevices.Mapper,
    int Rounds = 5,
    TimeSpan? MaxDelay = null,
    TimeSpan? LeadIn = null,
    TimeSpan? LocalOutputLatency = null,
    double RemoteMetres = 1.0,
    double LocalMetres = 0.2)
{
    /// <summary>The search window, defaulted.</summary>
    public TimeSpan Window => MaxDelay ?? TimeSpan.FromMilliseconds(1000);

    /// <summary>The lead-in, defaulted.</summary>
    public TimeSpan Lead => LeadIn ?? TimeSpan.FromSeconds(3);

    /// <summary>The reference speaker's own delay, defaulted.</summary>
    public TimeSpan ReferenceLatency => LocalOutputLatency ?? TimeSpan.FromMilliseconds(15);

    /// <summary>How long a run will take, so a caller can say so before starting.</summary>
    public TimeSpan Duration =>
        new ProbePlan(Lead, ProbeSession.Spacing(Window), Rounds).Duration
            + Window + TimeSpan.FromSeconds(1);
}

/// <summary>What a run produced.</summary>
/// <param name="Delay">The output's delay, or null when none was established.</param>
/// <param name="Session">Every probe and both summaries, for showing the person.</param>
/// <param name="Gaps">Capture buffers that came back short; any means the run is suspect.</param>
/// <param name="GapTime">How much of the recording was padding rather than sound.</param>
/// <param name="Problem">Why nothing was established, in one line, or null.</param>
public sealed record MeasurementOutcome(
    TimeSpan? Delay,
    SessionResult Session,
    int Gaps,
    TimeSpan GapTime,
    string? Problem)
{
    /// <summary>Whether a delay was established.</summary>
    public bool Ok => Delay is not null;
}

/// <summary>
/// Runs one delay measurement: the whole of ADR-0059's method, in one place.
/// </summary>
/// <remarks>
/// <b>One implementation, two callers.</b> The console tool and Settings' AUDIO section both drive
/// this rather than each owning a copy. Four attempts on a real truck changed this method three
/// times; two copies of it would have drifted apart by the second.
/// <para>
/// Blocking, and deliberately so: it sleeps for the length of the recording. A caller with a UI
/// runs it off the interface thread.
/// </para>
/// </remarks>
public static class DelayMeasurement
{
    /// <summary>Measure, or explain why not.</summary>
    /// <param name="request">What to measure.</param>
    /// <param name="progress">Called with a line of commentary as the run proceeds.</param>
    /// <param name="cancel">Checked while waiting, so a UI can give up.</param>
    public static MeasurementOutcome Run(
        MeasurementRequest request,
        Action<string>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Rounds < LatencyCalibration.MinimumRuns)
        {
            return Failed($"At least {LatencyCalibration.MinimumRuns} rounds are needed: fewer than "
                + "that and one bad reading cannot be outvoted.");
        }

        if (request.LocalDevice == request.RemoteDevice)
        {
            return Failed("The reference and the measured output are the same device, so the "
                + "difference between them would be nothing but noise.");
        }

        if (request.RemoteDevice == WaveDevices.Mapper)
        {
            return Failed("The output under test must be named by id. \"Whatever Windows is "
                + "defaulting to\" cannot be resolved to one, so it could silently be the reference.");
        }

        // Opposite sweeps, so neither output's probe can be found in the other's window — which is
        // what an output slower than the search window would otherwise do.
        var localProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
        var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
        var rate = LatencyProbe.SampleRate;
        var maxLag = (int)(request.Window.TotalSeconds * rate);

        var plan = new ProbePlan(request.Lead, ProbeSession.Spacing(request.Window), request.Rounds);
        var session = request.Duration;

        using var recorder = WaveRecorder.Open(request.MicDevice, rate, session, out var micProblem);
        if (recorder is null)
        {
            return Failed(micProblem ?? "The microphone could not be opened.");
        }

        using var local = WavePlayer.Open(request.LocalDevice, rate, out var localProblem);
        if (local is null)
        {
            return Failed(localProblem ?? "The reference speaker could not be opened.");
        }

        using var remote = WavePlayer.Open(request.RemoteDevice, rate, out var remoteProblem);
        if (remote is null)
        {
            return Failed(remoteProblem ?? "The output under test could not be opened.");
        }

        var localTrack = ProbeTrack.Build(localProbe, plan.LocalOffsets, plan.Duration, seed: 2);
        var remoteTrack = ProbeTrack.Build(remoteProbe, plan.RemoteOffsets, plan.Duration, seed: 3);

        progress?.Invoke($"Recording {session.TotalSeconds:F0} s. Keep quiet.");

        var clock = recorder.Start();

        var localAt = clock.Elapsed;
        if (!local.Play(localTrack))
        {
            return Failed("Windows refused the reference track.");
        }

        var remoteAt = clock.Elapsed;
        if (!remote.Play(remoteTrack))
        {
            return Failed("Windows refused the track for the output under test.");
        }

        var handoffs = plan.Handoffs(localAt, remoteAt);

        while (clock.Elapsed < session)
        {
            if (cancel.IsCancellationRequested)
            {
                return Failed("Stopped.");
            }

            progress?.Invoke($"{(session - clock.Elapsed).TotalSeconds:F0} s left");
            Thread.Sleep(500);
        }

        var captured = recorder.Stop();
        var gapTime = TimeSpan.FromSeconds(recorder.GapSamples / (double)rate);

        var result = ProbeSession.Analyse(
            handoffs,
            captured,
            localProbe,
            remoteProbe,
            maxLag,
            request.ReferenceLatency,
            request.RemoteMetres,
            request.LocalMetres);

        var problem = result.Problem switch
        {
            SessionProblem.None => null,

            SessionProblem.ReferenceUnsteady =>
                $"The reference speaker disagreed with itself by {result.Local.Spread.TotalMilliseconds:F0} ms, "
                + $"past the {LatencyCalibration.MaxReferenceSpread.TotalMilliseconds:F0} ms allowed. The whole "
                + "method subtracts the reference from the measured output, so a delay from this would be fiction.",

            SessionProblem.Impossible =>
                "The measured output came out earlier than the reference, which is impossible — "
                + "something matched the wrong thing.",

            _ when result.Local.Ok =>
                $"The reference was heard every time and the measured output was not, so the delay is "
                + $"longer than the {request.Window.TotalMilliseconds:F0} ms being searched. Raise the window.",

            _ =>
                "The reference speaker was not heard either, so the microphone is not picking up this "
                + "machine's own output. Echo cancellation is the usual cause — it exists to remove "
                + "sound the machine just played, which is exactly what this plays.",
        };

        return new MeasurementOutcome(result.Delay, result, recorder.Gaps, gapTime, problem);

        static MeasurementOutcome Failed(string why) =>
            new(null, default, 0, TimeSpan.Zero, why);
    }
}
