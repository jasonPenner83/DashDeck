using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using DashDeck.Core.Location;

// GpsProbe — proves the phone's GPS is actually reaching the tablet (ADR-0027, ADR-0028).
//
// The dash reads NMEA from a GPS-share app on the phone, over a Bluetooth virtual COM port or
// over TCP. Both are one-setting affairs once they work, and both are opaque when they do not:
// Windows turns one pairing into SEVERAL COM ports, only one of which the phone answers on, and
// picking the wrong one looks exactly like a phone that is not sharing. The dash cannot tell you
// which is which from the launcher, because the setting is applied at the next launch.
//
// So this opens the port, listens, and says which of the five things is true:
//
//   nothing arrived            the wrong port, or the app is not sharing
//   bytes, but not NMEA        the wrong port — something else is on it
//   NMEA, but no RMC or GGA    the app's sentence selection; turn RMC on
//   RMC/GGA, no position yet    the link works; the receiver has not seen the sky
//   a position                  this is the port to put in Settings
//
// Nothing here writes to the vehicle, installs anything, or touches the registry. It only reads.

try
{
    return Run();
}
catch (PlatformNotSupportedException)
{
    Console.Error.WriteLine("Serial ports are not available on this platform.");
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
        ListPorts();
        return 0;
    }

    var seconds = IntArg("--seconds") ?? 15;

    if (seconds is < 1 or > 600)
    {
        Console.Error.WriteLine("--seconds must be between 1 and 600.");
        return 1;
    }

    var tcp = StringArg("--tcp");
    var port = StringArg("--port");

    if (tcp is not null && port is not null)
    {
        Console.Error.WriteLine("Give --port or --tcp, not both — they are the two transports.");
        return 1;
    }

    if (tcp is not null)
    {
        return Listen($"tcp {tcp}", () => OpenTcp(tcp), seconds, bluetooth: false);
    }

    if (port is not null)
    {
        return Listen(port, () => OpenSerial(port), seconds, bluetooth: true);
    }

    Console.Error.WriteLine("Nothing to listen to. Give --port <COMn> for Bluetooth, or --tcp <host:port>.");
    Console.Error.WriteLine();
    Usage();
    return 1;
}

// ── the listen ───────────────────────────────────────────────────────────────────────────────

int Listen(string what, Func<TextReader> open, int seconds, bool bluetooth)
{
    Console.WriteLine($"Listening to {what} for {seconds} s. Keep the phone's GPS app in the foreground.");
    Console.WriteLine();

    TextReader reader;

    try
    {
        reader = open();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not open {what}: {ex.Message}");
        Console.Error.WriteLine();

        if (bluetooth)
        {
            Console.Error.WriteLine("The phone must be paired and its GPS app sharing over Bluetooth. Windows makes");
            Console.Error.WriteLine("several ports per pairing and only one of them answers, so try each in turn —");
            Console.Error.WriteLine("--list shows them. A port already open in another program also refuses.");
        }
        else
        {
            Console.Error.WriteLine("Nothing is listening there. Check the phone and the tablet are on the same");
            Console.Error.WriteLine("network (or the USB tether is up), and that the app says which host and port");
            Console.Error.WriteLine("it is serving on — the number differs between apps.");
        }

        return 2;
    }

    var tally = new NmeaTally();
    var raw = Flag("--raw");
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);

    using (reader)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            string? line;

            try
            {
                line = reader.ReadLine();
            }
            catch (TimeoutException)
            {
                // No sentence this window. Expected: a GPS sends about one burst a second.
                continue;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.Error.WriteLine($"The link dropped after {tally.Lines} lines: {ex.Message}");
                break;
            }

            if (line is null)
            {
                // End of stream — the phone closed it.
                break;
            }

            tally.Add(line, DateTimeOffset.UtcNow);

            if (raw)
            {
                Console.WriteLine(line.TrimEnd());
            }
            else if (tally.Lines % 10 == 0)
            {
                Console.Write('.');
            }
        }
    }

    if (!raw)
    {
        Console.WriteLine();
    }

    Console.WriteLine();
    Report(tally, what, bluetooth);

    return tally.Verdict is NmeaVerdict.Fix ? 0 : 3;
}

