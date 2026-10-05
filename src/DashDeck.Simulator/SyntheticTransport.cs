using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Vehicle;

namespace DashDeck.Simulator;

/// <summary>
/// A simulated 2019 F-150 that answers in real ELM327 ASCII.
/// </summary>
/// <remarks>
/// It would be far easier to fake this one layer higher and hand back decoded values. That
/// would also leave <c>ElmResponseParser</c>, bus switching and the adapter's whole
/// protocol untested until the first drive. Speaking the wire format means the real parser
/// is exercised on every mock run.
/// <para>
/// It also misbehaves on purpose: per-request latency, occasional dropped responses, and a
/// hard request ceiling matching the pessimistic Bluetooth-era figure. Components must hit
/// the same walls in simulation that they will hit in the truck.
/// </para>
/// <para>
/// <b>It holds no identifier of its own</b> (ADR-0052). A request is answered from the signal
/// catalog — whichever signal the catalog says lives at that request, encoded by the catalog's
/// own decode from the truck's quantity of the same name — and from the SAE reference table for
/// standard PIDs the catalog lacks. Its invented modules, identifiers, broadcast frames and VIN
/// are a data file (<see cref="SyntheticTruckData"/>). Placeholders are never asked for at all.
/// </para>
/// </remarks>
public sealed class SyntheticTransport : IStreamingTransport
{
    private readonly SimulatedF150 _truck;
    private readonly IClock _clock;
    private readonly SyntheticFaults _faults;

    private DateTimeOffset _lastAdvance;
    private CanBus _bus = CanBus.Hs;

    /// <summary>The module requests are addressed to, or null for the broadcast (ATSH).</summary>
    private ushort? _header;

    private readonly SyntheticTruckData _data;
    private readonly List<Answer> _answers;

    /// <param name="truck">The model.</param>
    /// <param name="clock">Time, for advancing the model.</param>
    /// <param name="faults">How badly the adapter behaves.</param>
    /// <param name="catalog">Where each signal lives and how it decodes; the shipped standard catalog if null.</param>
    /// <param name="reference">The standard PIDs the catalog lacks; the shipped reference if null.</param>
    /// <param name="data">The invented modules, frames and VIN; the shipped data file if null.</param>
    public SyntheticTransport(
        SimulatedF150 truck,
        IClock? clock = null,
        SyntheticFaults? faults = null,
        SignalCatalog? catalog = null,
        ObdReference? reference = null,
        SyntheticTruckData? data = null)
    {
        _truck = truck;
        _clock = clock ?? SystemClock.Instance;
        _faults = faults ?? SyntheticFaults.Realistic;
        _lastAdvance = _clock.UtcNow;
        _data = data ?? SyntheticTruckData.Shipped();

        reference ??= ShippedReference();
        IdentityDid = reference.IdentityDid;
        _answers = BuildAnswers(catalog ?? ShippedCatalog(), reference, truck);
    }

    /// <summary>
    /// The identifier a module answers its identity at — the question the scan asks, so the desk
    /// behaves like the cab whichever the user's vehicle file chooses.
    /// </summary>
    public ushort IdentityDid { get; set; }

    private bool _unplugged;

    /// <summary>The identifier the monitor passes alone (ATCRA), or null for all.</summary>
    private uint? _receiveFilter;

    /// <summary>The rate the adapter has pins 3 and 11 at: 125 kbit/s after STP53, or what STPBR set.</summary>
    private int _pins311Rate = 125000;

    /// <summary>
    /// The rate the synthetic truck's bus on pins 3 and 11 runs at. 125 kbit/s (MS-CAN) unless a test
    /// says otherwise. When the adapter is set to another rate it hears nothing there, and a request
    /// sent there is a CAN ERROR — what a real adapter at the wrong rate reports.
    /// </summary>
    public int Pins311BitRate { get; set; } = 125000;

    /// <summary>
    /// When set, the monitor says BUFFER FULL and stops after this many frames, the way a real
    /// adapter does when a busy bus outruns its serial link.
    /// </summary>
    public int? MonitorBufferFrames { get; set; }

