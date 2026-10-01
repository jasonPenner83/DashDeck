using System.Globalization;
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
public sealed class SyntheticTransport : IVehicleTransport
{
    private readonly SimulatedF150 _truck;
    private readonly IClock _clock;
    private readonly SyntheticFaults _faults;

    private DateTimeOffset _lastAdvance;
    private CanBus _bus = CanBus.Hs;

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

    private string HandleControl(string command) => command switch
    {
        "ATI" => "STN2230 v5.6.6 (synthetic)\r\r>",
        "ATZ" => "\rELM327 v1.5\r\r>",
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

        if (mode != 0x01)
        {
            return "NO DATA\r\r>";
        }

        // Bus decides what is reachable: the powertrain PIDs live on HS-CAN, the body-module
        // TPMS placeholders on MS-CAN. Asking for one on the wrong bus gets NO DATA, which is
        // exactly what a real adapter switched to the wrong bus would say.
        // The supported-PID bitmaps (00, 20, 40 …) are answered from the same list the model
        // encodes, so a scan of the synthetic truck tells the truth about it. Only on HS-CAN:
        // the body modules on MS-CAN are not emissions ECUs and do not answer the standard
        // question — which is why Ford's own PIDs are found by asking, not by scanning.
        var payload = _bus == CanBus.Ms ? EncodeMsPid(pid)
            : pid % 0x20 == 0 ? Bitmap(pid, HsPids)
            : EncodePid(pid);

        return payload is null
            ? "NO DATA\r\r>"
            : Respond(mode, pid, payload);
    }

    /// <summary>Encode the model's state the way a real ECU would, with the same quantisation.</summary>
    private byte[]? EncodePid(byte pid) => pid switch
    {
        0x04 => [Scale255(_truck.EngineLoadPercent)],
        0x05 => [Temp(_truck.CoolantTempC)],
        0x0C => TwoByte((ushort)Math.Clamp(_truck.Jitter(_truck.Rpm, 8) * 4, 0, 65535)),
        0x0D => [(byte)Math.Clamp(Math.Round(_truck.SpeedKph), 0, 255)],
        0x0F => [Temp(_truck.IntakeAirTempC)],
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

        // Answered, but deliberately absent from the shipped catalog: the 3.5 EcoBoost is a V6
        // with two banks, so a real one reports bank 2 as well. These are what a supported-PID
        // scan of the synthetic truck turns up as missing (ADR-0030).
        0x08 => [(byte)Math.Clamp(Math.Round((_truck.Jitter(0, 3) + 100) / 0.78125), 0, 255)],  // short-term fuel trim, bank 2
        0x09 => [(byte)Math.Clamp(Math.Round((-1.6 + 100) / 0.78125), 0, 255)],                 // long-term fuel trim, bank 2
        0x3D => TwoByte((ushort)Math.Clamp((245 + (_truck.EnginePowerKw * 3) + 40) * 10, 0, 65535)), // catalyst temp, bank 2
        0x44 => TwoByte((ushort)Math.Clamp(Math.Round(_truck.Jitter(1.0, 0.02) * 32768), 0, 65535)), // commanded lambda
        0x21 => TwoByte(0),                                                                     // distance with MIL on

        _ => null,
    };

    /// <summary>
    /// Every HS-CAN mode 01 PID <see cref="EncodePid"/> answers. Kept beside it; a test scans the
    /// bitmaps and asks every PID, and holds the two answers equal.
    /// </summary>
    private static readonly IReadOnlySet<int> HsPids = new HashSet<int>
    {
        0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x1F,
        0x21, 0x23, 0x2C, 0x2E, 0x2F, 0x30, 0x31, 0x33, 0x3C, 0x3D, 0x42, 0x43, 0x44, 0x45, 0x46,
        0x47, 0x49, 0x4A, 0x4C, 0x52, 0x5C, 0x5E, 0x61, 0x62, 0x63,
    };

    /// <summary>
    /// The supported-PID bitmap for the range starting at <paramref name="basePid"/>, with the
    /// last bit set when anything further along is supported — the way an ECU chains them.
    /// </summary>
    private static byte[]? Bitmap(byte basePid, IReadOnlySet<int> supported)
    {
        // A range nobody chained to is not answered at all, as on a real ECU.
        if (basePid != 0 && !supported.Any(p => p > basePid))
        {
            return null;
        }

        var bitmap = new byte[4];

        for (var i = 0; i < 32; i++)
        {
            var pid = basePid + i + 1;
            var set = i == 31 ? supported.Any(p => p > pid) : supported.Contains(pid);

            if (set)
            {
                bitmap[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bitmap;
    }

    /// <summary>
    /// MS-CAN body-module PIDs. Only the per-wheel TPMS placeholders, for now (see catalog).
    /// </summary>
    /// <remarks>
    /// 0.25 psi per count, with a touch of jitter so the readout is never suspiciously still.
    /// The rear left comes back low on purpose, so the overhead view has a corner to light.
    /// </remarks>
    private byte[]? EncodeMsPid(byte pid) => pid switch
    {
        0xC0 => [Psi(_truck.Jitter(_truck.TirePsiFrontLeft, 0.1))],
        0xC1 => [Psi(_truck.Jitter(_truck.TirePsiFrontRight, 0.1))],
        0xC2 => [Psi(_truck.Jitter(_truck.TirePsiRearLeft, 0.1))],
        0xC3 => [Psi(_truck.Jitter(_truck.TirePsiRearRight, 0.1))],
        _ => null,
    };

    private static byte Temp(double celsius) => (byte)Math.Clamp(Math.Round(celsius + 40), 0, 255);

    private static byte Scale255(double percent) => (byte)Math.Clamp(Math.Round(percent * 255.0 / 100.0), 0, 255);

    /// <summary>Encode psi at the catalog's 0.25 psi per count.</summary>
    private static byte Psi(double psi) => (byte)Math.Clamp(Math.Round(psi * 4.0), 0, 255);

    private static byte[] TwoByte(ushort value) => [(byte)(value >> 8), (byte)(value & 0xFF)];

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
public sealed record SyntheticFaults(int LatencyMs, double DropProbability, bool SupportsMsCan)
{
    /// <summary>
    /// The default. 60 ms per request is roughly a 15 requests/second ceiling — the
    /// pessimistic Bluetooth-era figure, kept deliberately until a real USB number is
    /// measured on the truck (open question Q12).
    /// </summary>
    public static readonly SyntheticFaults Realistic = new(LatencyMs: 60, DropProbability: 0.02, SupportsMsCan: true);

    /// <summary>No latency and no drops. For unit tests that are not about timing.</summary>
    public static readonly SyntheticFaults Perfect = new(LatencyMs: 0, DropProbability: 0, SupportsMsCan: true);

    /// <summary>A cheap HS-CAN-only adapter, for testing the degraded-capability path.</summary>
    public static readonly SyntheticFaults HsCanOnly = new(LatencyMs: 60, DropProbability: 0.02, SupportsMsCan: false);
}
