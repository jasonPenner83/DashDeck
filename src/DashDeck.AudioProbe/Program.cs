using System.Globalization;
using DashDeck.Core.Audio;
using DashDeck.WinAudio;

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

    var request = new MeasurementRequest(
        localId.Value,
        remoteId.Value,
        micId,
        rounds,
        TimeSpan.FromMilliseconds(IntArg("--max-delay-ms") ?? 1000),
        TimeSpan.FromSeconds(IntArg("--lead-in-s") ?? 3),
        localLatency,
        remoteMetres,
        localMetres);

    Console.WriteLine($"Probe       100 ms sweep, {LatencyProbe.StartHz:F0}-{LatencyProbe.EndHz:F0} Hz, {LatencyProbe.SampleRate} Hz mono");
    Console.WriteLine("            up for the reference, down for the measured output, so neither");
    Console.WriteLine("            can be found in the other's window");
    Console.WriteLine("Track       one continuous stream per output — the probes are embedded in it,");
    Console.WriteLine("            so the Bluetooth link is never suspended and the stereo sees one");
    Console.WriteLine("            play rather than one per probe");
    Console.WriteLine($"Lead-in     {request.Lead.TotalSeconds:F0} s of carrier before the first probe (--lead-in-s)");
    Console.WriteLine($"Window      searching {request.Window.TotalMilliseconds:F0} ms past each probe (--max-delay-ms)");
    Console.WriteLine($"Rounds      {rounds} to each output");
    Console.WriteLine($"Reference   device {localId} (the tablet's own speaker), assumed {localLatency.TotalMilliseconds:F0} ms of output delay");
    Console.WriteLine($"Measuring   device {remoteId.Value}");
    Console.WriteLine($"Microphone  device {(micId == WaveDevices.Mapper ? "default" : micId.ToString(CultureInfo.InvariantCulture))}");
    Console.WriteLine();
    Console.WriteLine("Keep the cab as it will be in use — engine running if that is the case being");
    Console.WriteLine($"measured. Do not talk over it. This takes about {request.Duration.TotalSeconds:F0} seconds.");
    Console.WriteLine();
    Console.WriteLine("The stereo will show one track playing for the duration. That is expected: it is");
    Console.WriteLine("what stops it suspending the link between probes.");
    Console.WriteLine();

    var outcome = DelayMeasurement.Run(request, line => Console.Write($"\r  {line,-28}"));
    Console.WriteLine();
    Console.WriteLine();

    if (outcome.Gaps > 0)
    {
        Console.WriteLine($"  WARNING: {outcome.Gaps} capture buffer(s) came back short — "
            + $"{outcome.GapTime.TotalMilliseconds:F0} ms of this recording is padding.");
        Console.WriteLine("  The timeline is preserved, but any probe that fell in a gap is missing and");
        Console.WriteLine("  any reading here is suspect. Close other audio software and run it again.");
        Console.WriteLine();
    }

    foreach (var (isRemote, match) in outcome.Session.Probes)
    {
        Console.WriteLine(match.Found
            ? $"  {(isRemote ? "stereo " : "tablet ")} {match.Lag.TotalMilliseconds,7:F1} ms   confidence {match.Confidence,6:F1}"
            : $"  {(isRemote ? "stereo " : "tablet ")}       -        not found  (confidence {match.Confidence:F1})");
    }

    Console.WriteLine();
    Console.WriteLine($"Heard from the tablet   {outcome.Session.Local.Lag.TotalMilliseconds,7:F1} ms   "
        + $"spread {outcome.Session.Local.Spread.TotalMilliseconds:F1} ms over {outcome.Session.Local.Accepted} runs");
    Console.WriteLine($"Heard from the stereo   {outcome.Session.Remote.Lag.TotalMilliseconds,7:F1} ms   "
        + $"spread {outcome.Session.Remote.Spread.TotalMilliseconds:F1} ms over {outcome.Session.Remote.Accepted} runs");
    Console.WriteLine();

    if (!outcome.Ok)
    {
        Console.WriteLine("REFUSED, rather than reporting a number that would be fiction:");
        Console.WriteLine();
        Console.WriteLine($"  {outcome.Problem}");
        return 3;
    }

    Console.WriteLine($"==> Output delay: {outcome.Delay!.Value.TotalMilliseconds:F0} ms");
    Console.WriteLine();
    Console.WriteLine("Type that into Settings ▸ AUDIO as the delay for this output, or press MEASURE");
    Console.WriteLine("there to have it filled in for you.");
    Console.WriteLine();
    Console.WriteLine($"Spread was {outcome.Session.Remote.Spread.TotalMilliseconds:F1} ms. With a continuous track every");
    Console.WriteLine("probe shares one playback start, so more than a few ms means something is wrong");
    Console.WriteLine("rather than merely jittery. It will also move after a reconnect and after the");
    Console.WriteLine("stream has been idle, because the delay follows the receiving end's buffer.");

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
    Console.WriteLine("  --max-delay-ms <n>      How far past each probe to search. Default 1000.");
    Console.WriteLine("                          A delay beyond it misses its own window and reads as");
    Console.WriteLine("                          not found, so raise it rather than lower it.");
    Console.WriteLine("  --lead-in-s <n>         Carrier before the first probe, so the stream's one");
    Console.WriteLine("                          cold start is paid for by nothing. Default 3.");
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