    private bool Pins311Mismatched => _bus == CanBus.Ms && _pins311Rate != Pins311BitRate;

    public TransportState State { get; private set; } = TransportState.Disconnected;

    public string Description => "Synthetic truck (simulated data)";

    public event Action<TransportState>? StateChanged;

    /// <summary>Requests served, for measuring what the plan actually achieved.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Requests deliberately dropped, so tests can assert the fault path runs.</summary>
    public int DroppedCount { get; private set; }

    public SimulatedF150 Truck => _truck;

    public Task ConnectAsync(CancellationToken ct)
    {
        State = TransportState.Connected;
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pull the cable. Every subsequent exchange fails the way a yanked USB serial port
    /// does, until <see cref="Replug"/>.
    /// </summary>
    /// <remarks>
    /// Not a curiosity. Constraint C5 says connect, disconnect, sleep and resume must all be
    /// non-events that recover on their own, and until there was a way to unplug the
    /// synthetic truck there was no way to test that claim — or to see what the dash looks
    /// like when readings stop, which is the state the quality flags exist for.
    /// </remarks>
    public void Unplug()
    {
        _unplugged = true;
        State = TransportState.Disconnected;
        StateChanged?.Invoke(State);
    }

    /// <summary>Plug it back in. Exchanges succeed again from the next request.</summary>
    public void Replug()
    {
        _unplugged = false;
        State = TransportState.Connected;
        StateChanged?.Invoke(State);
    }

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        if (_unplugged)
        {
            // A real serial port throws once the device is gone. It does not politely
            // answer NO DATA, and the difference matters: NO DATA is the vehicle declining
            // to answer, this is the link being absent.
            throw new IOException("The synthetic adapter is unplugged.");
        }

        AdvanceModel();

        var trimmed = command.Trim().ToUpperInvariant();

        // A request with a response count, listened for from the engine computer alone, comes back
        // as soon as its one answer does — the adapter stops waiting for other modules (ADR-0049).
        // FORScan measured this on the real truck at about 20 ms against the broadcast's 52.
        var counted = IsCountedRequest(trimmed) && _receiveFilter == 0x7E8;
        var latency = counted ? Math.Min(_faults.LatencyMs, FastLatencyMs) : _faults.LatencyMs;

        if (latency > 0)
        {
            await Task.Delay(latency, ct).ConfigureAwait(false);
        }

        if (counted)
        {
            // The count digit is the adapter's business, not the vehicle's.
            trimmed = trimmed[..^1];
        }

        if (trimmed.StartsWith("AT", StringComparison.Ordinal) ||
            trimmed.StartsWith("ST", StringComparison.Ordinal))
        {
            return HandleControl(trimmed);
        }

        RequestCount++;

        if (Pins311Mismatched)
        {
            return "CAN ERROR\r\r>";
        }

        if (_truck.Random.NextDouble() < _faults.DropProbability)
        {
            DroppedCount++;
            return "NO DATA\r\r>";
        }

        return HandlePid(trimmed);
    }

    /// <summary>The round trip of a counted, filtered request on the real truck (ADR-0049).</summary>
    public const int FastLatencyMs = 20;

    /// <summary>A hex request followed by a single response-count digit: <c>010C1</c>.</summary>
    private static bool IsCountedRequest(string command) =>
        command.Length is 5 or 7
        && !command.StartsWith("AT", StringComparison.Ordinal)
        && !command.StartsWith("ST", StringComparison.Ordinal)
        && command.All(Uri.IsHexDigit)
        && command[^1] is >= '1' and <= '9';

    /// <summary>Step the truck by however much wall-clock time has passed.</summary>
    private void AdvanceModel()
    {
        var now = _clock.UtcNow;
        _truck.Advance(now - _lastAdvance);
        _lastAdvance = now;
    }

