using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DashDeck.Abstractions;
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

    public SyntheticTransport(
        SimulatedF150 truck,
        IClock? clock = null,
        SyntheticFaults? faults = null)
    {
        _truck = truck;
        _clock = clock ?? SystemClock.Instance;
        _faults = faults ?? SyntheticFaults.Realistic;
        _lastAdvance = _clock.UtcNow;
    }

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

    public string Description => "Synthetic 2019 F-150 (simulated data)";

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

        if (_faults.LatencyMs > 0)
        {
            await Task.Delay(_faults.LatencyMs, ct).ConfigureAwait(false);
        }

        var trimmed = command.Trim().ToUpperInvariant();

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
            !byte.TryParse(command.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode) ||
            !byte.TryParse(command.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
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

        // The broadcast reaches the engine computer, and so does addressing it as 7E0. Every
        // other module ignores modes 01 and 09.
        if (_header is not null && !(_header == 0x7E0 && _bus == CanBus.Hs))
        {
            return "NO DATA\r\r>";
        }

        if (mode == 0x09 && pid == 0x02 && _bus == CanBus.Hs)
        {
            return VinResponse;
        }

        if (mode != 0x01)
        {
            return "NO DATA\r\r>";
        }

        // Mode 01 support bitmaps. A real ECU answers these, and they are how the vehicle
        // tells us which PIDs it implements rather than us assuming.
        if (pid is 0x00 or 0x20 or 0x40 or 0x60 or 0x80 or 0xA0)
        {
            if (_bus == CanBus.Ms)
            {
                // Ford's body-module PIDs are manufacturer-specific and are not advertised
                // in the standard support bitmaps. A scan of MS-CAN finding nothing is the
                // truthful answer, and is why those signals need discovering by other means.
                return "NO DATA\r\r>";
            }

            var bitmap = BuildSupportBitmap(pid);
            return bitmap is null ? "NO DATA\r\r>" : Respond(mode, pid, bitmap);
        }

        // Bus decides what is reachable: the powertrain PIDs live on HS-CAN, the body-module
        // TPMS placeholders on MS-CAN. Asking for one on the wrong bus gets NO DATA, which is
        // exactly what a real adapter switched to the wrong bus would say.
        var payload = _bus == CanBus.Ms ? EncodeMsPid(pid) : EncodePid(pid);
        return payload is null
            ? "NO DATA\r\r>"
            : Respond(mode, pid, payload);
    }

    /// <summary>
    /// The synthetic truck's VIN. Shaped like a 2019 F-150's, but serial <c>000000</c> is
    /// never issued, so it belongs to no real vehicle — the repository must never hold a real
    /// VIN (CLAUDE.md). The check digit is correct, so it exercises the same path a real one does.
    /// </summary>
    public const string SyntheticVin = "1FTEW1EP2KF000000";

    /// <summary>
    /// Mode 09 PID 02 the way an ELM327 with spaces off prints a multi-frame reply: a byte count
    /// (0x14 — 49 02 01 and seventeen characters), then ISO-TP frames whose index is glued to
    /// the data. The VIN is the first reply DashDeck reads that does not fit one CAN frame, so
    /// this is what keeps the parser honest about them (ADR-0033).
    /// </summary>
    private static string VinResponse
    {
        get
        {
            var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(SyntheticVin));
            return $"014\r0:490201{hex[..6]}\r1:{hex[6..20]}\r2:{hex[20..]}\r\r>";
        }
    }

    /// <summary>
    /// The HS-CAN mode 01 PIDs this synthetic ECU answers.
    /// </summary>
    /// <remarks>
    /// Declared explicitly rather than derived from <see cref="EncodePid"/>, because
    /// probing that method to build a bitmap would consume random numbers and break the
    /// determinism that makes scripted drives usable as fixtures. A test asserts this list
    /// matches what <see cref="EncodePid"/> actually implements, so drift fails CI instead
    /// of going unnoticed.
    /// </remarks>
    public static readonly byte[] SupportedHsPids =
    [
        0x04, 0x05, 0x06, 0x07, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x1F, 0x23, 0x2C, 0x2E, 0x2F, 0x30, 0x31, 0x33,
        0x3C, 0x42, 0x43, 0x45, 0x46, 0x47, 0x49, 0x4A, 0x4C, 0x52,
        0x5C, 0x5E, 0x61, 0x62, 0x63,

        // The check-engine light and code count, and the odometer (ADR-0041).
        0x01, 0xA6,

        // Answered but not in the shipped catalog — what a Settings ▸ Sensors scan turns up
        // as missing (ADR-0032).
        0x08, 0x09, 0x21, 0x3D, 0x44,
    ];

    /// <summary>True when <see cref="EncodePid"/> has an implementation for this PID.</summary>
    internal bool ImplementsHsPid(byte pid) => EncodePid(pid) is not null;

    /// <summary>
    /// Build the four-byte support bitmap for a range, the way an ECU does.
    /// </summary>
    /// <remarks>
    /// Bit 0 is the most significant bit of the first byte and means
    /// <c>basePid + 1</c>. The last bit of the range doubles as "the next range exists",
    /// which is how a scanner knows whether to keep walking.
    /// </remarks>
    private static byte[]? BuildSupportBitmap(byte basePid)
    {
        var supported = new HashSet<byte>(SupportedHsPids);
        var anyBeyond = SupportedHsPids.Any(p => p > basePid + 0x20);

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
                : supported.Contains((byte)pid);

            if (isSupported)
            {
                bitmap[index / 8] |= (byte)(1 << (7 - (index % 8)));
            }
        }

        return bitmap;
    }

    /// <summary>Encode the model's state the way a real ECU would, with the same quantisation.</summary>
    private byte[]? EncodePid(byte pid) => pid switch
    {
        0x04 => [Scale255(_truck.EngineLoadPercent)],
        0x05 => [Temp(_truck.CoolantTempC)],
        0x0C => TwoByte((ushort)Math.Clamp(_truck.Jitter(_truck.Rpm, 8) * 4, 0, 65535)),
        0x0D => [(byte)Math.Clamp(Math.Round(_truck.SpeedKph), 0, 255)],
        0x0F => [Temp(_truck.IntakeAirTempC)],
        0x01 => [(byte)((_truck.CheckEngine ? 0x80 : 0) | Math.Min(_truck.StoredCodes, 0x7F)), 0x07, 0xE5, 0x00],  // monitor status
        0xA6 => FourByte((uint)Math.Clamp(Math.Round(_truck.OdometerKm * 10), 0, uint.MaxValue)),            // odometer, 0.1 km
        0x10 => TwoByte((ushort)Math.Clamp(_truck.Jitter(_truck.MafGramsPerSecond, 0.3) * 100, 0, 65535)),
        0x11 => [Scale255(_truck.ThrottlePercent)],
        0x1F => TwoByte((ushort)Math.Clamp(_truck.RunTimeSeconds, 0, 65535)),
        0x2F => [Scale255(_truck.FuelLevelPercent)],
        0x46 => [Temp(_truck.AmbientTempC)],
        0x5E => TwoByte((ushort)Math.Clamp(_truck.FuelRateLitresPerHour * 20, 0, 65535)),

        // The wider standard set, derived from the same model so the extra widget options are
        // live on the synthetic truck rather than blank. Plausible, not claimed exact.
        0x43 => TwoByte((ushort)Math.Clamp(_truck.EngineLoadPercent * 2.5 * 2.55, 0, 65535)),   // absolute load
        0x0E => [(byte)Math.Clamp(Math.Round((10 + (_truck.EnginePowerKw * 0.3) + 64) * 2), 0, 255)], // timing advance
        0x30 => [8],                                                                            // warm-ups
        0x61 => [(byte)Math.Clamp(Math.Round(_truck.EngineLoadPercent + 125), 0, 255)],         // driver demand torque
        0x62 => [(byte)Math.Clamp(Math.Round((_truck.EngineLoadPercent * 0.9) + 125), 0, 255)], // actual torque
        0x63 => TwoByte(542),                                                                   // reference torque
        0x45 => [Scale255(_truck.ThrottlePercent)],                                             // relative throttle
        0x47 => [Scale255(_truck.ThrottlePercent)],                                             // throttle B
        0x49 => [Scale255(_truck.ThrottlePercent)],                                             // accel pedal D
        0x4A => [Scale255(_truck.ThrottlePercent)],                                             // accel pedal E
        0x4C => [Scale255(_truck.ThrottlePercent)],                                             // commanded throttle
        0x0B => [(byte)Math.Clamp(Math.Round(30 + (_truck.EnginePowerKw * 2.2)), 0, 255)],      // intake manifold pressure
        0x33 => [101],                                                                          // barometric pressure
        0x5C => [Temp(_truck.CoolantTempC - 3)],                                                // oil temperature
        0x3C => TwoByte((ushort)Math.Clamp((250 + (_truck.EnginePowerKw * 3) + 40) * 10, 0, 65535)), // catalyst temp
        0x0A => [127],                                                                          // fuel pressure ~381 kPa
        0x23 => TwoByte(3800),                                                                  // fuel rail gauge ~38 MPa
        0x06 => [(byte)Math.Clamp(Math.Round((_truck.Jitter(0, 3) + 100) / 0.78125), 0, 255)],  // short-term fuel trim
        0x07 => [(byte)Math.Clamp(Math.Round((-2.5 + 100) / 0.78125), 0, 255)],                 // long-term fuel trim
        0x52 => [Scale255(10)],                                                                 // ethanol %
        0x31 => TwoByte((ushort)Math.Clamp(1240 + _truck.DistanceKm, 0, 65535)),                // distance since clear
        0x2C => [Scale255(_truck.EnginePowerKw > 5 ? 8 : 0)],                                    // commanded EGR
        0x2E => [Scale255(Math.Clamp(_truck.Jitter(6, 6), 0, 100))],                            // evap purge
        0x42 => TwoByte((ushort)Math.Clamp((_truck.SpeedKph > 0 ? 14.2 : 12.6) * 1000, 0, 65535)), // control module voltage

        // Answered, but deliberately absent from the shipped catalog: the 2.7 EcoBoost is a V6
        // with two banks, so a real one reports bank 2 as well. These are what a supported-PID
        // scan of the synthetic truck turns up as missing (ADR-0032).
        0x08 => [(byte)Math.Clamp(Math.Round((_truck.Jitter(0, 3) + 100) / 0.78125), 0, 255)],  // short-term fuel trim, bank 2
        0x09 => [(byte)Math.Clamp(Math.Round((-1.6 + 100) / 0.78125), 0, 255)],                 // long-term fuel trim, bank 2
        0x3D => TwoByte((ushort)Math.Clamp((245 + (_truck.EnginePowerKw * 3) + 40) * 10, 0, 65535)), // catalyst temp, bank 2
        0x44 => TwoByte((ushort)Math.Clamp(Math.Round(_truck.Jitter(1.0, 0.02) * 32768), 0, 65535)), // commanded lambda
        0x21 => TwoByte(0),                                                                     // distance with MIL on

        _ => null,
    };

    /// <summary>
    /// MS-CAN body-module PIDs: the per-wheel TPMS placeholders, and the climate ones (ADR-0040).
    /// </summary>
    /// <remarks>
    /// 0.25 psi per count, with a touch of jitter so the readout is never suspiciously still.
    /// The rear left comes back low on purpose, so the overhead view has a corner to light.
    /// </remarks>
    private static byte HalfDegree(double celsius) => (byte)Math.Clamp(Math.Round(celsius * 2), 0, 255);

    private byte[]? EncodeMsPid(byte pid) => pid switch
    {
        0xC0 => [Psi(_truck.Jitter(_truck.TirePsiFrontLeft, 0.1))],
        0xC1 => [Psi(_truck.Jitter(_truck.TirePsiFrontRight, 0.1))],
        0xC2 => [Psi(_truck.Jitter(_truck.TirePsiRearLeft, 0.1))],
        0xC3 => [Psi(_truck.Jitter(_truck.TirePsiRearRight, 0.1))],

        // Climate placeholders (ADR-0040; see the catalog for each encoding).
        0xC4 => [HalfDegree(_truck.DriverSetTempC)],
        0xC5 => [HalfDegree(_truck.PassengerSetTempC)],
        0xC6 => [(byte)Math.Clamp(Math.Round((_truck.CabinTempC + 40) * 2), 0, 255)],
        0xC7 => [(byte)_truck.FanSpeed],
        0xC8 => [_truck.AirConditioning ? (byte)1 : (byte)0],
        0xC9 => [_truck.AutoMode ? (byte)1 : (byte)0],
        0xCA => [_truck.Recirculate ? (byte)1 : (byte)0],
        0xCB => [_truck.FrontDefrost ? (byte)1 : (byte)0],
        0xCC => [_truck.RearDefrost ? (byte)1 : (byte)0],
        0xCD => [(byte)_truck.Airflow],
        0xCE => [unchecked((byte)(sbyte)_truck.DriverSeat)],
        0xCF => [unchecked((byte)(sbyte)_truck.PassengerSeat)],
        0xD0 => [_truck.SteeringWheelHeat ? (byte)1 : (byte)0],

        // Warning-light placeholders (ADR-0041): all off on the synthetic truck.
        0xD1 or 0xD2 or 0xD3 or 0xD4 or 0xD5 => [0],

        // Economy and range placeholders (ADR-0041), from the synthetic engine's own fuel rate.
        0xD6 => TwoByte((ushort)Math.Clamp(Math.Round(_truck.EconomyL100 * 10), 0, 999)),
        0xD7 => TwoByte((ushort)Math.Clamp(Math.Round(_truck.RangeKm), 0, 2000)),
        _ => null,
    };

    /// <summary>
    /// The synthetic truck's modules, by bus and address, and the identifiers each answers.
    /// </summary>
    /// <remarks>
    /// Enough for the module sweep (ADR-0035) to find something on each bus and for the
    /// identifier sweep to find something in a module: part numbers at <c>F113</c>, the
    /// engine's VIN at <c>F190</c>, a couple of live values on the body module. The part
    /// numbers say SYNTH, and the identifiers in <c>4xxx</c> are invented — none of this is a
    /// claim about a real Ford, which is the line the vehicle packs hold (ADR-0033). The gateway
    /// declines <c>F113</c>, so the "it is there but would not say" path runs too.
    /// </remarks>
    private Dictionary<ushort, Func<byte[]>>? ModuleAt(CanBus bus, ushort address) => (bus, address) switch
    {
        (CanBus.Hs, 0x7E0) => new()
        {
            [0xF113] = () => Ascii("SYNTH-PCM-14C204-AA"),
            [0xF188] = () => Ascii("SYNTH-STRATEGY-01"),
            [0xF190] = () => Ascii(SyntheticVin),

            // Invented, for the ID hunter's follow path (ADR-0044): oil temperature (one byte, less
            // 40), transmission temperature (two bytes, sixteenths of a degree, less 40) and fuel
            // flow (two bytes, hundredths of a litre an hour).
            [0x4101] = () => [Temp(_truck.Jitter(_truck.OilTempC, 0.2))],
            [0x4102] = () => TwoByte((ushort)Math.Round((_truck.TransmissionTempC + 40) * 16)),
            [0x4103] = () => TwoByte((ushort)Math.Round(_truck.Jitter(_truck.FuelRateLitresPerHour, 0.02) * 100)),
            [0x4104] = () => [(byte)_truck.Random.Next(256)],
        },
        (CanBus.Hs, 0x7E1) => new() { [0xF113] = () => Ascii("SYNTH-TCM-7J104-AB") },
        (CanBus.Hs, 0x760) => new() { [0xF113] = () => Ascii("SYNTH-ABS-2C219-AC") },
        (CanBus.Hs, 0x730) => new() { [0xF113] = () => Ascii("SYNTH-PSCM-3F964-AA") },
        (CanBus.Hs, 0x716) => new(),
        (CanBus.Ms, 0x726) => new()
        {
            [0xF113] = () => Ascii("SYNTH-BCM-14B476-AD"),

            // Invented: battery voltage in tenths of a volt, and an ambient temperature.
            [0x4001] = () => TwoByte((ushort)Math.Round(_truck.Jitter(141, 1))),
            [0x4002] = () => [Temp(_truck.AmbientTempC)],

            // Invented tyre pressures, a quarter psi a count, for the ID hunter's match path.
            [0x4301] = () => [Psi(_truck.TirePsiFrontLeft)],
            [0x4302] = () => [Psi(_truck.TirePsiFrontRight)],
            [0x4303] = () => [Psi(_truck.TirePsiRearLeft)],
            [0x4304] = () => [Psi(_truck.TirePsiRearRight)],
        },
        (CanBus.Ms, 0x720) => new()
        {
            [0xF113] = () => Ascii("SYNTH-IPC-10849-AE"),

            // Invented: distance to empty in km, and economy in tenths of a litre per 100 km.
            [0x4201] = () => TwoByte((ushort)Math.Round(_truck.RangeKm)),
            [0x4202] = () => TwoByte((ushort)Math.Round(Math.Max(_truck.EconomyL100, 13.4) * 10)),
        },
        (CanBus.Ms, 0x733) => new()
        {
            [0xF113] = () => Ascii("SYNTH-HVAC-18C612-AA"),

            // Invented: the driver seat's level as the module holds it — heat in the low nibble,
            // cooling in the high — for the ID hunter's ask-while-you-do-it path, and a counter
            // beside it that a ranker must not take for it.
            [0x4401] = () => [Seat(_truck.Cabin.DriverSeat)],
            [0x4402] = () => [(byte)_truck.Random.Next(256)],
        },
        _ => null,
    };

    /// <summary>Identifiers a module has but will not give without security access.</summary>
    private static bool IsLocked(ushort address, ushort did) => address == 0x726 && did == 0x4003;

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

        if (ModuleAt(_bus, address) is not { } module)
        {
            return "NO DATA\r\r>";
        }

        if (IsLocked(address, did))
        {
            return "7F2233\r\r>";
        }

        return module.TryGetValue(did, out var read)
            ? Frames([0x62, (byte)(did >> 8), (byte)did, .. read()])
            : "7F2231\r\r>";
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

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

        var last = new Dictionary<uint, DateTimeOffset>();
        byte counter = 0;
        var sent = 0;
        var lastPassenger = (bool?)null;

        while (!ct.IsCancellationRequested && !_unplugged)
        {
            AdvanceModel();
            var now = _clock.UtcNow;
            var lines = new List<string>();

            bool Due(uint id, int periodMs)
            {
                if (last.TryGetValue(id, out var at) && (now - at).TotalMilliseconds < periodMs)
                {
                    return false;
                }

                last[id] = now;
                return true;
            }

            void Emit(uint id, params byte[] data)
            {
                if (_receiveFilter is null || _receiveFilter == id)
                {
                    lines.Add($"{id:X3} {string.Join(' ', data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))}");
                }
            }

            var cabin = _truck.Cabin;

            if (Pins311Mismatched)
            {
                // The wrong rate: silence.
            }
            else if (_bus == CanBus.Ms)
            {
                if (Due(0x3B3, 100))
                {
                    var doors = (byte)((cabin.DriverDoorOpen ? 1 : 0) | (cabin.PassengerDoorOpen ? 2 : 0));
                    Emit(0x3B3, doors, 0x40, Seat(cabin.DriverSeat), Seat(cabin.PassengerSeat),
                        (byte)(cabin.WheelHeat ? 1 : 0), 0x00, 0x00, 0x00);
                }

                if (Due(0x3C1, 200))
                {
                    var flags = (byte)((cabin.AirConditioning ? 1 : 0) | (cabin.Recirculate ? 2 : 0) |
                                       (cabin.RearDefrost ? 4 : 0) | (cabin.Auto ? 8 : 0));
                    Emit(0x3C1, (byte)cabin.Fan, flags, (byte)Math.Round(cabin.DriverSetTempC * 2), 0x03);
                }

                if (Due(0x42F, 500))
                {
                    Emit(0x42F, (byte)((cabin.DriverSeatbeltBuckled ? 0 : 1) | (cabin.ParkingBrake ? 2 : 0)), 0x00);
                }

                // Sent only when it changes: the passenger door's own frame.
                if (lastPassenger != cabin.PassengerDoorOpen)
                {
                    lastPassenger = cabin.PassengerDoorOpen;
                    Emit(0x3D5, (byte)(cabin.PassengerDoorOpen ? 0x10 : 0x00));
                }

                if (Due(0x4A0, 50))
                {
                    counter = (byte)((counter + 1) & 0x0F);
                    var noise = (byte)_truck.Random.Next(256);
                    Emit(0x4A0, counter, noise, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(counter ^ noise));
                }
            }
            else
            {
                if (Due(0x201, 20))
                {
                    var rpm = (ushort)Math.Round(_truck.Rpm * 4);
                    var speed = (ushort)Math.Round(_truck.SpeedKph * 100);
                    Emit(0x201, (byte)(rpm >> 8), (byte)rpm, 0x00, 0x00, (byte)(speed >> 8), (byte)speed, 0x00, 0x00);
                }

                if (Due(0x420, 100))
                {
                    Emit(0x420, Temp(_truck.CoolantTempC), 0x00, 0x00);
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

    /// <summary>A seat's level as one byte: heat in the low nibble, cooling in the high one.</summary>
    private static byte Seat(int level) => level >= 0 ? (byte)level : (byte)(-level << 4);

    private static byte Temp(double celsius) => (byte)Math.Clamp(Math.Round(celsius + 40), 0, 255);

    private static byte Scale255(double percent) => (byte)Math.Clamp(Math.Round(percent * 255.0 / 100.0), 0, 255);

    /// <summary>Encode psi at the catalog's 0.25 psi per count.</summary>
    private static byte Psi(double psi) => (byte)Math.Clamp(Math.Round(psi * 4.0), 0, 255);

    private static byte[] TwoByte(ushort value) => [(byte)(value >> 8), (byte)(value & 0xFF)];

    private static byte[] FourByte(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF)];

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
