using System.Globalization;
using DashDeck.AudioProbe;
using DashDeck.Core.Audio;

// AudioProbe — measures how late an audio output is (ADR-0059, Q34/Q35/Q36).
//
// Bluetooth to the truck's stereo adds something like 150-250 ms between the dash handing audio
// over and a speaker making a sound, and it cannot be removed. This finds out how much, so the
// dash can hold video back, speak turn instructions early, and keep tap clicks off that output.
//
// How it measures, and why it measures twice:
//
//   What a microphone hears is NOT the speaker's delay. It is the speaker's delay, plus the
//   sound's flight across the cab, plus however long Windows took to hand the capture over —
//   and that last term is 10-30 ms, far too much to ignore when the answer is around 200 ms.
//
//   So the same probe goes to the tablet's OWN speaker as well, in the SAME recording. The
//   capture delay is identical for both, so it cancels in the difference. What is left needs
//   only the tablet speaker's own output delay, which is small and which a built-in device
//   reports honestly — unlike a Bluetooth endpoint, where the reported figure stops at the radio.
//
// Nothing here writes to the vehicle, installs anything, or touches the registry.

try
{
    return Run();
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine("This tool needs Windows audio (winmm.dll). It only runs on Windows.");
    return 2;
}

int Run()
{
    if (Flag("--help") || Flag("-h"))
    {
        Usage();
        return 0;
    }

    if (Flag("--list"))
    {
        ListDevices();
        return 0;
    }

    var localId = IntArg("--local");
    if (localId is null)
    {
        Console.Error.WriteLine("--local is required: the id of the tablet's OWN speaker, which is the");
        Console.Error.WriteLine("reference this measurement is made against. Run --list to see the ids.");
        Console.Error.WriteLine();
        Usage();
        return 1;
    }

    var remoteId = IntArg("--remote");
    if (remoteId is null)
    {
        Console.Error.WriteLine("--remote is required: the id of the output being measured.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("It used to default to whatever Windows was defaulting to, which was a trap —");
        Console.Error.WriteLine("the wave API will not say which device that resolves to, so if the default was");
        Console.Error.WriteLine("also --local the tool measured one device against itself and the check below");
        Console.Error.WriteLine("could not tell. Name both outright. Run --list to see the ids.");
        return 1;
    }
    var micId = IntArg("--mic") ?? WaveDevices.Mapper;
    var rounds = IntArg("--rounds") ?? 5;
    var localLatency = TimeSpan.FromMilliseconds(IntArg("--local-latency-ms") ?? 15);
    var remoteMetres = DoubleArg("--remote-metres") ?? 1.0;
    var localMetres = DoubleArg("--local-metres") ?? 0.2;

    if (rounds < LatencyCalibration.MinimumRuns)
    {
        Console.Error.WriteLine($"--rounds must be at least {LatencyCalibration.MinimumRuns}: fewer than that and one");
        Console.Error.WriteLine("bad reading cannot be outvoted, which is the whole point of repeating.");
        return 1;
    }

    if (remoteId == localId)
    {
        Console.Error.WriteLine("--remote and --local are the same device. The measurement would come out at");
        Console.Error.WriteLine("roughly zero and tell you nothing. Point --remote at the stereo.");
        return 1;
    }

    // Two probes, one per output: an upward sweep for the reference and a downward one for the
    // output under test. They carry the same energy in the same band, so the codec and the cab
    // treat them alike, but neither is found in the other's window. Without that, an output slower
    // than the search window has its late arrivals found in the OTHER path's window and reported
    // as impossible delays — which is what a 2019 F-150 did at 0.6 ms.
    var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
    var remoteProbe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100), descending: true);
    var rate = LatencyProbe.SampleRate;

    // 2 s, not the 600 ms this started with. A real truck came in near or past 700 ms, and a delay
    // beyond the window misses its own slot entirely and reads as not found. Generous is cheap
    // here; too small is a measurement that cannot succeed.
    var maxLagTime = TimeSpan.FromMilliseconds(IntArg("--max-delay-ms") ?? 2000);
    var maxLag = (int)(maxLagTime.TotalSeconds * rate);

    var gap = ProbeSession.Spacing(maxLagTime);
    var session = gap * ((rounds * 2) + 4);   // + the two warm-up probes and a tail

    Console.WriteLine($"Probe       100 ms sweep, {LatencyProbe.StartHz:F0}-{LatencyProbe.EndHz:F0} Hz, {rate} Hz mono");
    Console.WriteLine($"            up for the reference, down for the measured output, so neither");
    Console.WriteLine($"            can be found in the other's window");
    Console.WriteLine($"Window      searching {maxLagTime.TotalMilliseconds:F0} ms past each handover (--max-delay-ms)");
    Console.WriteLine($"Rounds      {rounds} to each output, alternating");
    Console.WriteLine($"Reference   device {localId} (the tablet's own speaker), assumed {localLatency.TotalMilliseconds:F0} ms of output delay");
    Console.WriteLine($"Measuring   device {remoteId.Value}");
    Console.WriteLine($"Microphone  device {(micId == WaveDevices.Mapper ? "default" : micId.ToString(CultureInfo.InvariantCulture))}");
    Console.WriteLine();
    Console.WriteLine("Keep the cab as it will be in use — engine running if that is the case being");
    Console.WriteLine("measured. Do not talk over it. This takes about " + $"{session.TotalSeconds:F0} seconds.");
    Console.WriteLine();

    using var recorder = WaveRecorder.Open(micId, rate, session, out var micProblem);
    if (recorder is null)
    {
        Console.Error.WriteLine(micProblem);
        return 2;
    }

    using var local = WavePlayer.Open(localId.Value, rate, out var localProblem);
    if (local is null)
    {
        Console.Error.WriteLine(localProblem);
        return 2;
    }

    using var remote = WavePlayer.Open(remoteId.Value, rate, out var remoteProblem);
    if (remote is null)
    {
        Console.Error.WriteLine(remoteProblem);
        return 2;
    }

    // One capture for the whole run: the moment it started and the capture delay are then the
    // same constant for every probe, and both drop out of the difference.
    var clock = recorder.Start();
    var handoffs = new List<ProbeHandoff>();

    // Warm both outputs before anything is measured. A freshly opened device takes longer to
    // start than a running one, which put the first probe outside the search window every time
    // and counted it as a failure. These two are deliberately not recorded as handoffs.
    local.Play(probe);
    Thread.Sleep(gap);
    remote.Play(remoteProbe);
    Thread.Sleep(gap);

    for (var round = 0; round < rounds; round++)
    {
        Thread.Sleep(gap);
        handoffs.Add(new ProbeHandoff(false, clock.Elapsed));
        local.Play(probe);

        Thread.Sleep(gap);
        handoffs.Add(new ProbeHandoff(true, clock.Elapsed));
        remote.Play(remoteProbe);

        Console.Write($"\r  round {round + 1} of {rounds}");
    }

    Thread.Sleep(gap);
    var captured = recorder.Stop();
    Console.WriteLine($"\r  captured {captured.Length / (double)rate:F1} s   ");

    // Gaps are the difference between a measurement and a fiction. A buffer that came back short
    // is a hole in time; it is padded rather than closed so the timeline still lines up with the
    // clock the handovers were stamped against, but a probe that fell in one is simply not there.
    if (recorder.Gaps > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  WARNING: {recorder.Gaps} capture buffer(s) came back short — "
            + $"{recorder.GapSamples / (double)rate * 1000:F0} ms of this recording is padding.");
        Console.WriteLine("  The timeline is preserved, but any probe that fell in a gap is missing and");
        Console.WriteLine("  any reading here is suspect. Close other audio software and run it again.");
    }

    Console.WriteLine();

    var result = ProbeSession.Analyse(
        handoffs, captured, probe, remoteProbe, maxLag, localLatency, remoteMetres, localMetres);

    foreach (var (isRemote, match) in result.Probes)
    {
        Console.WriteLine(match.Found
            ? $"  {(isRemote ? "stereo " : "tablet ")} {match.Lag.TotalMilliseconds,7:F1} ms   confidence {match.Confidence,6:F1}"
            : $"  {(isRemote ? "stereo " : "tablet ")}       -        not found  (confidence {match.Confidence:F1})");
    }

    Console.WriteLine();

    var localSummary = result.Local;
    var remoteSummary = result.Remote;

    if (result.Problem == SessionProblem.ReferenceUnsteady)
    {
        Console.WriteLine($"Heard from the tablet   {localSummary.Lag.TotalMilliseconds,7:F1} ms   "
            + $"spread {localSummary.Spread.TotalMilliseconds:F1} ms over {localSummary.Accepted} runs");
        Console.WriteLine($"Heard from the stereo   {remoteSummary.Lag.TotalMilliseconds,7:F1} ms   "
            + $"spread {remoteSummary.Spread.TotalMilliseconds:F1} ms over {remoteSummary.Accepted} runs");
        Console.WriteLine();
        Console.WriteLine($"REFUSED. The reference speaker disagreed with itself by "
            + $"{localSummary.Spread.TotalMilliseconds:F0} ms, past the "
            + $"{LatencyCalibration.MaxReferenceSpread.TotalMilliseconds:F0} ms allowed.");
        Console.WriteLine();
        Console.WriteLine("That speaker is wired into this machine a fixed distance from its own");
        Console.WriteLine("microphone. Its readings should sit within a few milliseconds of each other —");
        Console.WriteLine("a Surface produced five inside 8 ms. Scattered readings mean the correlator is");
        Console.WriteLine("finding something other than the probe, and the whole measurement subtracts");
        Console.WriteLine("the reference from the stereo, so a delay computed from this would be fiction");
        Console.WriteLine("however tidy the stereo's own numbers look.");
        Console.WriteLine();
        Console.WriteLine("Try, in order:");
        Console.WriteLine("  * Look at the capture warning above. Gaps in the recording move arrivals.");
        Console.WriteLine("  * Close anything else using audio, and run it again.");
        Console.WriteLine($"  * Narrow the search: --max-delay-ms {Math.Max(600, maxLagTime.TotalMilliseconds / 2):F0}. A window far wider than");
        Console.WriteLine("    the delay gives the correlator more places to go wrong for no benefit.");
        Console.WriteLine("  * Fewer rounds, so the recording is shorter: --rounds 3.");
        return 3;
    }

    if (!localSummary.Ok || !remoteSummary.Ok)
    {
        Console.WriteLine($"Not enough probes were found: {localSummary.Accepted} on the tablet, "
            + $"{remoteSummary.Accepted} on the stereo, of {rounds} each.");
        Console.WriteLine();
        Console.WriteLine("This is a refusal, not a wrong answer — a confident wrong delay would be worse.");
        Console.WriteLine();

        // Which half failed says which cause it is, and they need completely different fixes.
        if (localSummary.Ok)
        {
            Console.WriteLine("The reference was found every time and only the measured output is missing,");
            Console.WriteLine("so the microphone and the timing are fine. That points at one thing:");
            Console.WriteLine();
            Console.WriteLine($"  * The delay is longer than the {maxLagTime.TotalMilliseconds:F0} ms being searched, so each probe");
            Console.WriteLine("    arrives after its own window has closed. Raise it:");
            Console.WriteLine($"      --max-delay-ms {maxLagTime.TotalMilliseconds * 2:F0}");
            Console.WriteLine();
            Console.WriteLine("  * Failing that, the output is muted or is not the device you think it is,");
            Console.WriteLine("    or the cab is far louder than the probe — turn the stereo up.");
        }
        else
        {
            Console.WriteLine("The reference speaker was not heard either, which means the microphone is not");
            Console.WriteLine("picking up this machine's own output at all. Most likely causes, in order:");
            Console.WriteLine();
            Console.WriteLine("  * The microphone's echo cancellation removed the probe. It exists to delete");
            Console.WriteLine("    sound the machine just played, which is precisely what this plays (Q35).");
            Console.WriteLine("    Turn off microphone enhancements for this device and try again.");
            Console.WriteLine("  * The wrong --mic, or the wrong --local (it must be a speaker you can hear).");
            Console.WriteLine("  * Everything is muted, or the room is far louder than the probe.");
        }

        return 3;
    }

    var resolved = result.Delay;

    Console.WriteLine($"Heard from the tablet   {localSummary.Lag.TotalMilliseconds,7:F1} ms   "
        + $"spread {localSummary.Spread.TotalMilliseconds:F1} ms over {localSummary.Accepted} runs");
    Console.WriteLine($"Heard from the stereo   {remoteSummary.Lag.TotalMilliseconds,7:F1} ms   "
        + $"spread {remoteSummary.Spread.TotalMilliseconds:F1} ms over {remoteSummary.Accepted} runs");
    Console.WriteLine();

    if (resolved is null)
    {
        Console.WriteLine("The stereo came out EARLIER than the tablet's own speaker, which is impossible.");
        Console.WriteLine("Something matched the wrong thing. Check that --local really is the built-in");
        Console.WriteLine("speaker and that --remote is not also pointing at it.");
        return 3;
    }

    Console.WriteLine($"==> Output delay: {resolved.Value.TotalMilliseconds:F0} ms");
    Console.WriteLine();
    Console.WriteLine($"Type that into the dash as the delay for this output. Spread was "
        + $"{remoteSummary.Spread.TotalMilliseconds:F1} ms:");
    Console.WriteLine(remoteSummary.Spread > TimeSpan.FromMilliseconds(20)
        ? "  WIDE. Two possible causes, and they need telling apart before you trust this:\n"
            + "  (a) the link really is unsteady, which is the Q36 answer and means compensation\n"
            + "      should be left off rather than averaged; or\n"
            + "  (b) this tool's own doing — it plays isolated bursts with silence between them,\n"
            + "      and a Bluetooth sink re-buffers after every gap. Real playback is continuous,\n"
            + "      so a wide spread here may be worse than what the dash will actually see.\n"
            + "  Measure again during continuous playback to tell them apart."
        : "  tight enough that a fixed number should hold for this connection.");
    Console.WriteLine();
    Console.WriteLine("It will move after a reconnect and after the stream has been idle, because the");
    Console.WriteLine("delay follows the receiving end's buffer. Re-measure then rather than assuming.");

    return 0;
}

