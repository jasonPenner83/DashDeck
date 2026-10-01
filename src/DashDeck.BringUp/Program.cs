using System.Diagnostics;
using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.BringUp;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Recording;

// DashDeck bring-up (P1.5).
//
// Runs in two stages, because they fail for different reasons and are worth separating:
//
//   Stage 1  the adapter itself — driver, COM port, baud, command protocol, identity,
//            MS-CAN capability. Works with the adapter on a desk, powered from USB, with
//            no vehicle attached at all.
//   Stage 2  the vehicle — which PIDs it answers, the real throughput ceiling, live data,
//            and a replayable capture. Needs the truck, ignition on.
//
// Stage 2 being skipped is a normal outcome, not a failure.

var portArg = ArgValue("--port");
var baud = int.Parse(ArgValue("--baud") ?? SerialPortTransport.DefaultBaudRate.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
var simulate = Flag("--simulate");
var benchSim = Flag("--simulate-bench");
var outDir = ArgValue("--out") ?? "captures";

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = cts.Token;

Console.WriteLine("DashDeck bring-up");
Console.WriteLine();

var catalog = SignalCatalog.FromFile(FindCatalog());
Console.WriteLine($"  catalog    {catalog.Count} signals");

// ---------------------------------------------------------------- choose a transport
IVehicleTransport transport;
string portName;

if (simulate || benchSim)
{
    var faults = benchSim ? SyntheticFaults.BenchNoVehicle : SyntheticFaults.Realistic;
    transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: faults);
    portName = benchSim ? "simulated (bench, no vehicle)" : "simulated";
    Console.WriteLine($"  transport  {portName}");
}
else
{
    portName = portArg ?? await FindAdapterPortAsync(baud, ct);
    transport = new SerialPortTransport(portName, baud);
    Console.WriteLine($"  transport  {portName} @ {baud} baud");
}

Directory.CreateDirectory(outDir);
var stamp = DateTimeOffset.UtcNow;
var capturePath = Path.Combine(outDir, $"bringup-{stamp:yyyyMMdd-HHmmss}.jsonl");
var recording = new RecordingTransport(transport, capturePath);

Console.WriteLine();

// ----------------------------------------------------------- stage 1: the adapter
Console.WriteLine("Stage 1 — adapter");
Console.WriteLine();

AdapterProbeReport probe;
try
{
    probe = await AdapterProbe.RunAsync(recording, portName, baud, ct);
}
catch (IOException ex)
{
    Console.Error.WriteLine($"  could not reach the adapter: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Ports the OS can see:");

    foreach (var p in SerialPortTransport.AvailablePorts())
    {
        Console.Error.WriteLine($"    {p}");
    }

    return 1;
}

foreach (var step in probe.Steps)
{
    Console.WriteLine($"  {(step.Ok ? "ok  " : "--  ")}{step.Command,-6} {Trim(step.Response, 44),-46}{step.Note}");
}

Console.WriteLine();
Console.WriteLine($"  identity        {probe.Identity ?? "no answer"}");
Console.WriteLine($"  device          {probe.DeviceDescription ?? "—"}");
Console.WriteLine($"  STN firmware    {probe.StnFirmware ?? "not an STN adapter"}");
Console.WriteLine($"  ST commands     {(probe.SupportsStCommands ? "yes" : "NO")}");
Console.WriteLine($"  MS-CAN switch   {(probe.MsCanSwitchAccepted ? "accepted" : "NOT accepted")}");
Console.WriteLine($"  OBD voltage     {probe.ObdVoltage ?? "—"}");
Console.WriteLine($"  vehicle         {probe.Vehicle}");
Console.WriteLine();

if (!probe.AdapterHealthy)
{
    Console.WriteLine("  The adapter did not identify itself. Nothing below will work until it does.");
    Console.WriteLine("  Check: FTDI driver installed (red LED means not), correct COM port, and that");
    Console.WriteLine("  FORScan or OBDwiz is not holding the port open.");
    await WriteReportAsync(new BringUpResult { StartedUtc = stamp, Adapter = probe }, catalog, outDir, stamp);
    return 1;
}