    private string HandleControl(string command)
    {
        // ATSH sets the request header. ATCRA and the flow-control commands only shape what the
        // adapter listens for, which a simulator answering by address already gets right.
        if (command.StartsWith("ATSH", StringComparison.Ordinal))
        {
            if (!ushort.TryParse(command.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var header))
            {
                return "?\r\r>";
            }

            _header = header == PidRequest.Broadcast ? null : header;
            return "OK\r\r>";
        }

        if (command == "ATZ")
        {
            _header = null;
            _bus = CanBus.Hs;
            _receiveFilter = null;
            _pins311Rate = 125000;
        }

        if (command.StartsWith("STPBR", StringComparison.Ordinal))
        {
            if (!int.TryParse(command.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate))
            {
                return "?\r\r>";
            }

            _pins311Rate = rate;
            return "OK\r\r>";
        }

        // The receive filter only matters to the monitor: requests are answered by address.
        if (command == "ATAR")
        {
            _receiveFilter = null;
        }
        else if (command.StartsWith("ATCRA", StringComparison.Ordinal))
        {
            _receiveFilter = command.Length > 5 &&
                uint.TryParse(command.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var filter)
                ? filter
                : null;
        }

        return HandleFixedControl(command);
    }

    private string HandleFixedControl(string command) => command switch
    {
        "ATI" => "STN2230 v5.6.6 (synthetic)\r\r>",
        "ATZ" => "\rELM327 v1.5\r\r>",
        "AT@1" => "OBDLink EX r1.0 (synthetic)\r\r>",
        "STI" => "STN2230 v5.6.6\r\r>",

        // Voltage is measured at OBD-II pin 16, which is vehicle power. On a desk the
        // adapter runs from USB and reads near zero -- a useful way to tell "not plugged
        // into the truck" from "plugged in with the ignition off".
        "ATRV" => _faults.VehiclePresent ? "14.1V\r\r>" : "0.2V\r\r>",
        "STP53" => SwitchBus(CanBus.Ms),
        "STP33" => SwitchBus(CanBus.Hs),
        _ => "OK\r\r>",
    };

    private string SwitchBus(CanBus bus)
    {
        if (!_faults.SupportsMsCan && bus == CanBus.Ms)
        {
            // What a toggle-switch or HS-only adapter looks like from software: it simply
            // rejects the command. Simulating this is how the "MS-CAN unreachable" path
            // gets tested without owning a deliberately bad adapter.
            return "?\r\r>";
        }

        _bus = bus;
        if (bus == CanBus.Ms)
        {
            _pins311Rate = 125000;
        }

        return "OK\r\r>";
    }

