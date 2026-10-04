using DashDeck.Abstractions;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.IdHunter;

/// <summary>The adapter the guide talks through: requests, the listener, and the synthetic truck when simulated.</summary>
internal sealed class HuntSession : IAsyncDisposable
{
    private HuntSession(IStreamingTransport transport, SimulatedF150? truck, string description)
    {
        Transport = transport;
        Truck = truck;
        Description = description;
        Adapter = new ElmAdapter(transport);
        Monitor = new CanMonitor(transport);
    }

    public IStreamingTransport Transport { get; }

    public ElmAdapter Adapter { get; }

    public CanMonitor Monitor { get; }

    /// <summary>The synthetic truck, or null on a real one.</summary>
    public SimulatedF150? Truck { get; }

    public string Description { get; }

    public bool Simulated => Truck is not null;

    /// <summary>
    /// The rate the bus on OBD pins 3 and 11 was heard at — 125 or 500 kbit/s — or null when nothing
    /// was heard there at either. Nothing is ever sent on pins 3/11 until this is known.
    /// </summary>
    public int? Pins311BitRate { get; private set; }

    /// <summary>The buses it is safe to send requests on: HS-CAN always, pins 3/11 once its rate is known.</summary>
    public IReadOnlyList<CanBus> RequestBuses => Pins311BitRate is null ? [CanBus.Hs] : [CanBus.Hs, CanBus.Ms];

    /// <summary>
    /// Find the rate of the bus on pins 3 and 11 by listening only — silently, so nothing is sent
    /// whichever rate is wrong. 125 kbit/s is Ford's MS-CAN; newer trucks put a 500 kbit/s bus there.
    /// </summary>
    public async Task<int?> DetectPins311Async(CancellationToken ct)
    {
        Pins311BitRate = null;

        var rate = await Monitor.DetectPins311Async(ct).ConfigureAwait(false);
        await RestoreAsync(ct).ConfigureAwait(false);

        if (rate is { } heard)
        {
            Pins311BitRate = heard;
            Adapter.Pins311BitRate = heard;
        }

        return rate;
    }

    /// <summary>What a bus is called on screen.</summary>
    public string BusName(CanBus bus) => bus == CanBus.Hs
        ? "HS-CAN"
        : Pins311BitRate is { } rate ? $"pins 3/11 ({rate / 1000} kbit/s)" : "pins 3/11";

    /// <summary>Modules found by a scan this session, by address, with their bus.</summary>
    public Dictionary<ushort, CanBus> KnownModules { get; } = [];

    public static async Task<HuntSession> OpenAsync(SerialPortTransport port, CancellationToken ct)
    {
        var session = new HuntSession(port, null, port.Description);
        await session.Adapter.InitializeAsync(ct).ConfigureAwait(false);
        return session;
    }

    public static async Task<HuntSession> SimulateAsync(CancellationToken ct)
    {
        var truck = new SimulatedF150(new ScriptedDrive("parked", "Parked with the engine running.", [new DriveSegment("parked", 36_000, 0)]));
        var transport = new SyntheticTransport(truck, faults: new SyntheticFaults(LatencyMs: 4, DropProbability: 0.01, SupportsMsCan: true));
        var session = new HuntSession(transport, truck, "SIMULATED truck (invented identifiers)");
        await session.Adapter.InitializeAsync(ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>A session on any transport — the tests' way to put the synthetic truck on the road.</summary>
    internal static async Task<HuntSession> ForAsync(IStreamingTransport transport, SimulatedF150? truck, CancellationToken ct)
    {
        var session = new HuntSession(transport, truck, transport.Description);
        await session.Adapter.InitializeAsync(ct).ConfigureAwait(false);
        return session;
    }

    public Task<PidResponse> RequestAsync(PidRequest request, CancellationToken ct) => Adapter.RequestAsync(request, ct);

    /// <summary>Put the adapter back the way requests need it, after listening.</summary>
    /// <remarks>
    /// The reset leaves the adapter to open the bus again on the next request, and in the truck
    /// the first requests after a listen at 500 kbit/s went unanswered. One standard request,
    /// answered or not, opens it before anything that matters is asked.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken ct)
    {
        await Adapter.InitializeAsync(ct).ConfigureAwait(false);
        await RequestAsync(new PidRequest(0x01, 0x00, CanBus.Hs), ct).ConfigureAwait(false);
    }

    /// <summary>Speed in km/h from the standard PID, or null when the truck does not answer.</summary>
    public async Task<double?> SpeedAsync(CancellationToken ct)
    {
        var response = await RequestAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), ct).ConfigureAwait(false);
        return response.IsSuccess && response.Data.Length >= 1 ? response.Data[0] : null;
    }

    /// <summary>A standard reading to compare against: <c>coolant</c> (°C) or <c>rpm</c>.</summary>
    public async Task<double?> ReferenceAsync(string name, CancellationToken ct)
    {
        var pid = name == "rpm" ? (ushort)0x0C : (ushort)0x05;
        var response = await RequestAsync(new PidRequest(0x01, pid, CanBus.Hs), ct).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return null;
        }

        var d = response.Data;
        return name == "rpm"
            ? d.Length >= 2 ? ((d[0] * 256) + d[1]) / 4.0 : null
            : d.Length >= 1 ? d[0] - 40 : null;
    }

    /// <summary>Which bus a module answers on: asked for its part number on each.</summary>
    public async Task<CanBus?> FindModuleAsync(ushort address, CancellationToken ct)
    {
        if (KnownModules.TryGetValue(address, out var known))
        {
            return known;
        }

        foreach (var bus in RequestBuses)
        {
            // Twice: one unanswered request is routine.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var response = await RequestAsync(new PidRequest(0x22, 0xF113, bus, address), ct).ConfigureAwait(false);
                if (response.ModuleAnswered)
                {
                    KnownModules[address] = bus;
                    return bus;
                }

                if (response.Failure != PidFailure.NoData && response.Failure != PidFailure.Timeout)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>Under simulation, do what a step asks.</summary>
    public void Simulate(IReadOnlyDictionary<string, double>? controls)
    {
        if (Truck is null || controls is null)
        {
            return;
        }

        foreach (var (name, value) in controls)
        {
            Truck.Cabin.Set(name, value);
        }
    }

    /// <summary>Under simulation, what the cluster would show for a match reading.</summary>
    public double? SimulatedReading(string name) => Truck is not { } t ? null : name switch
    {
        "range" => Math.Round(t.RangeKm),
        "economy" => Math.Round(Math.Max(t.EconomyL100, 13.4), 1),
        "tireFL" => Math.Round(t.TirePsiFrontLeft * 4) / 4,
        "tireFR" => Math.Round(t.TirePsiFrontRight * 4) / 4,
        "tireRL" => Math.Round(t.TirePsiRearLeft * 4) / 4,
        "tireRR" => Math.Round(t.TirePsiRearRight * 4) / 4,
        _ => null,
    };

    public ValueTask DisposeAsync() => Adapter.DisposeAsync();
}
