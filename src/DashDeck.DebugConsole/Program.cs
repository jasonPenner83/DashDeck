using System.Diagnostics;
using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Simulator;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Recording;

// DashDeck debug console — the P0 harness.
//
// The WPF shell does not exist yet, and this is not a stand-in for it. It exists so the
// whole stack below the UI can be run, watched and recorded on any machine, including one
// with no truck, no adapter and no Windows. Everything it drives is the real pipeline:
// synthetic transport -> ELM adapter -> catalog -> arbiter -> state bus.

var drive = Drives.ByName(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "cold-start-city");
var seconds = ArgValue("--seconds") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : 30;
var recordPath = ArgValue("--record");
var replayPath = ArgValue("--replay");

var catalogPath = FindCatalog();
var catalog = SignalCatalog.FromFile(catalogPath);

Console.WriteLine($"DashDeck P0 harness");
Console.WriteLine($"  catalog   {Path.GetFileName(catalogPath)} ({catalog.Count} signals)");

DashDeck.Vehicle.IVehicleTransport transport;
SyntheticTransport? synthetic = null;

if (replayPath is not null)
{
    transport = new ReplayTransport(replayPath);
    Console.WriteLine($"  source    replay of {Path.GetFileName(replayPath)}");
}
else
{
    synthetic = new SyntheticTransport(new SimulatedF150(drive));
    transport = synthetic;
    Console.WriteLine($"  source    {drive.Name} — {drive.Description}");
}

if (recordPath is not null)
{
    transport = new RecordingTransport(transport, recordPath);
    Console.WriteLine($"  recording {recordPath}");
}

var adapter = new ElmAdapter(transport);
await using var service = new VehicleService(adapter, catalog)
{
    // Mock data says so, always. This is what stops a simulated number ever being
    // mistaken for a real one on screen.
    Quality = replayPath is null ? SignalQuality.Simulated : SignalQuality.Live,

    // Placeholders ask nothing (ADR-0052); simulated, they read the synthetic truck by name.
    SimulatedValues = synthetic is null ? null : synthetic.Truck.Reading,
};

using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 5));
await service.StartAsync(stopping.Token);

var caps = adapter.Capabilities!;
Console.WriteLine($"  adapter   {caps.Name}");
Console.WriteLine($"  buses     {string.Join(", ", caps.Buses)}   simultaneous: {caps.SimultaneousBusAccess}");
Console.WriteLine($"  budget    {caps.MaxRequestsPerSecond:0.#} requests/sec (shared by everything)");
Console.WriteLine();

// Declare demand the way a component would: by name, with a priority and a rate. Nothing
// here knows a PID exists.
var declarations = new List<ISignalSubscription>
{
    service.Bus.Require("vehicle.speed", SignalPriority.High, 4),
    service.Bus.Require("engine.rpm", SignalPriority.High, 4),
    service.Bus.Require("engine.fuelRate", SignalPriority.Normal, 2),
    service.Bus.Require("engine.coolantTemp", SignalPriority.Normal, 0.5),
    service.Bus.Require("fuel.levelPercent", SignalPriority.Low, 0.2),
    service.Bus.Require("ambient.airTemp", SignalPriority.Low, 0.1),
    service.Bus.Require("engine.load", SignalPriority.Normal, 2),
};

var plan = service.Arbiter.CurrentPlan;
Console.WriteLine($"Polling plan — demand {plan.DemandHz:0.#} Hz, budget {plan.BudgetHz:0.#} Hz" +
                  (plan.IsDegraded ? "  [DEGRADED]" : string.Empty));

foreach (var entry in plan.Entries.OrderByDescending(e => e.RateHz))
{
    var declared = declarations.First(d => d.SignalId == entry.Signal.Id);
    var note = entry.RateHz < declared.RequestedRateHz - 1e-6
        ? $"  <- asked {declared.RequestedRateHz:0.##}, degraded"
        : string.Empty;
    Console.WriteLine($"  {entry.Signal.Id,-26} {entry.Priority,-10} {entry.RateHz,6:0.##} Hz{note}");
}

Console.WriteLine();
Console.WriteLine("Live signals (Ctrl-C to stop):");
Console.WriteLine();

var watch = Stopwatch.StartNew();
var lastRender = TimeSpan.Zero;

while (watch.Elapsed.TotalSeconds < seconds && !stopping.IsCancellationRequested)
{
    await Task.Delay(100, CancellationToken.None);

    if (watch.Elapsed - lastRender < TimeSpan.FromMilliseconds(500))
    {
        continue;
    }

    lastRender = watch.Elapsed;
    Render(service, synthetic, watch.Elapsed);
}

Console.WriteLine();
Console.WriteLine($"Ran {watch.Elapsed.TotalSeconds:0.#}s.");

if (synthetic is not null)
{
    var truck = synthetic.Truck;
    Console.WriteLine($"  requests served   {synthetic.RequestCount} " +
                      $"({synthetic.RequestCount / watch.Elapsed.TotalSeconds:0.#}/sec), " +
                      $"{synthetic.DroppedCount} dropped");
    Console.WriteLine($"  measured ceiling  {service.MeasuredRequestsPerSecond:0.#} req/sec " +
                      $"(adapter claimed {caps.MaxRequestsPerSecond:0.#})");
    Console.WriteLine($"  final budget      {service.Arbiter.BudgetHz:0.#} Hz, " +
                      $"allocated {service.Arbiter.CurrentPlan.AllocatedHz:0.#} Hz of " +
                      $"{service.Arbiter.CurrentPlan.DemandHz:0.#} Hz demanded");
    Console.WriteLine($"  ground truth      {truck.DistanceKm:0.###} km, " +
                      $"{truck.FuelUsedLitres:0.####} L consumed");
    Console.WriteLine("                    (the exact figure the trip computer must reproduce in P1)");
}

foreach (var declaration in declarations)
{
    declaration.Dispose();
}

static void Render(VehicleService service, SyntheticTransport? synthetic, TimeSpan elapsed)
{
    var ids = new[]
    {
        "vehicle.speed", "engine.rpm", "engine.coolantTemp",
        "engine.fuelRate", "engine.load", "fuel.levelPercent",
    };

    var parts = ids.Select(id =>
    {
        var v = service.Bus.Current(id);
        var name = id.Split('.')[^1];

        if (!v.IsUsable)
        {
            return $"{name}=--";
        }

        var flag = v.Quality == SignalQuality.Stale ? "~" : string.Empty;
        return $"{name}={flag}{v.Value:0.#}{v.Unit}";
    });

    var segment = synthetic is not null ? $"  [{synthetic.Truck.CurrentSegment}]" : string.Empty;
    Console.WriteLine($"  {elapsed.TotalSeconds,5:0.0}s  {string.Join("  ", parts)}{segment}");
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindCatalog()
{
    var dir = AppContext.BaseDirectory;

    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "catalog", "signals.obd2-standard.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }

    throw new FileNotFoundException(
        "Could not find catalog/signals.obd2-standard.json by walking up from the binary. " +
        "Run from inside the repository.");
}
