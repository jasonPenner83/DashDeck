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

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
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

        // Everything modelled so far lives on HS-CAN. Asking on MS-CAN gets the honest
        // answer rather than data that would not really be there.
        if (_bus != CanBus.Hs)
        {
            return "NO DATA\r\r>";
        }

        var payload = EncodePid(pid);
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
        _ => null,
    };

    private static byte Temp(double celsius) => (byte)Math.Clamp(Math.Round(celsius + 40), 0, 255);

    private static byte Scale255(double percent) => (byte)Math.Clamp(Math.Round(percent * 255.0 / 100.0), 0, 255);

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