if (!probe.SupportsStCommands)
{
    Console.WriteLine("  WARNING: the ST command set did not answer. A genuine OBDLink answers STI.");
    Console.WriteLine("  Without it there is no MS-CAN access, so TPMS, door state and drivetrain");
    Console.WriteLine("  mode are unreachable (ADR-0007).");
    Console.WriteLine();
}

// ----------------------------------------------------------- stage 2: the vehicle
if (probe.Vehicle != VehiclePresence.Present)
{
    Console.WriteLine("Stage 2 — vehicle: SKIPPED, no vehicle bus found.");
    Console.WriteLine();
    Console.WriteLine("  That is the expected result with the adapter on a desk. The adapter side is");
    Console.WriteLine("  now cleared, so the only unknowns left are genuinely vehicle-side.");
    Console.WriteLine();
    Console.WriteLine("  In the truck: ignition on, engine running, parked. Then run this again.");

    await recording.DisposeAsync();
    var bench = new BringUpResult { StartedUtc = stamp, Adapter = probe, CapturePath = capturePath };
    await WriteReportAsync(bench, catalog, outDir, stamp);
    return 0;
}

Console.WriteLine("Stage 2 — vehicle");
Console.WriteLine();

var adapter = new ElmAdapter(recording);
await adapter.InitializeAsync(ct);

var hsSupport = await PidSupportScanner.ScanAsync(adapter, CanBus.Hs, ct);
Console.WriteLine($"  HS-CAN supports {hsSupport.DataPids.Count} data PIDs across " +
                  $"{hsSupport.RangesProbed.Count} range(s)");
Console.WriteLine($"    {string.Join(" ", hsSupport.DataPids.Select(p => $"0x{p:X2}"))}");
Console.WriteLine();

PidSupportReport? msSupport = null;
if (probe.MsCanSwitchAccepted)
{
    msSupport = await PidSupportScanner.ScanAsync(adapter, CanBus.Ms, ct);
    Console.WriteLine($"  MS-CAN standard bitmap: {(msSupport.AnyResponse ? "answered" : "no answer")}");
    Console.WriteLine("    Ford's body PIDs are manufacturer-specific and are not advertised here;");
    Console.WriteLine("    no answer is the expected result and does not mean MS-CAN is unusable.");
    Console.WriteLine();
}

// Which catalog signals can this truck actually feed?
var coverage = new List<(string, bool, string)>();

foreach (var definition in catalog.Definitions.OrderBy(d => d.Id, StringComparer.Ordinal))
{
    var spec = definition.ToRequest();

    if (spec.Bus == CanBus.Ms)
    {
        coverage.Add((definition.Id, probe.MsCanSwitchAccepted,
            probe.MsCanSwitchAccepted ? "MS-CAN reachable; PID needs live confirmation" : "no MS-CAN access"));
        continue;
    }

    var supported = hsSupport.Supports((ushort)spec.Pid);
    coverage.Add((definition.Id, supported, supported ? string.Empty : $"vehicle does not list 0x{spec.Pid:X2}"));
}

Console.WriteLine("  Catalog coverage:");
foreach (var (id, supported, note) in coverage)
{
    Console.WriteLine($"    {(supported ? "ok  " : "--  ")}{id,-30}{note}");
}

Console.WriteLine();

// Measure the real ceiling against a PID we know answers.
var probePid = hsSupport.Supports(0x0D) ? (ushort)0x0D : hsSupport.SupportedPids.OrderBy(p => p).First();
var throughput = await MeasureThroughputAsync(adapter, probePid, 60, ct);

Console.WriteLine($"  Throughput over {throughput.Requests} requests of 0x{probePid:X2}:");
Console.WriteLine($"    mean {throughput.MeanMs:0.#} ms, min {throughput.MinMs:0.#} ms, " +
                  $"p95 {throughput.P95Ms:0.#} ms, {throughput.Failures} failed");
Console.WriteLine($"    sustainable ceiling ~{throughput.CeilingHz:0.#} requests/sec  <- answers Q12");
Console.WriteLine();

// Finally, run the real pipeline for a few seconds and read values off the bus.
await using var service = new VehicleService(adapter, catalog);
await service.StartAsync(ct);

var wanted = new[] { "vehicle.speed", "engine.rpm", "engine.coolantTemp", "fuel.levelPercent" }
    .Where(id => catalog.Ids.Contains(id))
    .ToArray();

