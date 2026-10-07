using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Vehicle.Tap;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>
/// One identifier on the truck: which bus, which module, which mode and PID.
/// </summary>
/// <param name="Header">The 11-bit request id the module was asked on — <c>7E0</c>, or <c>7DF</c> for the broadcast.</param>
public readonly record struct IdentifierKey(CanBus Bus, ushort Header, byte Mode, ushort Pid)
{
    /// <summary>The functional broadcast every emissions ECU answers.</summary>
    public const ushort Broadcast = 0x7DF;

    /// <summary>The PID as FORScan's traffic shows it: <c>1E1C</c>, or <c>05</c> for mode 01.</summary>
    public string PidText => Mode == 0x01 ? Pid.ToString("X2", CultureInfo.InvariantCulture) : Pid.ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>The module as the catalog writes it, or null for the broadcast.</summary>
    public string? ModuleText => Header == Broadcast ? null : Header.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>The bus as people say it: HS, or "3/11" for the bus on those pins (not MS on this truck, Q21).</summary>
    public string BusText => Bus == CanBus.Hs ? "HS" : "3/11";

    public override string ToString() =>
        $"{(Header == Broadcast ? "7DF" : Header.ToString("X3", CultureInfo.InvariantCulture))} {Mode:X2} {PidText} ({BusText})";
}

/// <summary>One answer heard: when, from which identifier, and the bytes after the mode and PID.</summary>
public sealed record Observation(DateTimeOffset At, IdentifierKey Key, byte[] Payload);

/// <summary>
/// Reads the traffic between FORScan and the adapter, as the tap records it, and turns each
/// answer into an <see cref="Observation"/> of the identifier it answers (ADR-0050).
/// </summary>
/// <remarks>
/// <b>It follows the adapter's state the way the adapter does.</b> A request on its own says
/// "22 1E1C"; which module heard it depends on the last <c>ATSH</c>, and which bus on the last
/// protocol command. So the reader keeps the same three things the adapter keeps, from what was
/// actually sent, and stamps every request with them. What FORScan sends, measured with the serial
/// tap on 2026-10-04:
/// <list type="bullet">
/// <item><c>ATSH0007E0</c> — a six-digit header, the module id in the last three;</item>
/// <item><c>ATTP6</c> for the main bus; <c>STP53</c> (then <c>STPBR500000</c>) for pins 3/11;</item>
/// <item><c>221E1C1</c> — a request with a response count on the end;</item>
/// <item><c>STPXd:22F113,r:1</c> — the STN's own request command, data in <c>d:</c>;</item>
/// <item>answers with headers and spaces off: <c>621E1C00F3</c>, ended by the prompt.</item>
/// </list>
/// Anything else — DTC reads, voltage, settings — passes by without an observation.
/// </remarks>
public sealed class TrafficReader
{
    private CanBus _bus = CanBus.Hs;
    private ushort _header = IdentifierKey.Broadcast;
    private bool _headersOn;
    private IdentifierKey? _pending;
    private string _lastCommand = "";
    private DateTimeOffset _answerStarted;
    private readonly List<string> _answer = [];

    /// <summary>Raised for every positive answer to a mode 01 or mode 22 request.</summary>
    public event Action<Observation>? Observed;

    /// <summary>Requests read so far, of any kind.</summary>
    public int Requests { get; private set; }

