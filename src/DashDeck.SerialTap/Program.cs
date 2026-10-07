using System.Globalization;
using System.IO.Ports;
using System.Net;
using DashDeck.Abstractions;
using DashDeck.Vehicle.Tap;

// DashDeck serial tap: records everything FORScan (or any program) says to the adapter, and what
// the adapter says back, by standing between them.
//
//   SerialTap                       pick the adapter's port from a list
//   SerialTap --port COM7           use that port
//   SerialTap --baud 115200         its speed (the OBDLink EX's, by default)
//   SerialTap --listen 35000        the TCP port FORScan connects to (35000 by default)
//   SerialTap --any                 accept connections from other machines, not just this one
//   SerialTap --out <folder>        where the logs go
//   SerialTap --quiet               do not echo every line to the window
//
// Then in FORScan: Settings ▸ Connection ▸ type WiFi, IP 127.0.0.1, port 35000. Connect.

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

if (Flag("--help") || Flag("-h") || Flag("/?"))
{
    Console.WriteLine("SerialTap [--port COMn] [--baud n] [--listen tcpPort] [--any] [--out folder] [--quiet]");
    return 0;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

var portName = ArgValue("--port") ?? PickPort();
if (portName is null)
{
    Pause();
    return 2;
}

var baud = int.TryParse(ArgValue("--baud"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 115200;
var tcpPort = int.TryParse(ArgValue("--listen"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 35000;
var address = Flag("--any") ? IPAddress.Any : IPAddress.Loopback;
var quiet = Flag("--quiet");

var folder = ArgValue("--out") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "tap");
Directory.CreateDirectory(folder);

var clock = SystemClock.Instance;
var logPath = Path.Combine(folder, $"tap-{clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.log");

using var serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
{
    Handshake = Handshake.None,
    DtrEnable = true,
    RtsEnable = true,
};

try
{
    serial.Open();
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
{
    Console.WriteLine($"Could not open {portName}: {ex.Message}");
    Console.WriteLine("Close DashDeck, the ID hunter and FORScan first — only one program can hold the port.");
    Pause();
    return 3;
}

using var log = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
var gate = new object();

void Write(string line)
{
    lock (gate)
    {
        log.WriteLine(line);

        if (!quiet)
        {
            Console.WriteLine(line);
        }
    }
}

var recorder = new TapRecorder(clock, Write);
var bridge = new TapBridge(serial.BaseStream, recorder, new IPEndPoint(address, tcpPort));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

bridge.Started += at =>
{
    log.WriteLine($"# SerialTap  {portName} @ {baud} baud  listening on {at}  started {clock.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
    log.WriteLine("# >> program to adapter    << adapter to program    -- notes");
    Console.WriteLine();
    Console.WriteLine($"  Holding {portName} at {baud} baud.");
    Console.WriteLine($"  In FORScan: Settings > Connection > type WiFi, IP {(address.Equals(IPAddress.Any) ? "this PC's address" : "127.0.0.1")}, port {at.Port}. Then connect.");
    Console.WriteLine($"  Recording to {logPath}");
    Console.WriteLine("  Ctrl+C to stop.");
    Console.WriteLine();
};

try
{
    await bridge.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
{
    Console.WriteLine($"Stopped: {ex.Message}");
}

recorder.Flush();
Console.WriteLine();
Console.WriteLine($"  {recorder.CommandLines} commands, {recorder.AnswerLines} answer lines " +
                  $"({recorder.BytesToAdapter} bytes out, {recorder.BytesFromAdapter} in).");
Console.WriteLine($"  Saved: {logPath}");
Pause();
return 0;

static string? PickPort()
{
    var ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();

    if (ports.Length == 0)
    {
        Console.WriteLine("No serial ports. Is the adapter plugged in?");
        return null;
    }

    if (ports.Length == 1)
    {
        Console.WriteLine($"Using {ports[0]}, the only serial port.");
        return ports[0];
    }

    Console.WriteLine("Which port is the adapter?");
    for (var i = 0; i < ports.Length; i++)
    {
        Console.WriteLine($"  {i + 1}. {ports[i]}");
    }

    Console.Write("Number: ");
    var answer = Console.ReadLine();
    return int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= ports.Length
        ? ports[n - 1]
        : null;
}

// Started by a double-click, the window would close on its last line before it could be read.
static void Pause()
{
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
}