var declarations = wanted
    .Select(id => service.Bus.Require(id, SignalPriority.High, 2))
    .ToList();

Console.WriteLine("  Live data for 8 seconds...");
await Task.Delay(TimeSpan.FromSeconds(8), ct);

var live = new List<(string, double, string)>();
foreach (var id in wanted)
{
    var value = service.Bus.Current(id);
    Console.WriteLine($"    {id,-24}{(value.IsUsable ? $"{value.Value:0.##} {value.Unit}" : "no reading")}");

    if (value.IsUsable)
    {
        live.Add((id, value.Value, value.Unit));
    }
}

foreach (var declaration in declarations)
{
    declaration.Dispose();
}

var result = new BringUpResult
{
    StartedUtc = stamp,
    Adapter = probe,
    HsSupport = hsSupport,
    MsSupport = msSupport,
    Throughput = throughput,
    CatalogCoverage = coverage,
    LiveSample = live,
    CapturePath = capturePath,
};

await WriteReportAsync(result, catalog, outDir, stamp);
Console.WriteLine();
Console.WriteLine($"  capture  {capturePath}");
return 0;

// ------------------------------------------------------------------------- helpers

static async Task<ThroughputResult> MeasureThroughputAsync(
    IVehicleAdapter adapter, ushort pid, int count, CancellationToken ct)
{
    var timings = new List<double>(count);
    var failures = 0;

    for (var i = 0; i < count; i++)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await adapter.RequestAsync(new PidRequest(0x01, pid, CanBus.Hs), ct);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (response.IsSuccess)
        {
            timings.Add(elapsed);
        }
        else
        {
            failures++;
        }
    }

    if (timings.Count == 0)
    {
        return new ThroughputResult(count, failures, 0, 0, 0);
    }

    timings.Sort();
    var p95Index = Math.Min(timings.Count - 1, (int)Math.Ceiling(timings.Count * 0.95) - 1);

    return new ThroughputResult(
        count, failures, timings.Average(), timings[0], timings[Math.Max(p95Index, 0)]);
}

static async Task<string> FindAdapterPortAsync(int baud, CancellationToken ct)
{
    var ports = SerialPortTransport.AvailablePorts();

    if (ports.Count == 0)
    {
        throw new IOException(
            "No serial ports found. Plug the adapter in and check the FTDI driver installed.");
    }

    Console.WriteLine($"  scanning    {string.Join(", ", ports)}");

    foreach (var port in ports)
    {
        // Short timeout: this opens ports that may belong to other devices, so linger as
        // little as possible on anything that is not our adapter.
        await using var candidate = new SerialPortTransport(port, baud)
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(1200),
        };

        try
        {
            await candidate.ConnectAsync(ct);
            await candidate.ExchangeAsync("ATZ", ct);
            var identity = await candidate.ExchangeAsync("ATI", ct);

            if (identity.Contains("ELM", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("STN", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("OBD", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  found       {port} — {identity.Replace("\r", " ").Trim()}");
                return port;
            }
        }
        catch (IOException)
        {
            // Not our adapter, or busy. Move on.
        }
    }

    throw new IOException(
        $"None of {string.Join(", ", ports)} answered like an OBD adapter. " +
        "Pass --port explicitly if you know which it is.");
}

static async Task WriteReportAsync(
    BringUpResult result, SignalCatalog catalog, string outDir, DateTimeOffset stamp)
{
    var path = Path.Combine(outDir, $"bringup-{stamp:yyyyMMdd-HHmmss}.md");
    await File.WriteAllTextAsync(path, BringUpReportWriter.ToMarkdown(result, catalog));
    Console.WriteLine($"  report   {path}");
}

static string Trim(string value, int max)
{
    var flat = value.Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Trim();

    return flat.Length <= max ? flat : flat[..max] + "…";
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool Flag(string name) => Array.IndexOf(args, name) >= 0;

static string FindCatalog()
{
    var dir = AppContext.BaseDirectory;

    for (var i = 0; i < 10 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "catalog", "signals.obd2-standard.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }

    throw new FileNotFoundException("Could not locate the signal catalog. Run from inside the repository.");
}