    /// <summary>Feed one line from the tap, live or from a saved log.</summary>
    public void Feed(TapLine line)
    {
        switch (line.Direction)
        {
            case TapDirection.ToAdapter:
                Command(line.Text.Trim().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
                break;

            case TapDirection.FromAdapter:
                if (_answer.Count == 0)
                {
                    _answerStarted = line.At;
                }

                if (line.Text.Trim().Length > 0)
                {
                    _answer.Add(line.Text.Trim().ToUpperInvariant());
                }

                if (line.Prompt)
                {
                    Answer();
                }

                break;

            default:
                // A note: a connection or a disconnection. The adapter keeps its state across a
                // reconnect, so nothing is reset — only an unanswered request is abandoned.
                _pending = null;
                _answer.Clear();
                break;
        }
    }

    private void Command(string command)
    {
        _lastCommand = command;
        _answer.Clear();
        _pending = null;

        if (command.Length == 0)
        {
            return;
        }

        if (command is "ATZ" or "ATWS" or "ATD")
        {
            _bus = CanBus.Hs;
            _header = IdentifierKey.Broadcast;
            _headersOn = false;
            return;
        }

        if (command.StartsWith("ATSH", StringComparison.Ordinal))
        {
            var hex = command[4..];
            _header = hex.Length is 3 or 6 && ushort.TryParse(hex[^3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                ? id
                : (ushort)0;   // a 29-bit or unreadable header: requests on it are not identifiers we can name
            return;
        }

        if (command is "ATH1")
        {
            _headersOn = true;
            return;
        }

        if (command is "ATH0")
        {
            _headersOn = false;
            return;
        }

        if (command.StartsWith("ATTP", StringComparison.Ordinal) || command.StartsWith("ATSP", StringComparison.Ordinal) ||
            command.StartsWith("STP3", StringComparison.Ordinal))
        {
            _bus = CanBus.Hs;
            return;
        }

        if (command.StartsWith("STP5", StringComparison.Ordinal))
        {
            _bus = CanBus.Ms;
            return;
        }

        if (command.StartsWith("STPX", StringComparison.Ordinal))
        {
            Request(StpxData(command[4..]), countDigit: false);
            return;
        }

        if (command.StartsWith("AT", StringComparison.Ordinal) || command.StartsWith("ST", StringComparison.Ordinal) ||
            command.StartsWith("VT", StringComparison.Ordinal))
        {
            return;
        }

        Request(command, countDigit: command.Length % 2 == 1);
    }

    /// <summary>The <c>d:</c> part of an <c>STPX</c> command: <c>D:22F113,R:1</c> → <c>22F113</c>.</summary>
    private static string StpxData(string arguments)
    {
        foreach (var part in arguments.Split(','))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2 && kv[0].Trim() == "D")
            {
                return kv[1].Trim();
            }
        }

        return "";
    }

    private void Request(string hex, bool countDigit)
    {
        if (countDigit)
        {
            // A response count after the request: 221E1C1 is 22 1E1C asking for one answer.
            hex = hex[..^1];
        }

        if (hex.Length < 4 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
        {
            return;
        }

        Requests++;
        var mode = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        ushort pid;
        if (mode == 0x01 && hex.Length == 4)
        {
            pid = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (mode == 0x22 && hex.Length == 6)
        {
            pid = ushort.Parse(hex.AsSpan(2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            return;
        }

        if (_header == 0)
        {
            return;
        }

        _pending = new IdentifierKey(_bus, _header, mode, pid);
    }

    private void Answer()
    {
        var lines = _answer.ToList();
        _answer.Clear();

        if (_pending is not { } key)
        {
            return;
        }

        var bytes = new List<byte>();
        var framed = false;

        foreach (var raw in lines)
        {
            var line = raw.Replace(" ", "", StringComparison.Ordinal);

            if (line.Length == 0 || line == _lastCommand || line is "OK" or "?" ||
                line.Contains("NODATA", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.Ordinal) ||
                line.StartsWith("SEARCHING", StringComparison.Ordinal) || line.Contains("STOPPED", StringComparison.Ordinal))
            {
                continue;
            }

            // "Busy, answer follows": the real answer is a later line.
            if (line.Length == 6 && line.StartsWith("7F", StringComparison.Ordinal) && line.EndsWith("78", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length > 2 && line[1] == ':' && Uri.IsHexDigit(line[0]))
            {
                framed = true;
                line = line[2..];
            }
            else if (!framed && line.Length == 3 && line.All(Uri.IsHexDigit) && lines.Count > 1)
            {
                // The byte count before a multi-frame answer.
                continue;
            }
            else if (!framed && bytes.Count > 0)
            {
                // A second module's answer to a broadcast: keep the first.
                continue;
            }

            if (_headersOn && !framed && line.Length > 5)
            {
                // 11-bit header and the single-frame length byte.
                line = line[5..];
            }

            if (line.Length % 2 != 0 || !line.All(Uri.IsHexDigit))
            {
                return;
            }

            for (var i = 0; i < line.Length; i += 2)
            {
                bytes.Add(byte.Parse(line.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        var pidLength = key.Mode == 0x01 ? 1 : 2;
        if (bytes.Count <= pidLength || bytes[0] != key.Mode + 0x40)
        {
            return;
        }

        var echoed = pidLength == 1 ? bytes[1] : (bytes[1] << 8) | bytes[2];
        if (echoed != key.Pid)
        {
            return;
        }

        _pending = null;
        Observed?.Invoke(new Observation(_answerStarted, key, [.. bytes.Skip(1 + pidLength)]));
    }
}
