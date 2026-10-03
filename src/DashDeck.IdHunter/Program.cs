using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.IdHunter;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;

// DashDeck ID hunter (ADR-0044): a guided, read-only hunt for where the truck keeps a value.
//
//   IdHunter                     find the adapter and start the guide
//   IdHunter --port COM5         use that port
//   IdHunter --simulate          the synthetic truck, to learn the guide at a desk
//   IdHunter --out <folder>      where findings.csv and the captures go
//   IdHunter --targets <file>    another checklist

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

if (Flag("--help") || Flag("-h") || Flag("/?"))
{
    Console.WriteLine("IdHunter [--port COMn] [--baud n] [--simulate] [--out folder] [--targets file]");
    return 0;
}

// The guide draws dashes, arrows and degree signs; the Windows console shows them only in UTF-8.
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var clock = SystemClock.Instance;
var io = new SystemConsole();

var targetsPath = ArgValue("--targets") ?? Path.Combine(AppContext.BaseDirectory, "targets.json");
if (!File.Exists(targetsPath))
{
    Console.WriteLine($"No checklist at {targetsPath}.");
    return 2;
}

var (targets, problems) = HuntTargetFile.Load(targetsPath);
foreach (var problem in problems)
{
    Console.WriteLine($"targets.json: {problem}");
}

var packs = VehiclePacks.LoadFolder(FindCatalogFolder("vehicles")).Packs;

var folder = ArgValue("--out") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DashDeck", "hunt", "hunt-" + clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
var output = new HuntOutput(folder, clock);

HuntSession session;
try
{
    if (Flag("--simulate"))
    {
        session = await HuntSession.SimulateAsync(cts.Token);
    }
    else
    {
        var port = ArgValue("--port");
        var baud = int.TryParse(ArgValue("--baud"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (int?)null;

        if (port is null || baud is null)
        {
            Console.WriteLine("Looking for the adapter… (close DashDeck and FORScan first: only one program can hold it)");
            var found = new List<PortTestResult>();
            foreach (var candidate in port is null ? SerialPortTransport.AvailablePorts() : [port])
            {
                var result = await PortTester.TestAsync(candidate, (p, r) => new SerialPortTransport(p, r), baud, cts.Token);
                Console.WriteLine($"  {result.Port,-6} {result.Outcome,-11} {result.Identity ?? result.Detail} {result.Voltage}");
                if (result.Outcome == PortTestOutcome.Adapter)
                {
                    found.Add(result);
                }
            }

            if (found.Count == 0)
            {
                Console.WriteLine("No adapter answered. Is it plugged in, and is DashDeck closed (three-dot menu ▸ CLOSE DASHDECK)?");
                Console.WriteLine("To learn the guide without the truck: IdHunter --simulate");
                return 1;
            }

            var chosen = found[0];
            if (found.Count > 1)
            {
                Console.Write($"More than one adapter. Which port [{chosen.Port}]: ");
                var typed = Console.ReadLine()?.Trim();
                chosen = found.FirstOrDefault(f => f.Port.Equals(typed, StringComparison.OrdinalIgnoreCase)) ?? chosen;
            }

            port = chosen.Port;
            baud = chosen.BaudRate ?? SerialPortTransport.DefaultBaudRate;
        }

        session = await HuntSession.OpenAsync(new SerialPortTransport(port, baud.Value) { ResponseTimeout = TimeSpan.FromSeconds(2) }, cts.Token);
    }
}
catch (IOException ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}

await using (session)
{
    await new Wizard(io, session, targets, output, packs, clock).RunAsync(cts.Token);
}

return 0;

static string? FindCatalogFolder(string name)
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "catalog", name);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }

    return null;
}