void ListDevices()
{
    Console.WriteLine("Outputs");
    foreach (var device in WaveDevices.Outputs())
    {
        Console.WriteLine($"  {device.Id,3}  {device.Name}");
    }

    Console.WriteLine($"  {WaveDevices.Mapper,3}  (whatever Windows is defaulting to)");
    Console.WriteLine();
    Console.WriteLine("Inputs");
    foreach (var device in WaveDevices.Inputs())
    {
        Console.WriteLine($"  {device.Id,3}  {device.Name}");
    }

    Console.WriteLine($"  {WaveDevices.Mapper,3}  (whatever Windows is defaulting to)");
    Console.WriteLine();
    Console.WriteLine("These names are truncated to 31 characters by the wave API and carry no stable");
    Console.WriteLine("identifier, so they may not line up with what Windows' own settings show (Q34).");
    Console.WriteLine("If the built-in speaker cannot be picked out here, say so — that is the answer.");
}

void Usage()
{
    Console.WriteLine("AudioProbe — how late is this audio output? (ADR-0059)");
    Console.WriteLine();
    Console.WriteLine("  AudioProbe --list");
    Console.WriteLine("  AudioProbe --local <id> --remote <id> [--mic <id>] [options]");
    Console.WriteLine();
    Console.WriteLine("  --local <id>            The tablet's own speaker. Required: it is the reference");
    Console.WriteLine("                          that cancels the microphone's own delay.");
    Console.WriteLine("  --remote <id>           The output being measured, e.g. the Bluetooth link.");
    Console.WriteLine("                          Required, and must be an id: \"the default\" cannot be");
    Console.WriteLine("                          resolved to one, so it could silently be --local.");
    Console.WriteLine("  --mic <id>              Input to listen on. Default: Windows' default.");
    Console.WriteLine($"  --rounds <n>            Probes per output. Default 5, minimum {LatencyCalibration.MinimumRuns}.");
    Console.WriteLine("  --local-latency-ms <n>  The tablet speaker's own output delay. Default 15.");
    Console.WriteLine("                          The one term that cannot be cancelled, so it is stated.");
    Console.WriteLine("  --remote-metres <m>     Stereo speakers to tablet. Default 1.0.");
    Console.WriteLine("  --local-metres <m>      Tablet speaker to its own microphone. Default 0.2.");
    Console.WriteLine("  --max-delay-ms <n>      How far past each handover to search. Default 2000.");
    Console.WriteLine("                          A delay beyond it misses its own window and reads as");
    Console.WriteLine("                          not found, so raise it rather than lower it.");
}

bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

string? ArgValue(string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

int? IntArg(string name) =>
    int.TryParse(ArgValue(name), CultureInfo.InvariantCulture, out var value) ? value : null;

double? DoubleArg(string name) =>
    double.TryParse(ArgValue(name), CultureInfo.InvariantCulture, out var value) ? value : null;
