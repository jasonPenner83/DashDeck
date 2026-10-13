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

    var remoteId = IntArg("--remote") ?? WaveDevices.Mapper;
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

    var probe = LatencyProbe.Chirp(TimeSpan.FromMilliseconds(100));
    var rate = LatencyProbe.SampleRate;
    var maxLagTime = TimeSpan.FromMilliseconds(600);   // nothing beyond this is worth believing
    var maxLag = (int)(maxLagTime.TotalSeconds * rate);

    // Not a round number picked by feel: probes closer together than this and one probe's search
    // window reaches the next, which matches perfectly and answers confidently with the wrong
    // number. ProbeSession states the rule and its tests hold it to the boundary.
    var gap = ProbeSession.Spacing(maxLagTime);
    var session = gap * ((rounds * 2) + 2);

    Console.WriteLine($"Probe       100 ms sweep, {LatencyProbe.StartHz:F0}-{LatencyProbe.EndHz:F0} Hz, {rate} Hz mono");
    Console.WriteLine($"Rounds      {rounds} to each output, alternating");
    Console.WriteLine($"Reference   device {localId} (the tablet's own speaker), assumed {localLatency.TotalMilliseconds:F0} ms of output delay");
    Console.WriteLine($"Measuring   device {(remoteId == WaveDevices.Mapper ? "default" : remoteId.ToString(CultureInfo.InvariantCulture))}");
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

    using var remote = WavePlayer.Open(remoteId, rate, out var remoteProblem);
    if (remote is null)
    {
        Console.Error.WriteLine(remoteProblem);
        return 2;
    }

    // One capture for the whole run: the moment it started and the capture delay are then the
    // same constant for every probe, and both drop out of the difference.
    var clock = recorder.Start();
    var handoffs = new List<ProbeHandoff>();

    for (var round = 0; round < rounds; round++)
    {
        Thread.Sleep(gap);
        handoffs.Add(new ProbeHandoff(false, clock.Elapsed));
        local.Play(probe);

        Thread.Sleep(gap);
        handoffs.Add(new ProbeHandoff(true, clock.Elapsed));
        remote.Play(probe);

        Console.Write($"\r  round {round + 1} of {rounds}");
    }

    Thread.Sleep(gap);
    var captured = recorder.Stop();
    Console.WriteLine($"\r  captured {captured.Length / (double)rate:F1} s   ");
    Console.WriteLine();

    var result = ProbeSession.Analyse(handoffs, captured, probe, maxLag, localLatency, remoteMetres, localMetres);

    foreach (var (isRemote, match) in result.Probes)
    {
        Console.WriteLine(match.Found
            ? $"  {(isRemote ? "stereo " : "tablet ")} {match.Lag.TotalMilliseconds,7:F1} ms   confidence {match.Confidence,6:F1}"
            : $"  {(isRemote ? "stereo " : "tablet ")}       -        not found  (confidence {match.Confidence:F1})");
    }

    Console.WriteLine();

    var localSummary = result.Local;
    var remoteSummary = result.Remote;

    if (!localSummary.Ok || !remoteSummary.Ok)
    {
        Console.WriteLine($"Not enough probes were found: {localSummary.Accepted} on the tablet, "
            + $"{remoteSummary.Accepted} on the stereo, of {rounds} each.");
        Console.WriteLine();
        Console.WriteLine("This is a refusal, not a wrong answer — a confident wrong delay would be worse.");
        Console.WriteLine("Most likely causes, in order:");
        Console.WriteLine("  * The microphone's echo cancellation removed the probe. It exists to delete");
        Console.WriteLine("    sound the machine just played, which is precisely what this plays (Q35).");
        Console.WriteLine("    Turn off microphone enhancements for this device and try again.");
        Console.WriteLine("  * The output is muted, or is not the device you think it is (--list).");
        Console.WriteLine("  * The cab is far louder than the probe. Turn the stereo up.");
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
        ? "  WIDE — this link is not steady enough for a fixed number to hold. Measure again;\n"
            + "  if it stays wide, leave compensation off rather than trust an average (Q36)."
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
    Console.WriteLine("  AudioProbe --local <id> [--remote <id>] [--mic <id>] [options]");
    Console.WriteLine();
    Console.WriteLine("  --local <id>            The tablet's own speaker. Required: it is the reference");
    Console.WriteLine("                          that cancels the microphone's own delay.");
    Console.WriteLine("  --remote <id>           The output being measured. Default: Windows' default,");
    Console.WriteLine("                          which is the Bluetooth link to the stereo if you set it.");
    Console.WriteLine("  --mic <id>              Input to listen on. Default: Windows' default.");
    Console.WriteLine($"  --rounds <n>            Probes per output. Default 5, minimum {LatencyCalibration.MinimumRuns}.");
    Console.WriteLine("  --local-latency-ms <n>  The tablet speaker's own output delay. Default 15.");
    Console.WriteLine("                          The one term that cannot be cancelled, so it is stated.");
    Console.WriteLine("  --remote-metres <m>     Stereo speakers to tablet. Default 1.0.");
    Console.WriteLine("  --local-metres <m>      Tablet speaker to its own microphone. Default 0.2.");
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
