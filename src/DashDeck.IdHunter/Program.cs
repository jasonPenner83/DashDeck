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

            // The dash's own settings say where the adapter was, at what rate, and which port is the
            // phone's GPS — so the hunter looks the way the dash does (ADR-0034), not blind.
            var dash = DashSettings.Read(DashSettings.DefaultPath);
            if (dash.Port is not null)
            {
                Console.WriteLine($"  DashDeck last used {dash.Port}{(dash.BaudRate is { } r ? $" at {r} baud" : "")}{(dash.Identity is { } id ? $" ({id})" : "")}.");
            }

            var found = await FindAdapterAsync(port ?? dash.Port, baud ?? dash.BaudRate, dash.GpsPort, cts.Token);
            if (found is null)
            {
                Console.WriteLine();
                Console.WriteLine("No adapter answered. Check:");
                Console.WriteLine("  - DashDeck is closed (three-dot menu > CLOSE DASHDECK), and Task Manager shows no DashDeck.Host left running;");
                Console.WriteLine("  - FORScan and OBDwiz are closed;");
                Console.WriteLine("  - the USB cable is in. Unplug it, wait 10 s, plug it back in and try again.");
                Console.WriteLine("If DashDeck's Settings > Vehicle shows the adapter on a port, say so: IdHunter --port COMn");
                Console.WriteLine("To learn the guide without the truck: IdHunter --simulate");
                Console.WriteLine();
                Console.Write("Press Enter to close.");
                Console.ReadLine();
                return 1;
            }

            (port, baud) = found.Value;
        }

        session = await HuntSession.OpenAsync(new SerialPortTransport(port, baud.Value) { ResponseTimeout = TimeSpan.FromSeconds(2) }, cts.Token);
    }
}
catch (IOException ex)
{
    Console.WriteLine(ex.Message);
    Console.Write("Press Enter to close.");
    Console.ReadLine();
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

// Find the adapter the way the dash does (ADR-0034) — its own handshake and its full list of rates —
// starting with the port and rate the dash last used, then every other port, never the phone's GPS.
static async Task<(string Port, int Baud)?> FindAdapterAsync(string? preferred, int? knownBaud, string? gpsPort, CancellationToken ct)
{
    var ports = SerialPortTransport.AvailablePorts();
    if (ports.Count == 0)
    {
        Console.WriteLine("  Windows lists no serial (COM) ports at all. Is the adapter's USB cable in?");
        return null;
    }

    var order = (preferred is null ? [] : new[] { preferred })
        .Concat(ports.Where(p => !p.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    foreach (var candidate in order)
    {
        if (gpsPort is not null && candidate.Equals(gpsPort, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {candidate,-6} skipped — DashDeck's phone GPS port");
            continue;
        }

        Console.Write($"  {candidate,-6} testing… ");

        var link = AdapterLinkTransport.ForSerialPorts(new AdapterLinkOptions
        {
            PreferredPort = candidate,
            KnownBaudRate = knownBaud,
            Relocate = false,
        });

        try
        {
            if (await link.TryLocateAsync(ct, relocate: false) is { } location)
            {
                Console.WriteLine($"{location.Identity} at {location.BaudRate} baud");
                return (location.Port, location.BaudRate);
            }

            Console.WriteLine(link.LastProblem ?? "no answer");
        }
        finally
        {
            await link.DisposeAsync();
        }
    }

    return null;
}

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