void Report(NmeaTally tally, string what, bool bluetooth)
{
    Console.WriteLine($"  lines          {tally.Lines}");
    Console.WriteLine($"  RMC / GGA      {tally.Rmc} / {tally.Gga}");
    Console.WriteLine($"  other          {tally.OtherSentences}{(tally.UnreadTypes.Count > 0 ? $" ({string.Join(", ", tally.UnreadTypes)})" : "")}");
    Console.WriteLine($"  rejected       {tally.Rejected}");
    Console.WriteLine($"  not NMEA       {tally.Junk}");
    Console.WriteLine();
    Console.WriteLine($"==> {tally.Describe()}");
    Console.WriteLine();

    switch (tally.Verdict)
    {
        case NmeaVerdict.Fix when bluetooth:
            Console.WriteLine("This is the one. Settings > Display > Location: USE PHONE GPS ON, VIA BLUETOOTH,");
            Console.WriteLine($"then pick {what} in PHONE COM PORT. It is applied at the next launch, so close");
            Console.WriteLine("DashDeck and open it again.");
            break;

        case NmeaVerdict.Fix:
            Console.WriteLine("This is the one. Settings > Display > Location: USE PHONE GPS ON, VIA NETWORK,");
            Console.WriteLine($"then put {what[4..]} in PHONE APP HOST:PORT. It is applied at the next launch,");
            Console.WriteLine("so close DashDeck and open it again.");
            break;

        case NmeaVerdict.NoFixYet:
            Console.WriteLine("The link is right and the phone has no position. Under a roof that is normal —");
            Console.WriteLine("take it outside or to a window and run this again. This port is still the answer.");
            break;

        case NmeaVerdict.WrongSentences:
            Console.WriteLine("The link is right and the app is sending sentence types the dash does not read.");
            Console.WriteLine("Turn on RMC (position, speed and course) in the app's sentence selection; GGA");
            Console.WriteLine("also works but carries no speed or course, so the compass stays on the tablet.");
            break;

        case NmeaVerdict.NotNmea when tally.Rejected > 0:
            Console.WriteLine("Sentences are arriving damaged and none of them decode. That is a garbled link");
            Console.WriteLine("rather than a missing one — out of Bluetooth range, or a port that is carrying");
            Console.WriteLine("something at a different speed. Move the phone closer and try again.");
            break;

        case NmeaVerdict.NotNmea:
            Console.WriteLine("Something is on this port and it is not a GPS. Almost certainly the wrong port —");
            Console.WriteLine("the OBD adapter is the usual culprit. Try the other ports from --list.");
            break;

        default:
            Console.WriteLine("Nothing came past at all. In order of likelihood: the wrong one of the several");
            Console.WriteLine("ports a pairing makes; the phone's GPS app not actually sharing (open it and");
            Console.WriteLine("check it says connected); the pairing dropped. --list shows what Windows has.");
            break;
    }

    if (tally.Rejected > 0 && tally.Parsed > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{tally.Rejected} sentences would not decode. A few is a noisy link and is fine; a lot");
        Console.WriteLine("alongside working ones usually means the phone is at the edge of Bluetooth range.");
    }
}

// ── the two transports ───────────────────────────────────────────────────────────────────────

TextReader OpenSerial(string name)
{
    // Bluetooth SPP ignores the baud rate — the link runs at its own speed — but SerialPort needs
    // a value, and 9600 is the classic NMEA rate. The same value the dash opens it with.
    var serial = new SerialPort(name, 9600) { NewLine = "\n", ReadTimeout = 2000 };
    serial.Open();
    return new SerialPortReader(serial);
}

TextReader OpenTcp(string endpoint)
{
    // host:port, both of them — the port a phone app serves NMEA on varies between apps, and
    // guessing one would produce a connection failure that reads like the app being off.
    var parts = endpoint.Split(':', 2);

    if (parts.Length != 2 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var tcpPort))
    {
        throw new ArgumentException($"--tcp wants host:port, e.g. 192.168.1.7:11123 — got \"{endpoint}\".");
    }

    var host = parts[0];
    var client = new TcpClient();
    client.Connect(host, tcpPort);
    client.ReceiveTimeout = 2000;

    return new StreamReader(client.GetStream(), Encoding.ASCII);
}

void ListPorts()
{
    var ports = SerialPort.GetPortNames()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p.Length)
        .ThenBy(p => p, StringComparer.Ordinal)
        .ToArray();

    if (ports.Length == 0)
    {
        Console.WriteLine("Windows has no COM ports. Pair the phone first (Bluetooth settings > Add device),");
        Console.WriteLine("and make sure its GPS app is sharing over Bluetooth rather than only over USB.");
        return;
    }

    Console.WriteLine("COM ports Windows knows:");
    Console.WriteLine();

    foreach (var p in ports)
    {
        Console.WriteLine($"  {p}");
    }

    Console.WriteLine();
    Console.WriteLine("Windows names these by number only, so which is the phone cannot be read off the list.");
    Console.WriteLine("One pairing usually makes two or more, and the OBD adapter is in here too. Try them:");
    Console.WriteLine();
    Console.WriteLine($"  GpsProbe --port {ports[0]} --seconds 15");
    Console.WriteLine();
    Console.WriteLine("The one that answers with a position is the one for Settings. Do not leave the adapter's");
    Console.WriteLine("port open for long while the dash is running — one program at a time per port.");
}

// ── arguments ────────────────────────────────────────────────────────────────────────────────

void Usage()
{
    Console.WriteLine("GpsProbe — is the phone's GPS reaching the tablet?");
    Console.WriteLine();
    Console.WriteLine("  GpsProbe --list                        the COM ports Windows knows");
    Console.WriteLine("  GpsProbe --port COM5 [--seconds 15]    listen to a paired phone over Bluetooth");
    Console.WriteLine("  GpsProbe --tcp 192.168.1.7:11123       listen to the app's network feed instead");
    Console.WriteLine();
    Console.WriteLine("  --raw            print every sentence as it arrives, instead of a progress dot");
    Console.WriteLine("  --seconds <n>    how long to listen, 1-600 (default 15)");
    Console.WriteLine();
    Console.WriteLine("Exit codes: 0 a position arrived, 3 the link gave no position, 1 bad arguments,");
    Console.WriteLine("2 the port or host could not be opened.");
}

bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

string? StringArg(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

int? IntArg(string name) =>
    StringArg(name) is { } raw && int.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : null;

/// <summary>
/// A <see cref="SerialPort"/> as a <see cref="TextReader"/>, so both transports read the same way.
/// </summary>
/// <remarks>
/// <c>SerialPort.ReadLine</c> throws <see cref="TimeoutException"/> on a quiet window rather than
/// returning null, and the caller treats that as "nothing yet" rather than end of stream — which
/// is the honest reading, since a GPS that has gone quiet for two seconds has not hung up.
/// </remarks>
internal sealed class SerialPortReader(SerialPort port) : TextReader
{
    public override string? ReadLine() => port.ReadLine();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            port.Dispose();
        }

        base.Dispose(disposing);
    }
}