    private string HandlePid(string command)
    {
        if (command.Length < 4 ||
            !byte.TryParse(command.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode))
        {
            return "?\r\r>";
        }

        if (!_faults.VehiclePresent)
        {
            // What the adapter says on a desk: it hunted for a bus and found none. Control
            // commands still answer, because the adapter is powered from USB.
            return "SEARCHING...\rUNABLE TO CONNECT\r\r>";
        }

        if (mode == 0x22)
        {
            return HandleModuleRead(command);
        }

        if (!byte.TryParse(command.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
        {
            return "?\r\r>";
        }

        // Modes 01 and 09 are the engine computer's (ISO 15765-4): the broadcast reaches it, and
        // so does addressing it as 7E0. Every other module ignores them.
        if (_header is not null && !(_header == EngineRequest && _bus == CanBus.Hs))
        {
            return "NO DATA\r\r>";
        }

        if (mode == 0x09 && pid == 0x02 && _bus == CanBus.Hs)
        {
            return _data.Vin is { } vin ? VinResponse(vin) : "NO DATA\r\r>";
        }

        if (mode != 0x01)
        {
            return "NO DATA\r\r>";
        }

        // Mode 01 support bitmaps. A real ECU answers these, and they are how the vehicle tells us
        // which PIDs it implements rather than us assuming. Built from what the truck answers, so
        // they never claim a PID it does not.
        if (pid % 0x20 == 0)
        {
            var bitmap = BuildSupportBitmap(_bus, pid);
            return bitmap is null ? "NO DATA\r\r>" : Respond(mode, pid, bitmap);
        }

        var payload = Encode(_bus, null, mode, pid);
        return payload is null ? "NO DATA\r\r>" : Respond(mode, pid, payload);
    }

    /// <summary>ISO 15765-4's first emissions ECU — the engine computer.</summary>
    private const ushort EngineRequest = 0x7E0;

    /// <summary>
    /// Mode 09 PID 02 the way an ELM327 with spaces off prints a multi-frame reply: a byte count
    /// (0x14 — 49 02 01 and seventeen characters), then ISO-TP frames whose index is glued to
    /// the data. The VIN is the first reply DashDeck reads that does not fit one CAN frame, so
    /// this is what keeps the parser honest about them (ADR-0033).
    /// </summary>
    private static string VinResponse(string vin)
    {
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(vin));
        return $"014\r0:490201{hex[..6]}\r1:{hex[6..20]}\r2:{hex[20..]}\r\r>";
    }

    /// <summary>The synthetic truck's VIN, from its data file, or null.</summary>
    public string? Vin => _data.Vin;

    // ── Answering from the catalog ────────────────────────────────────────────

    /// <summary>One request the truck can answer: where it is asked, how it is encoded, and from what.</summary>
    private sealed record Answer(CanBus Bus, ushort? Module, byte Mode, ushort Pid, DecodeSpec Decode, string Name);

    private static SignalCatalog? _shippedCatalog;
    private static ObdReference? _shippedReference;

    private static SignalCatalog ShippedCatalog()
    {
        if (_shippedCatalog is not null)
        {
            return _shippedCatalog;
        }

        var path = SyntheticCatalog.Find("signals.obd2-standard.json");
        try
        {
            _shippedCatalog = path is null ? SignalCatalog.FromDefinitions([]) : SignalCatalog.FromFile(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _shippedCatalog = SignalCatalog.FromDefinitions([]);
        }

        return _shippedCatalog;
    }

    private static ObdReference ShippedReference()
    {
        if (_shippedReference is not null)
        {
            return _shippedReference;
        }

        var path = SyntheticCatalog.Find("signals.obd2-standard.json");
        _shippedReference = ObdReference.Load(path is null ? null : Path.GetDirectoryName(path)).Reference;
        return _shippedReference;
    }

    /// <summary>
    /// Every request the truck answers: the catalog's signals it has a quantity for — never a
    /// placeholder, which has no request — then the reference's standard PIDs the catalog does
    /// not cover, which is what a supported-PID scan finds "missing" (ADR-0032).
    /// </summary>
    private static List<Answer> BuildAnswers(SignalCatalog catalog, ObdReference reference, SimulatedF150 truck)
    {
        var answers = catalog.Definitions
            .Where(d => !d.Placeholder && truck.Knows(d.Id))
            .Select(d => new Answer(d.Bus, d.ModuleAddress, d.Mode, d.Pid, d.Decode, d.Id))
            .ToList();

        var covered = answers.Where(a => a.Mode == 0x01 && a.Bus == CanBus.Hs && a.Module is null)
            .Select(a => a.Pid)
            .ToHashSet();

        foreach (var entry in reference.Mode01)
        {
            if (entry.Decode is { } decode && !covered.Contains((ushort)entry.Pid) && truck.Knows(entry.Id))
            {
                answers.Add(new Answer(CanBus.Hs, null, 0x01, (ushort)entry.Pid, decode, entry.Id));
            }
        }

        return answers;
    }

    /// <summary>The mode 01 PIDs the truck answers on a bus, to the broadcast.</summary>
    public IReadOnlySet<int> SupportedPids(CanBus bus) =>
        _answers.Where(a => a.Bus == bus && a.Module is null && a.Mode == 0x01).Select(a => (int)a.Pid).ToHashSet();

    /// <summary>
    /// The payload for a request, every signal there encoded and laid over the others (two signals
    /// can share a byte under masks, as the check-engine light and the code count do); null when
    /// the truck answers nothing there.
    /// </summary>
    private byte[]? Encode(CanBus bus, ushort? module, byte mode, ushort pid)
    {
        var here = _answers.Where(a => a.Bus == bus && a.Module == module && a.Mode == mode && a.Pid == pid).ToList();
        if (here.Count == 0)
        {
            return null;
        }

        var payload = new byte[here.Max(a => a.Decode.ByteOffset + a.Decode.ByteLength)];
        foreach (var answer in here)
        {
            if (_truck.Reading(answer.Name) is not { } value)
            {
                continue;
            }

            var d = answer.Decode;
            var raw = SyntheticEncoding.Raw((value - d.Offset) / d.Scale, d.ByteLength, d.Signed);
            if (d.Mask is { } mask)
            {
                for (var i = 0; i < raw.Length; i++)
                {
                    raw[i] &= (byte)(mask >> (8 * (raw.Length - 1 - i)));
                }
            }

            for (var i = 0; i < raw.Length; i++)
            {
                payload[d.ByteOffset + i] |= raw[i];
            }
        }

        return payload;
    }

    /// <summary>
    /// Build the four-byte support bitmap for a range, the way an ECU does.
    /// </summary>
    /// <remarks>
    /// Bit 0 is the most significant bit of the first byte and means
    /// <c>basePid + 1</c>. The last bit of the range doubles as "the next range exists",
    /// which is how a scanner knows whether to keep walking.
    /// </remarks>
    private byte[]? BuildSupportBitmap(CanBus bus, byte basePid)
    {
        var supported = SupportedPids(bus);
        if (supported.Count == 0)
        {
            return null;
        }

        var anyBeyond = supported.Any(p => p > basePid + 0x20);

        if (basePid != 0x00 && !supported.Any(p => p > basePid && p <= basePid + 0x20) && !anyBeyond)
        {
            return null;
        }

        var bitmap = new byte[4];

        for (var index = 0; index < 32; index++)
        {
            var pid = basePid + 1 + index;

            var isSupported = pid == basePid + 0x20
                ? anyBeyond
                : supported.Contains(pid);

            if (isSupported)
            {
                bitmap[index / 8] |= (byte)(1 << (7 - (index % 8)));
            }
        }

        return bitmap;
    }

    // ── Modules, from the data file ───────────────────────────────────────────

    /// <summary>Mode 22 to one module: data, a negative response, or silence if nothing is there.</summary>
    private string HandleModuleRead(string command)
    {
        if (_header is not { } address ||
            command.Length != 6 ||
            !ushort.TryParse(command.AsSpan(2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var did))
        {
            // Mode 22 is not a broadcast service: nothing answers 7DF.
            return "NO DATA\r\r>";
        }

        // A catalog signal that names this module first: the truck answers what the catalog says.
        if (Encode(_bus, address, 0x22, did) is { } fromCatalog)
        {
            return Frames([0x62, (byte)(did >> 8), (byte)did, .. fromCatalog]);
        }

        if (_data.ModuleAt(_bus, address) is not { } module)
        {
            return "NO DATA\r\r>";
        }

        if (module.Locked.Any(l => SyntheticTruckData.Hex(l) == did))
        {
            return "7F2233\r\r>";
        }

        if (did == IdentityDid)
        {
            return module.Identity is { } identity
                ? Frames([0x62, (byte)(did >> 8), (byte)did, .. SyntheticEncoding.Ascii(identity)])
                : "7F2231\r\r>";
        }

        var identifier = module.Identifiers.FirstOrDefault(i => SyntheticTruckData.Hex(i.Did) == did);
        byte[]? data = identifier switch
        {
            null => null,
            { Text: "$vin" } => _data.Vin is { } vin ? SyntheticEncoding.Ascii(vin) : null,
            { Text: { } text } => SyntheticEncoding.Ascii(text),
            _ => SyntheticEncoding.Encode(identifier, _truck),
        };

        return data is null ? "7F2231\r\r>" : Frames([0x62, (byte)(did >> 8), (byte)did, .. data]);
    }

    /// <summary>
    /// Print a reply the way an ELM327 with spaces off does: one line when it fits a CAN frame,
    /// otherwise a byte count and ISO-TP frames with their index glued on — six bytes in the
    /// first frame, seven in each after.
    /// </summary>
    private static string Frames(byte[] reply)
    {
        if (reply.Length <= 7)
        {
            return Convert.ToHexString(reply) + "\r\r>";
        }

        var sb = new StringBuilder();
        sb.Append(reply.Length.ToString("X3", CultureInfo.InvariantCulture)).Append('\r');
        sb.Append("0:").Append(Convert.ToHexString(reply, 0, 6)).Append('\r');

        var index = 1;
        for (var at = 6; at < reply.Length; at += 7, index++)
        {
            sb.Append((index % 16).ToString("X", CultureInfo.InvariantCulture)).Append(':')
              .Append(Convert.ToHexString(reply, at, Math.Min(7, reply.Length - at))).Append('\r');
        }

        return sb.Append("\r>").ToString();
    }

    /// <summary>
    /// Monitor mode: the synthetic truck's broadcast traffic, a line per frame as an STN prints
    /// it with headers and spaces on, until cancelled (ADR-0044).
    /// </summary>
    /// <remarks>
    /// <b>Every identifier here is invented</b> and laid out for the desk, not copied from a Ford:
    /// periodic frames carrying the cabin's switches, one sent only when it changes (so a listener
    /// has to carry a value forward), a rolling counter with a checksum (so a ranker has noise to
    /// reject), and engine speed and coolant on HS-CAN.
    /// </remarks>
    public async IAsyncEnumerable<string> StreamAsync(string command, [EnumeratorCancellation] CancellationToken ct)
    {
        var trimmed = command.Trim().ToUpperInvariant();
        if (trimmed is not ("STMA" or "ATMA" or "STM"))
        {
            yield return (await ExchangeAsync(command, ct).ConfigureAwait(false)).TrimEnd('>', '\r');
            yield break;
        }

        var frames = _data.Broadcast
            .Select(f => (Frame: f, Id: uint.TryParse(f.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : (uint?)null))
            .Where(f => f.Id is not null)
            .Select(f => new FrameState(f.Frame, f.Id!.Value))
            .ToList();
        var sent = 0;

        while (!ct.IsCancellationRequested && !_unplugged)
        {
            AdvanceModel();
            var now = _clock.UtcNow;
            var lines = new List<string>();

            if (!Pins311Mismatched)
            {
                foreach (var state in frames.Where(f => f.Frame.Bus == _bus))
                {
                    var due = state.Frame.OnChange || state.Last is not { } at || (now - at).TotalMilliseconds >= state.Frame.PeriodMs;
                    if (!due)
                    {
                        continue;
                    }

                    var data = BuildFrame(state);
                    if (state.Frame.OnChange && state.LastData is { } previous && previous.SequenceEqual(data))
                    {
                        continue;
                    }

                    state.Last = now;
                    state.LastData = data;

                    if (_receiveFilter is null || _receiveFilter == state.Id)
                    {
                        lines.Add($"{state.Id:X3} {string.Join(' ', data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))}");
                    }
                }
            }

            foreach (var line in lines)
            {
                if (MonitorBufferFrames is { } limit && ++sent > limit)
                {
                    yield return "BUFFER FULL";
                    yield break;
                }

                yield return line;
            }

            try
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>One broadcast frame's description and what the monitor last did with it.</summary>
    private sealed class FrameState(SyntheticFrame frame, uint id)
    {
        public SyntheticFrame Frame { get; } = frame;

        public uint Id { get; } = id;

        public DateTimeOffset? Last { get; set; }

        public byte[]? LastData { get; set; }

        public int Counter { get; set; }

        /// <summary>The frame's byte descriptions, read once.</summary>
        public List<(byte? Constant, SyntheticByteSource? Source)> Parts { get; } = [.. frame.Bytes.Select(Part)];

        private static (byte?, SyntheticByteSource?) Part(System.Text.Json.JsonElement element) =>
            element.ValueKind == System.Text.Json.JsonValueKind.Number
                ? ((byte)element.GetInt32(), null)
                : (null, System.Text.Json.JsonSerializer.Deserialize<SyntheticByteSource>(element, PartOptions));

        private static readonly System.Text.Json.JsonSerializerOptions PartOptions = new() { PropertyNameCaseInsensitive = true };
    }

    /// <summary>A frame's bytes now: constants, encoded values and bits, then counters, noise and XORs.</summary>
    private byte[] BuildFrame(FrameState state)
    {
        var bytes = new List<byte>();
        var xors = new List<(int At, IReadOnlyList<int> Of)>();

        foreach (var (constant, source) in state.Parts)
        {
            if (constant is { } c)
            {
                bytes.Add(c);
            }
            else if (source is { Counter: { } wrap })
            {
                state.Counter = (state.Counter + 1) & wrap;
                bytes.Add((byte)state.Counter);
            }
            else if (source is { Xor.Count: > 0 })
            {
                xors.Add((bytes.Count, source.Xor));
                bytes.Add(0);
            }
            else if (source is not null)
            {
                bytes.AddRange(SyntheticEncoding.Encode(source, _truck) ?? new byte[source.ByteLength]);
            }
        }

        foreach (var (at, of) in xors)
        {
            bytes[at] = of.Aggregate((byte)0, (acc, i) => (byte)(acc ^ (i < bytes.Count ? bytes[i] : 0)));
        }

        return [.. bytes];
    }

    /// <summary>Format a positive response exactly as an ELM327 with spaces and echo off.</summary>
    private static string Respond(byte mode, byte pid, byte[] payload)
    {
        var sb = new StringBuilder();
        sb.Append((mode + 0x40).ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(pid.ToString("X2", CultureInfo.InvariantCulture));

        foreach (var b in payload)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append("\r\r>");
        return sb.ToString();
    }

    public ValueTask DisposeAsync()
    {
        State = TransportState.Disconnected;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// How badly the synthetic adapter behaves.
/// </summary>
/// <param name="LatencyMs">Per-request round-trip delay. Real adapters are not instant.</param>
/// <param name="DropProbability">Fraction of requests answered <c>NO DATA</c> at random.</param>
/// <param name="SupportsMsCan">False simulates a toggle-switch or HS-only adapter.</param>
/// <param name="VehiclePresent">
/// False simulates the adapter powered on a desk with nothing plugged into the OBD-II
/// port: control commands answer normally, vehicle requests report UNABLE TO CONNECT.
/// </param>
public sealed record SyntheticFaults(
    int LatencyMs,
    double DropProbability,
    bool SupportsMsCan,
    bool VehiclePresent = true)
{
    /// <summary>
    /// The default, now matched to the real truck.
    /// </summary>
    /// <remarks>
    /// 52 ms is the mean round trip measured on the 2019 F-150 with an OBDLink EX on
    /// 2026-10-01 (~19 requests/second). Keeping the simulator at the vehicle's real
    /// latency is the whole point: a simulator with more headroom than the truck produces
    /// components that only fail on the road.
    /// </remarks>
    public static readonly SyntheticFaults Realistic = new(LatencyMs: 52, DropProbability: 0.02, SupportsMsCan: true);

    /// <summary>No latency and no drops. For unit tests that are not about timing.</summary>
    public static readonly SyntheticFaults Perfect = new(LatencyMs: 0, DropProbability: 0, SupportsMsCan: true);

    /// <summary>A cheap HS-CAN-only adapter, for testing the degraded-capability path.</summary>
    public static readonly SyntheticFaults HsCanOnly = new(LatencyMs: 60, DropProbability: 0.02, SupportsMsCan: false);

    /// <summary>
    /// A healthy adapter on a desk with no vehicle attached — the first bring-up stage.
    /// </summary>
    public static readonly SyntheticFaults BenchNoVehicle =
        new(LatencyMs: 20, DropProbability: 0, SupportsMsCan: true, VehiclePresent: false);
}
